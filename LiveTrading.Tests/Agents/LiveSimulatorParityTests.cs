using System.Threading.Channels;
using Agent.Abstractions;
using Agent.Models;
using Brokers.Models;
using ChartAnnotator.Engine;
using ChartAnnotator.MarketData;
using LiveTrading.Actors;
using LiveTrading.Agents;
using LiveTrading.Configuration;
using LiveTrading.MarketData;
using LiveTrading.Shadow;
using LiveTrading.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using RiskManager.Calibration;
using RiskManager.Safety;
using TradingCore.MarketData;
using TradingCore.Pipeline;

namespace LiveTrading.Tests.Agents;

/// <summary>
/// Phase 2's core acceptance-criterion test: "Agent output matches simulator on replayed
/// identical completed candles." <see cref="LiveTrading.Agents.AgentSupervisor.EvaluateAsync"/>'s
/// <see cref="AgentMarketContext"/> construction is verified byte-for-byte against
/// <c>Simulator.Engine.StrategySimulationSession.ProcessFrameAsync</c>'s construction (Simulator/
/// Engine/StrategySimulationSession.cs:477-489) by hand-building an identical "simulator-style"
/// context from the same <see cref="MarketAnalysisUpdate"/>/broker/spread and comparing the
/// resulting <see cref="AgentDecision"/>s field-by-field - the same shared <see
/// cref="StrategyDecisionPipelineFactory"/> both sides already use (Phase 0's guarantee) means any
/// divergence here can only come from context construction, which is exactly the new surface
/// Phase 2 introduces.
/// </summary>
[TestFixture]
public sealed class LiveSimulatorParityTests
{
    private static readonly InstrumentKey Instrument = AgentTestSupport.Instrument;
    private static readonly BarInterval M1 = AgentTestSupport.M1;
    private const int WarmupCandles = 10;
    private const decimal SpreadBasisPoints = 1.5m;

    private static Candle Candle(DateTimeOffset openTime, decimal close) => new()
    {
        Instrument = Instrument,
        Interval = M1,
        OpenTime = openTime,
        CloseTime = openTime.AddMinutes(1),
        Prices = new Ohlc(close - 0.0003m, close + 0.0005m, close - 0.0007m, close),
        Volume = new MarketVolume(100m, VolumeKind.Unknown),
        IsComplete = true
    };

    private static LiveMarketDefinition Market() => new()
    {
        Instrument = Instrument,
        ExecutionInterval = M1,
        AnalysisBaseInterval = M1,
        AnalysisIntervals = new HashSet<BarInterval> { M1 }
    };

    /// <summary>Deterministic, pure function of its <see cref="AgentMarketContext"/> - captures
    /// both the context it received and the decision it returned, so a test can compare two
    /// independent evaluations for exact equality.</summary>
    private sealed class RecordingAgent : ITradingAgent
    {
        public string Name => "recording-agent";
        public IReadOnlySet<BarInterval> RequiredIntervals { get; } = new HashSet<BarInterval> { M1 };
        public BarInterval TriggerInterval => M1;
        public AgentExitManagementMode ExitManagementMode => AgentExitManagementMode.Bracket;
        public AgentMarketContext? LastContext { get; private set; }
        public AgentDecision? LastDecision { get; private set; }

        public Task<AgentDecision> EvaluateAsync(AgentMarketContext context, CancellationToken cancellationToken = default)
        {
            LastContext = context;
            decimal close = context.Analysis.Get(M1).LatestCandle.Prices.Close;
            LastDecision = new AgentDecision
            {
                DecisionId = "parity-decision",
                Action = AgentAction.Buy,
                Instrument = context.Instrument,
                Confidence = 0.8m,
                CreatedAt = context.Timestamp,
                Reason = "parity-test",
                ReferencePrice = close,
                StopLossPrice = close - 0.005m,
                TakeProfitPrice = close + 0.010m
            };
            return Task.FromResult(LastDecision);
        }
    }

    private static RuntimeFeaturePolicy FeaturePolicy(bool setupCalibrationEnabled) => new()
    {
        AnnotationOptions = new(),
        MarketRegimeRouting = new(),
        ValueLocationEvidence = new(),
        CurrencyStrengthEvidence = new(),
        RsiBollingerSignals = new(),
        DmiConfirmationEnabled = true,
        CurrencyStrength = new(),
        SetupCalibration = new SetupCalibrationPolicyOptions { Enabled = setupCalibrationEnabled }
    };

    private static LiveTradingPolicyBundle BuildBundle(RuntimeFeaturePolicy featurePolicy) => new()
    {
        PolicyBundleId = Guid.NewGuid(),
        Revision = 1,
        StrategyVersion = "v1",
        FeatureSchemaHash = featurePolicy.ComputeHash(),
        FeaturePolicy = featurePolicy,
        PositionSizing = new(),
        AdaptiveRisk = new(),
        PortfolioRisk = new(),
        CorrelationRisk = new(),
        TradingConditions = new(),
        AccountSafety = new(),
        LegacyManagement = new(),
        ImprovedManagement = new(),
        RegimeManagement = new(),
        ConfigurationHash = "test-configuration-hash",
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static SetupCalibrationArtifact EmptyBucketArtifact() => new()
    {
        SchemaVersion = 1,
        CalibrationId = "cal-1",
        TrainingFrom = DateTimeOffset.UtcNow.AddDays(-30),
        TrainingTo = DateTimeOffset.UtcNow.AddDays(-1),
        Instruments = [Instrument.Value],
        StrategyVersion = "v1",
        FeatureSchemaHash = MetaLabelFeatureFactory.SchemaVersion,
        Parameters = new Dictionary<string, string>(),
        TotalSamples = 0,
        CreatedAt = DateTimeOffset.UtcNow,
        DataHash = "test-data-hash",
        Buckets = [] // deliberately empty - Evaluate falls through to "SetupCalibrationBucketUnavailable".
    };

    /// <summary>Drives a real <see cref="MarketAnalysisActor"/> (Phase 1's unchanged data plane)
    /// over a seeded warm-up history and returns the single <see cref="MarketAnalysisUpdate"/> it
    /// publishes once ready - the shared input both the "simulator-style" hand-built context and
    /// the live <see cref="AgentSupervisor"/> path consume in this test.</summary>
    private static async Task<MarketAnalysisUpdate> BuildLiveUpdateAsync(FakeTimeProvider clock)
    {
        var provider = new FakeCompletedCandleProvider();
        DateTimeOffset start = clock.GetUtcNow().AddMinutes(-WarmupCandles);
        provider.Seed(Instrument, M1, Enumerable.Range(0, WarmupCandles)
            .Select(i => Candle(start.AddMinutes(i), 1.1000m + i * 0.0001m)));

        var aggregator = new MultiTimeframeAggregator(Instrument, Market().AnalysisIntervals);
        IChartAnnotator annotator = new ChartAnnotationEngine(
            new ChartAnnotationOptions { AtrPeriod = 3, RsiPeriod = 3, BollingerPeriod = 3, HeavyAnalysisEveryCandles = 1 });
        IMarketDataQualityGate qualityGate = new MarketDataQualityGate(
            new MarketDataQualityOptions { RejectGaps = true, RequireIndicatorsReady = false });
        var actor = new MarketAnalysisActor(
            Market(), aggregator, annotator, qualityGate, provider, clock, NullLogger<MarketAnalysisActor>.Instance, WarmupCandles);

        Channel<LiveMarketEvent> input = Channel.CreateBounded<LiveMarketEvent>(
            new BoundedChannelOptions(10) { SingleReader = true, SingleWriter = false });
        Channel<MarketAnalysisUpdate> output = Channel.CreateBounded<MarketAnalysisUpdate>(
            new BoundedChannelOptions(10) { SingleReader = true, SingleWriter = true });

        // Warm-up alone never publishes - only a genuine live candle event triggers an update
        // (matching MarketAnalysisActorTests.RunAsync_LiveCandleAfterWarmUp_PublishesExactlyOneUpdate).
        DateTimeOffset liveOpen = clock.GetUtcNow();
        await input.Writer.WriteAsync(new CandleClosedMarketEvent
        {
            Instrument = Instrument,
            ReceivedAt = clock.GetUtcNow(),
            Kind = LiveMarketEventKind.CandleClosed,
            Candle = Candle(liveOpen, 1.1000m + WarmupCandles * 0.0001m),
            Interval = M1
        });
        input.Writer.Complete();
        clock.Advance(TimeSpan.FromMinutes(1));

        await actor.RunAsync(input.Reader, output.Writer, CancellationToken.None);

        Assert.That(output.Reader.Count, Is.EqualTo(1), "Expected exactly one MarketAnalysisUpdate from the live candle.");
        return await output.Reader.ReadAsync();
    }

    private static void AssertDecisionsMatch(AgentDecision expected, AgentDecision actual)
    {
        Assert.Multiple(() =>
        {
            Assert.That(actual.Action, Is.EqualTo(expected.Action));
            Assert.That(actual.Instrument, Is.EqualTo(expected.Instrument));
            Assert.That(actual.Confidence, Is.EqualTo(expected.Confidence));
            Assert.That(actual.CreatedAt, Is.EqualTo(expected.CreatedAt));
            Assert.That(actual.Reason, Is.EqualTo(expected.Reason));
            Assert.That(actual.ReferencePrice, Is.EqualTo(expected.ReferencePrice));
            Assert.That(actual.StopLossPrice, Is.EqualTo(expected.StopLossPrice));
            Assert.That(actual.TakeProfitPrice, Is.EqualTo(expected.TakeProfitPrice));
            Assert.That(actual.RegimeLabel, Is.EqualTo(expected.RegimeLabel));
            Assert.That(actual.SetupCalibrationRiskMultiplier, Is.EqualTo(expected.SetupCalibrationRiskMultiplier));
            Assert.That(actual.MetaLabelRiskMultiplier, Is.EqualTo(expected.MetaLabelRiskMultiplier));
            // IReadOnlyList<string> fields break record-generated Equals/== via reference-equality
            // fallback (arrays/List<T> don't implement IEquatable<IReadOnlyList<string>>) - compare
            // contents explicitly rather than via the record's own Equals.
            Assert.That(actual.ValueLocationEvidenceReasonCodes, Is.EqualTo(expected.ValueLocationEvidenceReasonCodes).AsCollection);
            Assert.That(actual.TrendQualityReasonCodes, Is.EqualTo(expected.TrendQualityReasonCodes).AsCollection);
            Assert.That(actual.CurrencyStrengthReasonCodes, Is.EqualTo(expected.CurrencyStrengthReasonCodes).AsCollection);
        });
    }

    [Test]
    public async Task AgentSupervisorContext_MatchesHandBuiltSimulatorStyleContext_ProducesIdenticalDecisions()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 2, 8, 0, 0, TimeSpan.Zero));
        MarketAnalysisUpdate update = await BuildLiveUpdateAsync(clock);

        var pipelineFactory = new StrategyDecisionPipelineFactory(new ShadowExecutionCoordinator());
        RuntimeFeaturePolicy featurePolicy = FeaturePolicy(setupCalibrationEnabled: false);
        var safetyOptions = new TradingSafetyOptions();
        var broker = new FakeBrokerClient();
        decimal executableSpread = update.Analysis.Get(M1).LatestCandle.Prices.Close * SpreadBasisPoints / 10_000m;

        // "Simulator-style": hand-built AgentMarketContext, field-for-field identical to
        // StrategySimulationSession.ProcessFrameAsync's own construction.
        var simulatorAgent = new RecordingAgent();
        StrategyDecisionRuntime simulatorRuntime = pipelineFactory.Create(
            new StrategyRuntimeDefinition { StrategyId = "strategy-1", StrategyVersion = "v1", Agent = simulatorAgent },
            featurePolicy, setupCalibration: null, metaModel: null, safetyOptions);
        var simulatorContext = new AgentMarketContext
        {
            Instrument = update.Instrument,
            Timestamp = update.AvailableAt,
            Analysis = update.Analysis,
            Account = broker.Accounts_Value.Single(),
            Positions = broker.Positions_Value,
            OpenOrders = broker.OpenOrders_Value,
            ExecutableSpread = executableSpread,
            MarketDataAvailableAt = update.AvailableAt,
            StrategyId = "strategy-1",
            CurrencyStrength = null
        };
        var shadowBroker = new ShadowBrokerClient(broker);
        TradingPipelineResult simulatorResult = await simulatorRuntime.Pipeline
            .ProcessAsync(simulatorContext, shadowBroker, CancellationToken.None);

        // Live: AgentSupervisor.EvaluateAsync builds its own context internally from the same
        // MarketAnalysisUpdate/broker/spread.
        var liveAgent = new RecordingAgent();
        StrategyDecisionRuntime liveRuntime = pipelineFactory.Create(
            new StrategyRuntimeDefinition { StrategyId = "strategy-1", StrategyVersion = "v1", Agent = liveAgent },
            featurePolicy, setupCalibration: null, metaModel: null, safetyOptions);
        var epochCoordinator = new LiveDecisionEpochCoordinator(
            [Instrument], clock, new LiveDecisionEpochCoordinatorOptions(), NullLogger<LiveDecisionEpochCoordinator>.Instance);
        var supervisor = new AgentSupervisor(broker, epochCoordinator, clock, NullLogger<AgentSupervisor>.Instance);
        var instance = new AgentInstanceState
        {
            Key = new AgentInstanceKey(AgentInstanceKey.DefaultDeploymentId, Instrument, "strategy-1", Guid.Empty, 1),
            Runtime = liveRuntime,
            PolicyBundle = BuildBundle(featurePolicy),
            Assignment = new LiveStrategyAssignment { StrategyId = "strategy-1", PolicyBundleId = "bundle-1" }
        };
        supervisor.Register(instance);
        await supervisor.ApplyAsync(update, executableSpread, CancellationToken.None);

        Assert.That(simulatorAgent.LastContext, Is.Not.Null);
        Assert.That(liveAgent.LastContext, Is.Not.Null);
        AgentMarketContext expectedContext = simulatorAgent.LastContext!;
        AgentMarketContext actualContext = liveAgent.LastContext!;
        Assert.Multiple(() =>
        {
            Assert.That(actualContext.Instrument, Is.EqualTo(expectedContext.Instrument));
            Assert.That(actualContext.Timestamp, Is.EqualTo(expectedContext.Timestamp));
            Assert.That(actualContext.Analysis, Is.SameAs(update.Analysis));
            Assert.That(expectedContext.Analysis, Is.SameAs(update.Analysis));
            Assert.That(actualContext.ExecutableSpread, Is.EqualTo(expectedContext.ExecutableSpread));
            Assert.That(actualContext.MarketDataAvailableAt, Is.EqualTo(expectedContext.MarketDataAvailableAt));
            Assert.That(actualContext.StrategyId, Is.EqualTo(expectedContext.StrategyId));
            Assert.That(actualContext.CurrencyStrength, Is.EqualTo(expectedContext.CurrencyStrength));
        });

        Assert.That(simulatorAgent.LastDecision, Is.Not.Null);
        Assert.That(liveAgent.LastDecision, Is.Not.Null);
        AssertDecisionsMatch(simulatorAgent.LastDecision!, liveAgent.LastDecision!);

        // Acceptance criteria: no order ever reaches a broker, meta multiplier never exceeds one.
        Assert.That(simulatorResult.Submission, Is.Null);
        Assert.That(instance.LastCandidate, Is.Not.Null);
        Assert.That(instance.LastCandidate!.MetaLabel.RiskMultiplier, Is.LessThanOrEqualTo(1m));
    }

    [Test]
    public async Task FeatureSwitchDifference_SetupCalibrationEnabledVsDisabled_ProducesVisiblyDifferentDecisions()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 2, 8, 0, 0, TimeSpan.Zero));
        MarketAnalysisUpdate update = await BuildLiveUpdateAsync(clock);

        var pipelineFactory = new StrategyDecisionPipelineFactory(new ShadowExecutionCoordinator());
        var safetyOptions = new TradingSafetyOptions();
        var broker = new FakeBrokerClient();
        decimal executableSpread = update.Analysis.Get(M1).LatestCandle.Prices.Close * SpreadBasisPoints / 10_000m;
        var shadowBroker = new ShadowBrokerClient(broker);

        var context = new AgentMarketContext
        {
            Instrument = update.Instrument,
            Timestamp = update.AvailableAt,
            Analysis = update.Analysis,
            Account = broker.Accounts_Value.Single(),
            Positions = broker.Positions_Value,
            OpenOrders = broker.OpenOrders_Value,
            ExecutableSpread = executableSpread,
            MarketDataAvailableAt = update.AvailableAt,
            StrategyId = "strategy-1",
            CurrencyStrength = null
        };

        RuntimeFeaturePolicy disabledPolicy = FeaturePolicy(setupCalibrationEnabled: false);
        StrategyDecisionRuntime disabledRuntime = pipelineFactory.Create(
            new StrategyRuntimeDefinition { StrategyId = "strategy-1", StrategyVersion = "v1", Agent = new RecordingAgent() },
            disabledPolicy, setupCalibration: null, metaModel: null, safetyOptions);
        TradingPipelineResult disabledResult = await disabledRuntime.Pipeline.ProcessAsync(context, shadowBroker, CancellationToken.None);

        RuntimeFeaturePolicy enabledPolicy = FeaturePolicy(setupCalibrationEnabled: true);
        StrategyDecisionRuntime enabledRuntime = pipelineFactory.Create(
            new StrategyRuntimeDefinition { StrategyId = "strategy-1", StrategyVersion = "v1", Agent = new RecordingAgent() },
            enabledPolicy, setupCalibration: EmptyBucketArtifact(), metaModel: null, safetyOptions);
        TradingPipelineResult enabledResult = await enabledRuntime.Pipeline.ProcessAsync(context, shadowBroker, CancellationToken.None);

        Assert.Multiple(() =>
        {
            // Disabled: the pipeline never constructs a SetupCalibrationPolicy at all, so the
            // decision's SetupCalibrationRiskMultiplier stays whatever the agent itself set (null
            // here - RecordingAgent never sets it).
            Assert.That(disabledResult.Decision!.SetupCalibrationRiskMultiplier, Is.Null);
            Assert.That(disabledResult.SetupCalibration, Is.Null);

            // Enabled with an artifact that has no matching bucket: Trade stays true (fail-open),
            // but the decision is visibly stamped with the calibration's risk multiplier and the
            // pipeline result carries the evaluated SetupCalibrationDecision - a real, visible
            // difference driven purely by the feature-policy toggle, on otherwise-identical input.
            Assert.That(enabledResult.Decision!.SetupCalibrationRiskMultiplier, Is.EqualTo(1m));
            Assert.That(enabledResult.SetupCalibration, Is.Not.Null);
            Assert.That(enabledResult.SetupCalibration!.ReasonCode, Is.EqualTo("SetupCalibrationBucketUnavailable"));

            // Everything else about the decision (action, prices, confidence) is unaffected -
            // the difference is attributable only to the calibration-related fields.
            Assert.That(enabledResult.Decision!.Action, Is.EqualTo(disabledResult.Decision!.Action));
            Assert.That(enabledResult.Decision!.ReferencePrice, Is.EqualTo(disabledResult.Decision!.ReferencePrice));
        });
    }

    /// <summary>
    /// Multi-agent architecture Phase 5: a simulator assignment's <c>AnalysisOptionsOverride</c>
    /// and an equivalent live-host assignment's resolved <c>LiveTradingPolicyBundle.FeaturePolicy
    /// .AnnotationOptions</c> must resolve to the identical <see cref="AnalysisProfileKey"/> when
    /// given the same options/required intervals. <c>Simulator.Engine.AnalysisProfileRegistry</c>
    /// and <see cref="LiveAnalysisProfileRegistry"/> are independent instances (one per
    /// environment, per foundational decision #5 - no shared concrete runtime class) - both are
    /// documented, verified-by-reading thin wrappers around
    /// <see cref="AnalysisProfileKey.Create"/> with <see cref="MetaLabelFeatureFactory.SchemaVersion"/>,
    /// so exercising that shared factory directly (rather than adding a test-only cross-project
    /// reference to Simulator, which LiveTrading.Tests does not otherwise need) proves the same
    /// parity guarantee both registries individually rely on.
    /// </summary>
    [Test]
    public void AnalysisProfileKey_SameOptions_MatchesAcrossSimulatorAndLiveRegistries()
    {
        var options = new ChartAnnotationOptions { RsiPeriod = 21, SwingLeftBars = 3, SwingRightBars = 3 };
        IReadOnlySet<BarInterval> intervals = new HashSet<BarInterval> { M1 };

        AnalysisProfileKey simulatorProfile = AnalysisProfileKey.Create(
            options, intervals, MetaLabelFeatureFactory.SchemaVersion);
        AnalysisProfileKey liveProfile = new LiveAnalysisProfileRegistry().GetOrCreateProfile(options, intervals);

        Assert.That(simulatorProfile.ProfileHash, Is.EqualTo(liveProfile.ProfileHash));
        Assert.That(simulatorProfile, Is.EqualTo(liveProfile));
    }

    /// <summary>Divergent options must still diverge identically on both sides - the parity
    /// guarantee above would be vacuous if the underlying factory simply ignored its input.</summary>
    [Test]
    public void AnalysisProfileKey_DifferentOptions_DivergesConsistently()
    {
        IReadOnlySet<BarInterval> intervals = new HashSet<BarInterval> { M1 };
        var liveRegistry = new LiveAnalysisProfileRegistry();

        AnalysisProfileKey simulatorDefault = AnalysisProfileKey.Create(
            new ChartAnnotationOptions(), intervals, MetaLabelFeatureFactory.SchemaVersion);
        AnalysisProfileKey simulatorOverride = AnalysisProfileKey.Create(
            new ChartAnnotationOptions { RsiPeriod = 21 }, intervals, MetaLabelFeatureFactory.SchemaVersion);
        AnalysisProfileKey liveDefault = liveRegistry.GetOrCreateProfile(new ChartAnnotationOptions(), intervals);
        AnalysisProfileKey liveOverride = liveRegistry.GetOrCreateProfile(
            new ChartAnnotationOptions { RsiPeriod = 21 }, intervals);

        Assert.Multiple(() =>
        {
            Assert.That(simulatorOverride.ProfileHash, Is.Not.EqualTo(simulatorDefault.ProfileHash));
            Assert.That(liveOverride.ProfileHash, Is.Not.EqualTo(liveDefault.ProfileHash));
            Assert.That(simulatorDefault.ProfileHash, Is.EqualTo(liveDefault.ProfileHash));
            Assert.That(simulatorOverride.ProfileHash, Is.EqualTo(liveOverride.ProfileHash));
        });
    }
}
