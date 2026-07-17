using Agent.Models;
using Brokers.Models;
using ChartAnnotator.Engine;
using LiveTrading.Agents;
using LiveTrading.Shadow;
using LiveTrading.Tests.Fakes;
using NUnit.Framework;
using TradingCore.Pipeline;

namespace LiveTrading.Tests.Agents;

[TestFixture]
public sealed class LiveAgentRuntimeTests
{
    private static readonly InstrumentKey Instrument = AgentTestSupport.Instrument;
    private static readonly BarInterval M1 = AgentTestSupport.M1;
    private static readonly IReadOnlySet<BarInterval> Intervals = new HashSet<BarInterval> { M1 };

    private static AnalysisProfileKey Profile() =>
        AnalysisProfileKey.Create(new ChartAnnotationOptions(), Intervals, "schema-v1");

    // AgentTestSupport.Snapshot hardcodes Version = 1 (fine for its own single-snapshot tests) -
    // MarketDataQualityGate's default RejectGaps/version-collision check rejects a later call
    // whose AvailableAt differs but whose Version stayed at 1, so every snapshot built here needs
    // its own Version, incrementing in step with AvailableAt.
    private static ChartAnnotator.Models.AnalysisSnapshot AnalysisSnapshot(DateTimeOffset availableAt, long version) => new()
    {
        Instrument = Instrument,
        Interval = M1,
        AvailableAt = availableAt,
        Version = version,
        LatestCandle = AgentTestSupport.Candle(availableAt.AddMinutes(-1), M1),
        Indicators = new ChartAnnotator.Models.IndicatorSnapshot(),
        Swings = [],
        PriceZones = [],
        Trendlines = [],
        Channels = [],
        Confidence = new ChartAnnotator.Models.ConfidenceScore { Total = 0.5m, Contributions = [] }
    };

    private static MarketAnalysisSnapshot Snapshot(DateTimeOffset availableAt, long version = 1) => new()
    {
        Instrument = Instrument,
        Profile = Profile(),
        SnapshotVersion = version,
        DecisionEpoch = version,
        AvailableAt = availableAt,
        Timeframes = new Dictionary<BarInterval, ChartAnnotator.Models.AnalysisSnapshot>
        {
            [M1] = AnalysisSnapshot(availableAt, version)
        },
        CrossMarket = null,
        DataQuality = TradingCore.MarketData.DataQualityResult.Valid
    };

    private static FakeTradingAgent BuildAgent(Func<AgentMarketContext, AgentDecision> decide) => new()
    {
        RequiredIntervals = Intervals,
        TriggerInterval = M1,
        Decide = decide
    };

    private static AgentDecision Observe(AgentMarketContext context) => new()
    {
        Action = AgentAction.Observe,
        Instrument = context.Instrument,
        Confidence = 0m,
        CreatedAt = context.Timestamp,
        Reason = "no setup"
    };

    private static SafeTradingPipeline BuildPipeline(Agent.Abstractions.ITradingAgent agent) => new(
        agent,
        new ShadowExecutionCoordinator(),
        dataQuality: new TradingCore.MarketData.MarketDataQualityGate(
            new TradingCore.MarketData.MarketDataQualityOptions { RejectGaps = false, RejectFutureSnapshots = false }));

    private static LiveAgentRuntime BuildRuntime(
        FakeTradingAgent agent, string strategyId = "strategy-1", int revision = 1, FakeBrokerClient? broker = null) =>
        new(
            new AgentInstanceKey(AgentInstanceKey.DefaultDeploymentId, Instrument, strategyId, Guid.Empty, revision),
            Profile(),
            AgentExecutionMode.Shadow,
            BuildPipeline(agent),
            broker ?? new FakeBrokerClient());

    [Test]
    public async Task EvaluateAsync_TwoIndependentRuntimes_DoNotShareState()
    {
        var agentA = BuildAgent(Observe);
        var agentB = BuildAgent(Observe);
        using LiveAgentRuntime runtimeA = BuildRuntime(agentA, "strategy-a");
        using LiveAgentRuntime runtimeB = BuildRuntime(agentB, "strategy-b");
        DateTimeOffset now = DateTimeOffset.UtcNow;

        AgentRuntimeResult resultA = await runtimeA.EvaluateAsync(Snapshot(now));
        AgentRuntimeResult resultB = await runtimeB.EvaluateAsync(Snapshot(now));

        Assert.Multiple(() =>
        {
            Assert.That(agentA.EvaluationCount, Is.EqualTo(1));
            Assert.That(agentB.EvaluationCount, Is.EqualTo(1));
            Assert.That(resultA.Key, Is.Not.EqualTo(resultB.Key));
            Assert.That(resultA.Failure, Is.Null);
            Assert.That(resultB.Failure, Is.Null);
        });
    }

    [Test]
    public async Task EvaluateAsync_ExceptionMidEvaluation_ReturnsFailureAndReleasesGate()
    {
        var agent = BuildAgent(_ => throw new InvalidOperationException("boom"));
        using LiveAgentRuntime runtime = BuildRuntime(agent);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        AgentRuntimeResult first = await runtime.EvaluateAsync(Snapshot(now));
        Assert.That(first.Failure, Is.Not.Null);
        Assert.That(first.Failure, Is.InstanceOf<InvalidOperationException>());

        // The gate must have been released in the failing call's `finally` - a second call must
        // not hang waiting on a semaphore a prior exception never released.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        AgentRuntimeResult second = await runtime.EvaluateAsync(Snapshot(now.AddMinutes(1), version: 2), cts.Token);
        Assert.That(second.Failure, Is.Not.Null);
    }

    [Test]
    public void Key_DiffersByRevision_EvenForSameStrategyId()
    {
        var agentA = BuildAgent(Observe);
        var agentB = BuildAgent(Observe);
        using LiveAgentRuntime runtimeA = BuildRuntime(agentA, "strategy-1", revision: 1);
        using LiveAgentRuntime runtimeB = BuildRuntime(agentB, "strategy-1", revision: 2);

        Assert.That(runtimeA.Key, Is.Not.EqualTo(runtimeB.Key));
        Assert.That(runtimeA.Key.StrategyId, Is.EqualTo(runtimeB.Key.StrategyId));
    }
}
