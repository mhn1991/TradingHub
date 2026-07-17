using Agent.Models;
using Brokers.Models;
using LiveTrading.Actors;
using LiveTrading.Agents;
using LiveTrading.Configuration;
using LiveTrading.Shadow;
using LiveTrading.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using PortfolioManager.Risk;
using RiskManager;
using RiskManager.Conditions;
using RiskManager.Safety;
using TradeManager;
using TradingCore.Pipeline;

namespace LiveTrading.Tests.Agents;

[TestFixture]
public sealed class AgentSupervisorTests
{
    private static readonly InstrumentKey Instrument = AgentTestSupport.Instrument;
    private static readonly BarInterval M1 = AgentTestSupport.M1;
    private static readonly BarInterval M5 = AgentTestSupport.M5;

    private static LiveTradingPolicyBundle BuildBundle()
    {
        var featurePolicy = new RuntimeFeaturePolicy
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
            FeatureSchemaHash = featurePolicy.ComputeHash(),
            FeaturePolicy = featurePolicy,
            PositionSizing = new(),
            AdaptiveRisk = new(),
            CorrelationRisk = new PortfolioManager.Correlation.CorrelationRiskOptions(),
            PortfolioRisk = new(),
            TradingConditions = new(),
            AccountSafety = new(),
            LegacyManagement = new(),
            RegimeManagement = new TradeManager.RegimeManagementOptions(),
            ImprovedManagement = new(),
            ConfigurationHash = "test-configuration-hash",
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private static AgentInstanceState BuildInstance(FakeTradingAgent agent, string strategyId = "strategy-1") => new()
    {
        Key = new AgentInstanceKey(AgentInstanceKey.DefaultDeploymentId, Instrument, strategyId, Guid.Empty, 1),
        Runtime = new StrategyDecisionRuntime
        {
            Agent = agent,
            Pipeline = new SafeTradingPipeline(agent, new ShadowExecutionCoordinator()),
            StrategyVersion = "v1",
            FeaturePolicyHash = "hash",
            SetupCalibrationId = null,
            MetaModelVersion = null
        },
        PolicyBundle = BuildBundle(),
        Assignment = new LiveStrategyAssignment { StrategyId = strategyId, PolicyBundleId = "bundle-1" }
    };

    private static AgentDecision Observe(AgentMarketContext context) => new()
    {
        Action = AgentAction.Observe,
        Instrument = context.Instrument,
        Confidence = 0m,
        CreatedAt = context.Timestamp,
        Reason = "no setup"
    };

    private static AgentDecision Buy(AgentMarketContext context) => new()
    {
        Action = AgentAction.Buy,
        Instrument = context.Instrument,
        Confidence = 0.75m,
        CreatedAt = context.Timestamp,
        Reason = "test buy signal",
        ReferencePrice = 1.1002m,
        StopLossPrice = 1.0950m,
        TakeProfitPrice = 1.1100m
    };

    private static AgentSupervisor BuildSupervisor(FakeBrokerClient broker, out LiveDecisionEpochCoordinator epoch, TimeProvider? timeProvider = null)
    {
        timeProvider ??= new FakeTimeProvider(new DateTimeOffset(2026, 3, 2, 8, 0, 0, TimeSpan.Zero));
        epoch = new LiveDecisionEpochCoordinator(
            [Instrument], timeProvider, new LiveDecisionEpochCoordinatorOptions(), NullLogger<LiveDecisionEpochCoordinator>.Instance);
        return new AgentSupervisor(broker, epoch, timeProvider, NullLogger<AgentSupervisor>.Instance);
    }

    [Test]
    public async Task ApplyAsync_TriggerIntervalNotClosed_SkipsEvaluation()
    {
        var agent = new FakeTradingAgent { TriggerInterval = M1, RequiredIntervals = new HashSet<BarInterval> { M1 }, Decide = Observe };
        AgentInstanceState instance = BuildInstance(agent);
        var supervisor = BuildSupervisor(new FakeBrokerClient(), out _);
        supervisor.Register(instance);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        MultiTimeframeAnalysis analysis = AgentTestSupport.Analysis(now, M1);
        MarketAnalysisUpdate update = AgentTestSupport.Update(analysis, new HashSet<BarInterval> { M5 }, now, sequence: 1);

        await supervisor.ApplyAsync(update, executableSpread: 0.0002m, CancellationToken.None);

        Assert.That(agent.EvaluationCount, Is.EqualTo(0));
    }

    [Test]
    public async Task ApplyAsync_MissingRequiredSnapshot_SkipsEvaluation()
    {
        var agent = new FakeTradingAgent
        {
            TriggerInterval = M1, RequiredIntervals = new HashSet<BarInterval> { M1, M5 }, Decide = Observe
        };
        AgentInstanceState instance = BuildInstance(agent);
        var supervisor = BuildSupervisor(new FakeBrokerClient(), out _);
        supervisor.Register(instance);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        MultiTimeframeAnalysis analysis = AgentTestSupport.Analysis(now, M1); // M5 missing
        MarketAnalysisUpdate update = AgentTestSupport.Update(analysis, new HashSet<BarInterval> { M1 }, now, sequence: 1);

        await supervisor.ApplyAsync(update, executableSpread: 0.0002m, CancellationToken.None);

        Assert.That(agent.EvaluationCount, Is.EqualTo(0));
    }

    [Test]
    public async Task ApplyAsync_DuplicateOrOutOfOrderMarketSequence_IsSkipped()
    {
        var agent = new FakeTradingAgent { TriggerInterval = M1, RequiredIntervals = new HashSet<BarInterval> { M1 }, Decide = Observe };
        AgentInstanceState instance = BuildInstance(agent);
        var supervisor = BuildSupervisor(new FakeBrokerClient(), out _);
        supervisor.Register(instance);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        MultiTimeframeAnalysis analysis = AgentTestSupport.Analysis(now, M1);
        MarketAnalysisUpdate first = AgentTestSupport.Update(analysis, new HashSet<BarInterval> { M1 }, now, sequence: 5);
        MarketAnalysisUpdate stale = AgentTestSupport.Update(analysis, new HashSet<BarInterval> { M1 }, now, sequence: 5);

        await supervisor.ApplyAsync(first, executableSpread: 0.0002m, CancellationToken.None);
        await supervisor.ApplyAsync(stale, executableSpread: 0.0002m, CancellationToken.None);

        Assert.That(agent.EvaluationCount, Is.EqualTo(1));
    }

    [Test]
    public async Task ApplyAsync_FullTrigger_UpdatesCountersAndLastCandidate()
    {
        var agent = new FakeTradingAgent { TriggerInterval = M1, RequiredIntervals = new HashSet<BarInterval> { M1 }, Decide = Buy };
        AgentInstanceState instance = BuildInstance(agent);
        var supervisor = BuildSupervisor(new FakeBrokerClient(), out _);
        supervisor.Register(instance);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        MultiTimeframeAnalysis analysis = AgentTestSupport.Analysis(now, M1);
        MarketAnalysisUpdate update = AgentTestSupport.Update(analysis, new HashSet<BarInterval> { M1 }, now, sequence: 1);

        await supervisor.ApplyAsync(update, executableSpread: 0.0002m, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(agent.EvaluationCount, Is.EqualTo(1));
            Assert.That(instance.CandidatesBuy, Is.EqualTo(1));
            Assert.That(instance.CandidatesSell, Is.EqualTo(0));
            Assert.That(instance.LastCandidate, Is.Not.Null);
            Assert.That(instance.LastCandidate!.Action, Is.EqualTo(AgentAction.Buy));
            Assert.That(instance.LastStatus, Is.EqualTo(TradingPipelineStatus.Processed.ToString()));
        });
    }

    [Test]
    public async Task ApplyAsync_ObserveDecision_UpdatesObservedCounterOnly()
    {
        var agent = new FakeTradingAgent { TriggerInterval = M1, RequiredIntervals = new HashSet<BarInterval> { M1 }, Decide = Observe };
        AgentInstanceState instance = BuildInstance(agent);
        var supervisor = BuildSupervisor(new FakeBrokerClient(), out _);
        supervisor.Register(instance);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        MultiTimeframeAnalysis analysis = AgentTestSupport.Analysis(now, M1);
        MarketAnalysisUpdate update = AgentTestSupport.Update(analysis, new HashSet<BarInterval> { M1 }, now, sequence: 1);

        await supervisor.ApplyAsync(update, executableSpread: 0.0002m, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(instance.CandidatesObserved, Is.EqualTo(1));
            Assert.That(instance.LastCandidate, Is.Null);
            // AGENT-10
            Assert.That(instance.Evaluations, Is.EqualTo(1));
            Assert.That(instance.ObserveReasonCounts.GetValueOrDefault("Unspecified"), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ApplyAsync_ObserveDecisionWithReasonCode_IncrementsReasonSpecificCounter()
    {
        // AGENT-10 regression: the diagnostics audit found only a coarse Observed counter existed,
        // with no per-reason-code breakdown (trend/setup/confirmation/price-action/regime/invalid-
        // stop rejections were all indistinguishable). This proves distinct reason codes are now
        // tracked separately.
        var agent = new FakeTradingAgent
        {
            TriggerInterval = M1,
            RequiredIntervals = new HashSet<BarInterval> { M1 },
            Decide = context => new AgentDecision
            {
                Action = AgentAction.Observe,
                Instrument = context.Instrument,
                Confidence = 0m,
                CreatedAt = context.Timestamp,
                Reason = "waiting for a valid primary trend",
                ReasonCode = "PrimaryTrendNotReady"
            }
        };
        AgentInstanceState instance = BuildInstance(agent);
        var supervisor = BuildSupervisor(new FakeBrokerClient(), out _);
        supervisor.Register(instance);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        MultiTimeframeAnalysis analysis = AgentTestSupport.Analysis(now, M1);
        MarketAnalysisUpdate update = AgentTestSupport.Update(analysis, new HashSet<BarInterval> { M1 }, now, sequence: 1);

        await supervisor.ApplyAsync(update, executableSpread: 0.0002m, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(instance.CandidatesObserved, Is.EqualTo(1));
            Assert.That(instance.ObserveReasonCounts.GetValueOrDefault("PrimaryTrendNotReady"), Is.EqualTo(1));
            Assert.That(instance.ObserveReasonCounts.GetValueOrDefault("Unspecified"), Is.EqualTo(0));
        });
    }

    [Test]
    public async Task ApplyAsync_RepeatedIdenticalDecisionId_IncrementsDuplicateCounter()
    {
        // AGENT-10 regression: the diagnostics audit found no "duplicate decisions" tracking.
        // DecisionId is deterministic (ProgressiveStrategyBase), so an exact repeat across two
        // evaluations is a genuine duplicate, not a coincidence.
        var agent = new FakeTradingAgent
        {
            TriggerInterval = M1,
            RequiredIntervals = new HashSet<BarInterval> { M1 },
            Decide = context => new AgentDecision
            {
                Action = AgentAction.Buy,
                Instrument = context.Instrument,
                Confidence = 80m,
                CreatedAt = context.Timestamp,
                Reason = "duplicate-test",
                DecisionId = "fixed-decision-id",
                ReferencePrice = 1.1002m,
                StopLossPrice = 1.0950m,
                TakeProfitPrice = 1.1100m
            }
        };
        AgentInstanceState instance = BuildInstance(agent);
        var supervisor = BuildSupervisor(new FakeBrokerClient(), out _);
        supervisor.Register(instance);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        MultiTimeframeAnalysis analysis = AgentTestSupport.Analysis(now, M1);
        MarketAnalysisUpdate first = AgentTestSupport.Update(analysis, new HashSet<BarInterval> { M1 }, now, sequence: 1);
        MarketAnalysisUpdate second = AgentTestSupport.Update(analysis, new HashSet<BarInterval> { M1 }, now.AddMinutes(1), sequence: 2);

        await supervisor.ApplyAsync(first, executableSpread: 0.0002m, CancellationToken.None);
        await supervisor.ApplyAsync(second, executableSpread: 0.0002m, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(instance.CandidatesBuy, Is.EqualTo(2));
            Assert.That(instance.DuplicateDecisions, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ApplyAsync_MetaLabelRiskMultiplier_NeverExceedsOneAcrossConfidenceSweep()
    {
        var random = new Random(1234);
        for (int i = 0; i < 25; i++)
        {
            decimal confidence = (decimal)random.NextDouble();
            var agent = new FakeTradingAgent
            {
                TriggerInterval = M1,
                RequiredIntervals = new HashSet<BarInterval> { M1 },
                Decide = context => new AgentDecision
                {
                    Action = AgentAction.Buy,
                    Instrument = context.Instrument,
                    Confidence = confidence,
                    CreatedAt = context.Timestamp,
                    Reason = "sweep",
                    ReferencePrice = 1.1002m,
                    StopLossPrice = 1.0950m,
                    TakeProfitPrice = 1.1100m
                }
            };
            AgentInstanceState instance = BuildInstance(agent, $"strategy-sweep-{i}");
            var supervisor = BuildSupervisor(new FakeBrokerClient(), out _);
            supervisor.Register(instance);

            DateTimeOffset now = DateTimeOffset.UtcNow;
            MultiTimeframeAnalysis analysis = AgentTestSupport.Analysis(now, M1);
            MarketAnalysisUpdate update = AgentTestSupport.Update(analysis, new HashSet<BarInterval> { M1 }, now, sequence: 1);

            await supervisor.ApplyAsync(update, executableSpread: 0.0002m, CancellationToken.None);

            if (instance.LastCandidate is { } candidate)
            {
                Assert.That(candidate.MetaLabel.RiskMultiplier, Is.LessThanOrEqualTo(1m),
                    $"Meta-label risk multiplier exceeded 1 for confidence {confidence}.");
            }
        }
    }
    [Test]
    public async Task ApplyAsync_NoAgentTriggerStillCompletesMarketEpoch()
    {
        var agent = new FakeTradingAgent
        {
            TriggerInterval = M5,
            RequiredIntervals = new HashSet<BarInterval> { M5 },
            Decide = Observe
        };
        AgentInstanceState instance = BuildInstance(agent);
        var supervisor = BuildSupervisor(new FakeBrokerClient(), out LiveDecisionEpochCoordinator epoch);
        supervisor.Register(instance);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        MultiTimeframeAnalysis analysis = AgentTestSupport.Analysis(now, M1);
        MarketAnalysisUpdate update = AgentTestSupport.Update(
            analysis,
            new HashSet<BarInterval> { M1 },
            now,
            sequence: 1);

        await supervisor.ApplyAsync(update, executableSpread: 0.0002m, CancellationToken.None);
        LiveDecisionEpochBatch batch = await epoch.ClosedEpochs.ReadAsync().AsTask()
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Multiple(() =>
        {
            Assert.That(agent.EvaluationCount, Is.EqualTo(0));
            Assert.That(batch.UnavailableInstruments, Is.Empty);
            Assert.That(batch.OrderedCandidates, Is.Empty);
        });
    }

}
