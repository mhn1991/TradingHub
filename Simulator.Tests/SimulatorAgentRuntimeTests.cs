using Agent.Abstractions;
using Agent.Models;
using Brokers.Models;
using ChartAnnotator.Engine;
using Simulator.Engine;
using Simulator.MarketData;
using Simulator.Models;
using TradingCore.MarketData;
using TradingCore.Pipeline;

namespace Simulator.Tests;

/// <summary>
/// Thin adapter-forwarding checks for <see cref="SimulatorAgentRuntime"/> - the wrapped
/// <see cref="StrategyWorkerHost"/> already has its own dedicated sequencing/parallelism test
/// coverage (<see cref="Phase2StreamingAndWorkersTests"/>), so these tests only exercise the
/// wrapper's own surface: Key/AnalysisProfile/Mode passthrough, the documented
/// <see cref="NotSupportedException"/> for the shared snapshot-only contract, and a minimal
/// round-trip proving <see cref="SimulatorAgentRuntime.EnqueueFrameAsync"/> actually delegates.
/// </summary>
[TestFixture]
public sealed class SimulatorAgentRuntimeTests
{
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");
    private static readonly BarInterval M1 = BarInterval.Minutes(1);

    private static (SimulatorAgentRuntime Runtime, StrategyWorkerHost Host) BuildRuntime(
        AnalysisProfileKey profile, AgentExecutionMode mode)
    {
        StrategySimulationSession session = StrategySimulationSession.Create(
            "always-observe",
            new AlwaysObserveAgent(),
            new SimulationOptions
            {
                StartingBalance = 100_000m,
                Leverage = 20m,
                CommissionRate = 0m,
                SpreadBasisPoints = 0m,
                SlippageBasisPoints = 0m,
                CloseOpenPositionsAtEnd = false
            },
            dataQualityOptions: new MarketDataQualityOptions { RequireIndicatorsReady = false, RejectGaps = false });
        var key = new AgentInstanceKey(AgentInstanceKey.SimulatorDeploymentId, Instrument, session.StrategyId, Guid.Empty, 0);
        var host = new StrategyWorkerHost(session, channelCapacity: 4, key);
        return (new SimulatorAgentRuntime(host, profile, mode), host);
    }

    private static AnalysisProfileKey Profile() =>
        AnalysisProfileKey.Create(new ChartAnnotationOptions(), new HashSet<BarInterval> { M1 }, "schema-v1");

    [Test]
    public void Key_AnalysisProfile_Mode_ForwardFromConstructorAndHost()
    {
        AnalysisProfileKey profile = Profile();
        (SimulatorAgentRuntime runtime, StrategyWorkerHost host) = BuildRuntime(profile, AgentExecutionMode.Executable);

        Assert.Multiple(() =>
        {
            Assert.That(runtime.Key, Is.SameAs(host.Key));
            Assert.That(runtime.AnalysisProfile, Is.EqualTo(profile));
            Assert.That(runtime.Mode, Is.EqualTo(AgentExecutionMode.Executable));
        });
    }

    [Test]
    public void EvaluateAsync_FromSnapshotAlone_ThrowsNotSupported()
    {
        (SimulatorAgentRuntime runtime, _) = BuildRuntime(Profile(), AgentExecutionMode.Shadow);
        var snapshot = new MarketAnalysisSnapshot
        {
            Instrument = Instrument,
            Profile = Profile(),
            SnapshotVersion = 1,
            DecisionEpoch = 1,
            AvailableAt = DateTimeOffset.UtcNow,
            Timeframes = new Dictionary<BarInterval, ChartAnnotator.Models.AnalysisSnapshot>(),
            CrossMarket = null,
            DataQuality = TradingCore.MarketData.DataQualityResult.Valid
        };

        Assert.ThrowsAsync<NotSupportedException>(async () => await runtime.EvaluateAsync(snapshot));
    }

    [Test]
    public async Task EnqueueFrameAsync_DelegatesToWrappedHost()
    {
        (SimulatorAgentRuntime runtime, StrategyWorkerHost host) = BuildRuntime(Profile(), AgentExecutionMode.Shadow);
        DateTimeOffset openTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var frame = new MarketFrame
        {
            Sequence = 1,
            AvailableAt = openTime.AddMinutes(1),
            ExecutionCandle = MarketCandle.FromMid(new Candle
            {
                Instrument = Instrument,
                Interval = M1,
                OpenTime = openTime,
                CloseTime = openTime.AddMinutes(1),
                Prices = new Ohlc(1.1m, 1.1005m, 1.0995m, 1.1002m),
                IsComplete = true
            }),
            ClosedIntervals = new HashSet<BarInterval>(),
            Snapshots = new Dictionary<BarInterval, ChartAnnotator.Models.AnalysisSnapshot>(),
            InputStreamId = "test-stream",
            IsWarmup = false,
            IsLastCandle = true
        };

        Task<StrategyFrameResult> pending = await runtime.EnqueueFrameAsync(frame);
        StrategyFrameResult result = await pending;

        Assert.Multiple(() =>
        {
            Assert.That(result.StrategyId, Is.EqualTo(host.StrategyId));
            Assert.That(result.Sequence, Is.EqualTo(1));
        });

        host.Complete();
        await host.DisposeAsync();
    }

    private sealed class AlwaysObserveAgent : ITradingAgent
    {
        public string Name => "always-observe";
        public IReadOnlySet<BarInterval> RequiredIntervals { get; } = new HashSet<BarInterval> { M1 };
        public BarInterval TriggerInterval => M1;
        public AgentExitManagementMode ExitManagementMode => AgentExitManagementMode.ProtectiveStopAndStrategyExit;

        public Task<AgentDecision> EvaluateAsync(AgentMarketContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentDecision
            {
                Action = AgentAction.Observe,
                Instrument = context.Instrument,
                Confidence = 0m,
                CreatedAt = context.Timestamp,
                Reason = "no setup"
            });
    }
}
