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
using TradingCore.Pipeline;

namespace LiveTrading.Tests.Agents;

/// <summary>
/// Multi-agent architecture Phase 6: per-agent evaluation timeout in
/// <see cref="AgentSupervisor.ApplyAsync"/>. A slow agent is marked unhealthy
/// (<see cref="AgentInstanceState.TimeoutCount"/>) without its evaluation task being abandoned,
/// and its per-instance gate stays held until that straggling evaluation truly finishes - so a
/// later <see cref="AgentSupervisor.ApplyAsync"/> call evaluating the same instance again must
/// wait for it, never overlap it.
/// </summary>
/// <remarks>
/// Every test here dispatches <c>ApplyAsync</c> via <c>Task.Run</c> rather than awaiting it
/// directly: the fake agent's blocking <c>Decide</c> callback runs on <c>ApplyAsync</c>'s
/// synchronous prefix (nothing yields control back to the caller before it, since
/// <see cref="FakeBrokerClient"/>'s calls all complete synchronously) - without <c>Task.Run</c>
/// the blocking call would run inline on the test thread, and there would be no way to advance
/// the <see cref="FakeTimeProvider"/> past the timeout while it's still blocked.
/// </remarks>
[TestFixture]
public sealed class AgentSupervisorTimeoutTests
{
    private static readonly InstrumentKey Instrument = AgentTestSupport.Instrument;
    private static readonly BarInterval M1 = AgentTestSupport.M1;

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

    // AgentTestSupport.Snapshot/Analysis hardcode Version = 1 - fine for a single call, but two
    // ApplyAsync calls against the *same* instance's SafeTradingPipeline (hence the same
    // stateful MarketDataQualityGate) with different AvailableAt and an unchanged Version trips
    // its "version_collision" critical check, silently rejecting the second call before it ever
    // reaches the agent. Each call here needs its own Version, incrementing with `sequence`.
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

    private static MarketAnalysisUpdate Update(DateTimeOffset now, long sequence)
    {
        var analysis = new MultiTimeframeAnalysis(
            Instrument, now, new Dictionary<BarInterval, ChartAnnotator.Models.AnalysisSnapshot>
            {
                [M1] = AnalysisSnapshot(now, sequence)
            });
        return AgentTestSupport.Update(analysis, new HashSet<BarInterval> { M1 }, now, sequence);
    }

    private static AgentDecision Observe(AgentMarketContext context) => new()
    {
        Action = AgentAction.Observe,
        Instrument = context.Instrument,
        Confidence = 0m,
        CreatedAt = context.Timestamp,
        Reason = "no setup"
    };

    [Test]
    public async Task ApplyAsync_SlowAgent_TimesOutWithoutAbandoningTheEvaluation()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 2, 8, 0, 0, TimeSpan.Zero));
        var epochCoordinator = new LiveDecisionEpochCoordinator(
            [Instrument], clock, new LiveDecisionEpochCoordinatorOptions { BarrierTimeout = TimeSpan.FromSeconds(30) },
            NullLogger<LiveDecisionEpochCoordinator>.Instance);
        var release = new TaskCompletionSource();
        var agent = new FakeTradingAgent
        {
            RequiredIntervals = new HashSet<BarInterval> { M1 },
            TriggerInterval = M1,
            Decide = context =>
            {
                release.Task.Wait(TimeSpan.FromSeconds(10));
                return Observe(context);
            }
        };
        AgentInstanceState instance = BuildInstance(agent);
        var supervisor = new AgentSupervisor(
            new FakeBrokerClient(), epochCoordinator, clock, NullLogger<AgentSupervisor>.Instance,
            maxConcurrentEvaluations: 2, evaluationTimeout: TimeSpan.FromSeconds(1));
        supervisor.Register(instance);

        DateTimeOffset now = clock.GetUtcNow();
        Task applyTask = Task.Run(() => supervisor.ApplyAsync(Update(now, sequence: 1), 1m, CancellationToken.None));

        // Let the background evaluation actually start blocking on `release` (and register its
        // Task.Delay timer against the fake clock) before advancing past the timeout.
        await Task.Delay(100);
        clock.Advance(TimeSpan.FromSeconds(2));
        await applyTask;

        Assert.That(instance.TimeoutCount, Is.EqualTo(1));

        // Release the straggling evaluation so it can finish, and the instance gate with it.
        release.SetResult();
        await Task.Delay(100);
        Assert.That(agent.EvaluationCount, Is.EqualTo(1),
            "The timed-out evaluation must still run to completion, not be abandoned.");
    }

    [Test]
    public async Task ApplyAsync_LaterCallForSameInstance_WaitsForTheStragglingEvaluation()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 2, 8, 0, 0, TimeSpan.Zero));
        var epochCoordinator = new LiveDecisionEpochCoordinator(
            [Instrument], clock, new LiveDecisionEpochCoordinatorOptions { BarrierTimeout = TimeSpan.FromSeconds(30) },
            NullLogger<LiveDecisionEpochCoordinator>.Instance);
        var release = new TaskCompletionSource();
        int concurrentDecideCalls = 0;
        int maxConcurrentDecideCalls = 0;
        var agent = new FakeTradingAgent
        {
            RequiredIntervals = new HashSet<BarInterval> { M1 },
            TriggerInterval = M1,
            Decide = context =>
            {
                int current = Interlocked.Increment(ref concurrentDecideCalls);
                int observedMax;
                do
                {
                    observedMax = maxConcurrentDecideCalls;
                } while (current > observedMax &&
                         Interlocked.CompareExchange(ref maxConcurrentDecideCalls, current, observedMax) != observedMax);

                // Only the first call blocks - the second (from the later ApplyAsync call) must
                // never run concurrently with it.
                if (current == 1)
                    release.Task.Wait(TimeSpan.FromSeconds(10));

                Interlocked.Decrement(ref concurrentDecideCalls);
                return Observe(context);
            }
        };
        AgentInstanceState instance = BuildInstance(agent);
        var supervisor = new AgentSupervisor(
            new FakeBrokerClient(), epochCoordinator, clock, NullLogger<AgentSupervisor>.Instance,
            maxConcurrentEvaluations: 2, evaluationTimeout: TimeSpan.FromSeconds(1));
        supervisor.Register(instance);

        DateTimeOffset now = clock.GetUtcNow();
        Task first = Task.Run(() => supervisor.ApplyAsync(Update(now, sequence: 1), 1m, CancellationToken.None));
        await Task.Delay(100);
        clock.Advance(TimeSpan.FromSeconds(2));
        await first;
        Assert.That(instance.TimeoutCount, Is.EqualTo(1));

        // The second ApplyAsync call must block on the instance gate until the straggler
        // finishes.
        Task second = Task.Run(() => supervisor.ApplyAsync(Update(now.AddMinutes(1), sequence: 2), 1m, CancellationToken.None));
        await Task.Delay(100);
        release.SetResult();
        await second;

        Assert.Multiple(() =>
        {
            Assert.That(maxConcurrentDecideCalls, Is.EqualTo(1), "The straggling and the later evaluation must never overlap.");
            Assert.That(agent.EvaluationCount, Is.EqualTo(2));
        });
    }
}
