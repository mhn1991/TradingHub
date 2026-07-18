using Agent.Models;
using Brokers.Models;
using ChartAnnotator.Engine;
using ChartAnnotator.Models;
using LiveTrading.Actors;
using LiveTrading.Agents;
using LiveTrading.Configuration;
using LiveTrading.MarketData;
using LiveTrading.Shadow;
using LiveTrading.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using TradingCore.MarketData;
using TradingCore.Pipeline;
using System.Diagnostics;

namespace LiveTrading.Tests.Agents;

[TestFixture]
public sealed class MultiAgentCapacityTests
{
    private static readonly BarInterval M1 = BarInterval.Minutes(1);

    [Test]
    public async Task TwentyInstruments_FourAgentsAndTwoProfilesEach_CloseOneCompleteShadowEpoch()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 18, 12, 1, 0, TimeSpan.Zero));
        InstrumentKey[] instruments = Enumerable.Range(0, 20)
            .Select(index => new InstrumentKey($"TEST:INSTRUMENT-{index:00}"))
            .ToArray();
        AnalysisProfileKey[] profiles =
        [
            AnalysisProfileKey.Create(new ChartAnnotationOptions { RsiPeriod = 14 }, new HashSet<BarInterval> { M1 }, "test-schema"),
            AnalysisProfileKey.Create(new ChartAnnotationOptions { RsiPeriod = 21 }, new HashSet<BarInterval> { M1 }, "test-schema")
        ];
        var epochs = new LiveDecisionEpochCoordinator(
            instruments,
            clock,
            new LiveDecisionEpochCoordinatorOptions { BarrierTimeout = TimeSpan.FromSeconds(10) },
            NullLogger<LiveDecisionEpochCoordinator>.Instance);
        var broker = new FakeBrokerClient();
        var supervisor = new AgentSupervisor(
            broker,
            epochs,
            clock,
            NullLogger<AgentSupervisor>.Instance,
            maxConcurrentEvaluations: 8,
            evaluationTimeout: TimeSpan.FromSeconds(10));
        var runtimes = new List<RecordingRuntime>(80);

        foreach (InstrumentKey instrument in instruments)
        {
            for (int agentIndex = 0; agentIndex < 4; agentIndex++)
            {
                string strategyId = $"strategy-{agentIndex}";
                AnalysisProfileKey profile = profiles[agentIndex / 2];
                var agent = new FakeTradingAgent
                {
                    RequiredIntervals = new HashSet<BarInterval> { M1 },
                    TriggerInterval = M1,
                    Decide = context => new AgentDecision
                    {
                        DecisionId = $"observe-{instrument.Value}-{strategyId}",
                        Action = AgentAction.Observe,
                        Instrument = context.Instrument,
                        Confidence = 0m,
                        CreatedAt = context.Timestamp,
                        Reason = "capacity test",
                        ReasonCode = "CapacityTest"
                    }
                };
                var pipeline = new SafeTradingPipeline(agent, new ShadowExecutionCoordinator());
                var decisionRuntime = new StrategyDecisionRuntime
                {
                    Agent = agent,
                    Pipeline = pipeline,
                    StrategyVersion = "v1",
                    FeaturePolicyHash = profile.ProfileHash,
                    SetupCalibrationId = null,
                    MetaModelVersion = null
                };
                LiveTradingPolicyBundle policy = BuildBundle(profile.ProfileHash);
                var key = new AgentInstanceKey(
                    AgentInstanceKey.DefaultDeploymentId,
                    instrument,
                    strategyId,
                    policy.PolicyBundleId,
                    policy.Revision);
                var inner = new LiveAgentRuntime(
                    key, profile, AgentExecutionMode.Shadow, pipeline, broker);
                var runtime = new RecordingRuntime(inner);
                runtimes.Add(runtime);
                supervisor.Register(new AgentInstanceState
                {
                    Key = key,
                    Runtime = decisionRuntime,
                    IsolatedRuntime = runtime,
                    AnalysisProfile = profile,
                    PolicyBundle = policy,
                    Assignment = new LiveStrategyAssignment
                    {
                        StrategyId = strategyId,
                        PolicyBundleId = policy.PolicyBundleId.ToString("D"),
                        Mode = StrategyActivationMode.Shadow
                    }
                });
            }
        }

        DateTimeOffset availableAt = clock.GetUtcNow();
        MarketAnalysisUpdate[] updates = instruments
            .Select(instrument => BuildUpdate(instrument, profiles, availableAt))
            .ToArray();
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        int[] collectionsBefore = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
        var stopwatch = Stopwatch.StartNew();
        IReadOnlyList<AgentEvaluationAudit>[] audits = await Task.WhenAll(updates.Select(update =>
            supervisor.ApplyAsync(update, executableSpread: null, CancellationToken.None)));
        LiveDecisionEpochBatch batch = await epochs.ClosedEpochs.ReadAsync().AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10));
        stopwatch.Stop();
        double[] durations = supervisor.Instances
            .Select(instance => instance.EvaluationDurationSummary().Average)
            .Order()
            .ToArray();
        long allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        int[] collections =
        [
            GC.CollectionCount(0) - collectionsBefore[0],
            GC.CollectionCount(1) - collectionsBefore[1],
            GC.CollectionCount(2) - collectionsBefore[2]
        ];
        TestContext.Progress.WriteLine(
            $"capacity-metrics runtimes=80 epoch_ms={stopwatch.Elapsed.TotalMilliseconds:F3} " +
            $"eval_p50_ms={Percentile(durations, 0.50):F3} eval_p95_ms={Percentile(durations, 0.95):F3} " +
            $"eval_p99_ms={Percentile(durations, 0.99):F3} allocated_bytes={allocatedBytes} " +
            $"gc0={collections[0]} gc1={collections[1]} gc2={collections[2]} " +
            $"peak_mailbox={supervisor.Instances.Max(instance => instance.PeakMailboxDepth)}");

        Assert.Multiple(() =>
        {
            Assert.That(supervisor.Instances, Has.Count.EqualTo(80));
            Assert.That(audits.Sum(items => items.Count), Is.EqualTo(80));
            Assert.That(audits.SelectMany(items => items).All(item => item.Outcome == AgentEvaluationOutcome.Completed), Is.True);
            Assert.That(batch.ExpectedAgents, Has.Count.EqualTo(80));
            Assert.That(batch.CompletedAgents, Has.Count.EqualTo(80));
            Assert.That(batch.FailedAgents, Is.Empty);
            Assert.That(batch.TimedOutAgents, Is.Empty);
            Assert.That(batch.MissingAgents, Is.Empty);
            Assert.That(batch.UnavailableInstruments, Is.Empty);
            Assert.That(batch.OrderedCandidates, Is.Empty, "All 80 runtimes are shadow observers in this capacity fixture.");
            Assert.That(broker.PlaceOrderCallCount, Is.Zero);
        });

        foreach (InstrumentKey instrument in instruments)
        {
            RecordingRuntime[] assigned = runtimes.Where(runtime => runtime.Key.Instrument == instrument).ToArray();
            Assert.That(assigned, Has.Length.EqualTo(4));
            Assert.That(assigned.Select(runtime => runtime.LastSnapshot).All(snapshot => snapshot is not null), Is.True);
            Assert.That(assigned[0].LastSnapshot, Is.SameAs(assigned[1].LastSnapshot));
            Assert.That(assigned[2].LastSnapshot, Is.SameAs(assigned[3].LastSnapshot));
            Assert.That(assigned[0].LastSnapshot, Is.Not.SameAs(assigned[2].LastSnapshot));
        }

        foreach (RecordingRuntime runtime in runtimes)
            runtime.Dispose();
    }

    private static double Percentile(IReadOnlyList<double> ordered, double percentile)
    {
        int index = Math.Clamp((int)Math.Ceiling(ordered.Count * percentile) - 1, 0, ordered.Count - 1);
        return ordered[index];
    }

    private static MarketAnalysisUpdate BuildUpdate(
        InstrumentKey instrument,
        IReadOnlyList<AnalysisProfileKey> profiles,
        DateTimeOffset availableAt)
    {
        var snapshots = profiles.ToDictionary(
            profile => profile,
            profile => MarketAnalysisSnapshot.Create(
                instrument,
                profile,
                snapshotVersion: 1,
                decisionEpoch: LiveDecisionEpochCoordinator.EpochFor(availableAt),
                availableAt,
                new Dictionary<BarInterval, AnalysisSnapshot> { [M1] = BuildAnalysis(instrument, availableAt) },
                crossMarket: null,
                DataQualityResult.Valid));
        MarketAnalysisSnapshot first = snapshots[profiles[0]];
        return new MarketAnalysisUpdate
        {
            Instrument = instrument,
            AvailableAt = availableAt,
            ClosedIntervals = new HashSet<BarInterval> { M1 },
            Analysis = new MultiTimeframeAnalysis(instrument, availableAt, first.Timeframes),
            SnapshotsByProfile = snapshots,
            Health = new MarketDataHealthSnapshot
            {
                Instrument = instrument,
                State = LiveMarketState.Ready,
                AsOf = availableAt,
                IsQuoteStale = false,
                ProcessedCandleCount = 1,
                DuplicateCandleCount = 0,
                GapDetectedCount = 0,
                RecentIssues = []
            },
            MarketSequence = 1
        };
    }

    private static AnalysisSnapshot BuildAnalysis(InstrumentKey instrument, DateTimeOffset availableAt) => new()
    {
        Instrument = instrument,
        Interval = M1,
        AvailableAt = availableAt,
        Version = 1,
        LatestCandle = new Candle
        {
            Instrument = instrument,
            Interval = M1,
            OpenTime = availableAt.AddMinutes(-1),
            CloseTime = availableAt,
            Prices = new Ohlc(1m, 1.01m, 0.99m, 1m),
            Volume = new MarketVolume(100m, VolumeKind.Unknown),
            IsComplete = true
        },
        Indicators = new IndicatorSnapshot(),
        Swings = [],
        PriceZones = [],
        Trendlines = [],
        Channels = [],
        Confidence = new ConfidenceScore { Total = 0.5m, Contributions = [] }
    };

    private static LiveTradingPolicyBundle BuildBundle(string profileHash)
    {
        var features = new RuntimeFeaturePolicy
        {
            AnnotationOptions = new(),
            MarketRegimeRouting = new(),
            ValueLocationEvidence = new(),
            CurrencyStrengthEvidence = new(),
            RsiBollingerSignals = new(),
            DmiConfirmationEnabled = true,
            CurrencyStrength = new(),
            SetupCalibration = new()
        };
        return new LiveTradingPolicyBundle
        {
            PolicyBundleId = Guid.NewGuid(),
            Revision = 1,
            StrategyVersion = "v1",
            FeatureSchemaHash = features.ComputeHash(),
            FeaturePolicy = features,
            PositionSizing = new(),
            AdaptiveRisk = new(),
            CorrelationRisk = new PortfolioManager.Correlation.CorrelationRiskOptions(),
            PortfolioRisk = new(),
            TradingConditions = new(),
            AccountSafety = new(),
            LegacyManagement = new(),
            RegimeManagement = new TradeManager.RegimeManagementOptions(),
            ImprovedManagement = new(),
            ConfigurationHash = $"capacity-{profileHash}",
            CreatedAt = DateTimeOffset.UnixEpoch
        };
    }

    private sealed class RecordingRuntime(LiveAgentRuntime inner) : IAgentRuntime, IDisposable
    {
        public AgentInstanceKey Key => inner.Key;
        public AnalysisProfileKey AnalysisProfile => inner.AnalysisProfile;
        public AgentExecutionMode Mode => inner.Mode;
        public MarketAnalysisSnapshot? LastSnapshot { get; private set; }

        public Task<AgentRuntimeResult> EvaluateAsync(
            MarketAnalysisSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            LastSnapshot = snapshot;
            return inner.EvaluateAsync(snapshot, cancellationToken);
        }

        public void Dispose() => inner.Dispose();
    }
}
