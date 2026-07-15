using Agent.Abstractions;
using Agent.Models;
using Brokers.Abstractions;
using Brokers.Models;
using ExecutionManager;
using RiskManager.Safety;
using TradingCore.MarketData;
using TradingCore.Pipeline;

namespace Simulator.Tests;

/// <summary>
/// Exercises the real <see cref="SafeTradingPipeline"/> end-to-end (agent -> safety ->
/// execution) to prove the equity-protection risk multiplier introduced in this pass
/// is actually applied where live backtests/live trading run it, not just reachable
/// in isolated <see cref="TradingSafetyController"/>/<see cref="TradeManager.StructureBasedTradeManager"/>
/// unit fixtures.
/// </summary>
[TestFixture]
public sealed class EquityProtectionPipelineTests
{
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");
    private static readonly DateTimeOffset Start = new(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task ActivatedRiskMultiplierTier_PreservesRequestedQuantityAndStampsSizingMultiplier()
    {
        var safety = new TradingSafetyController(new TradingSafetyOptions
        {
            EquityProtection = new EquityProtectionOptions
            {
                Enabled = true,
                Tiers =
                [
                    new EquityProtectionTier
                    {
                        TierId = "risk-1",
                        MaximumGivebackAmount = 100m,
                        Action = EquityProtectionAction.ReduceFutureRisk,
                        FutureRiskMultiplier = 0.4m
                    }
                ]
            }
        });
        safety.ObserveEquity(100_000m, Start);
        safety.ObserveEquity(100_500m, Start.AddHours(1));
        safety.ObserveEquity(100_350m, Start.AddHours(2)); // giveback 150 >= 100 -> tier active

        var decision = new AgentDecision
        {
            Action = AgentAction.Buy,
            Instrument = Instrument,
            SuggestedQuantity = 1_000m,
            Confidence = 80m,
            CreatedAt = Start,
            Reason = "test buy"
        };
        var execution = new RecordingExecutionCoordinator();
        var pipeline = new SafeTradingPipeline(
            new StubAgent(decision),
            execution,
            new AlwaysValidDataQualityGate(),
            safety);

        TradingPipelineResult result = await pipeline.ProcessAsync(Context(), new StubBrokerClient());

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(TradingPipelineStatus.Processed));
            Assert.That(result.Decision!.SuggestedQuantity, Is.EqualTo(1_000m));
            Assert.That(result.Decision!.EquityProtectionRiskMultiplier, Is.EqualTo(0.4m));
            Assert.That(result.Decision!.EquityProtectionActivatedTierIds, Is.EqualTo("risk-1"));
            Assert.That(execution.LastDecision, Is.SameAs(result.Decision));
        });
    }

    [Test]
    public async Task PauseNewEntriesTier_RejectsTheEntryEntirely()
    {
        var safety = new TradingSafetyController(new TradingSafetyOptions
        {
            EquityProtection = new EquityProtectionOptions
            {
                Enabled = true,
                Tiers =
                [
                    new EquityProtectionTier
                    {
                        TierId = "pause-1",
                        MaximumGivebackAmount = 100m,
                        Action = EquityProtectionAction.PauseNewEntries
                    }
                ]
            }
        });
        safety.ObserveEquity(100_000m, Start);
        safety.ObserveEquity(100_500m, Start.AddHours(1));
        safety.ObserveEquity(100_350m, Start.AddHours(2)); // breaches and pauses

        var decision = new AgentDecision
        {
            Action = AgentAction.Buy,
            Instrument = Instrument,
            SuggestedQuantity = 1_000m,
            Confidence = 80m,
            CreatedAt = Start,
            Reason = "test buy"
        };
        var execution = new RecordingExecutionCoordinator();
        var pipeline = new SafeTradingPipeline(
            new StubAgent(decision),
            execution,
            new AlwaysValidDataQualityGate(),
            safety);

        TradingPipelineResult result = await pipeline.ProcessAsync(Context(), new StubBrokerClient());

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(TradingPipelineStatus.RejectedBySafety));
            Assert.That(execution.ProcessCallCount, Is.Zero, "A rejected entry must never reach execution.");
        });
    }

    [Test]
    public async Task Disabled_ProducesByteIdenticalDecisionToBaseline()
    {
        var safety = new TradingSafetyController(new TradingSafetyOptions());
        safety.ObserveEquity(100_000m, Start);
        safety.ObserveEquity(50_000m, Start.AddHours(1)); // a large drawdown, but the feature is disabled

        var decision = new AgentDecision
        {
            Action = AgentAction.Buy,
            Instrument = Instrument,
            SuggestedQuantity = 1_000m,
            Confidence = 80m,
            CreatedAt = Start,
            Reason = "test buy"
        };
        var pipeline = new SafeTradingPipeline(
            new StubAgent(decision),
            new RecordingExecutionCoordinator(),
            new AlwaysValidDataQualityGate(),
            safety);

        TradingPipelineResult result = await pipeline.ProcessAsync(Context(), new StubBrokerClient());

        Assert.Multiple(() =>
        {
            Assert.That(result.Decision!.SuggestedQuantity, Is.EqualTo(1_000m));
            Assert.That(result.Decision!.EquityProtectionRiskMultiplier, Is.Null);
            Assert.That(result.Decision!.EquityProtectionActivatedTierIds, Is.Null);
        });
    }

    private static AgentMarketContext Context() => new()
    {
        Instrument = Instrument,
        Timestamp = Start,
        Analysis = new MultiTimeframeAnalysis(Instrument, Start, new Dictionary<BarInterval, ChartAnnotator.Models.AnalysisSnapshot>()),
        Account = new AccountSnapshot { AccountId = "a", Balance = 100_000m, CanTrade = true },
        Positions = [],
        OpenOrders = []
    };

    private sealed class StubAgent(AgentDecision decision) : ITradingAgent
    {
        public string Name => "stub";
        public IReadOnlySet<BarInterval> RequiredIntervals { get; } = new HashSet<BarInterval>();
        public BarInterval TriggerInterval => BarInterval.Minutes(5);
        public AgentExitManagementMode ExitManagementMode => AgentExitManagementMode.Bracket;

        public Task<AgentDecision> EvaluateAsync(
            AgentMarketContext context,
            CancellationToken cancellationToken = default) => Task.FromResult(decision);
    }

    private sealed class RecordingExecutionCoordinator : IExecutionCoordinator
    {
        public int ProcessCallCount { get; private set; }
        public AgentDecision? LastDecision { get; private set; }

        public Task<OrderSubmission?> ProcessAsync(
            AgentDecision decision,
            ITradingBrokerClient broker,
            CancellationToken cancellationToken = default)
        {
            ProcessCallCount++;
            LastDecision = decision;
            return Task.FromResult<OrderSubmission?>(null);
        }

        public Task<ProtectiveStopAmendmentResult> AmendProtectiveStopAsync(
            ProtectiveStopAmendmentCommand command,
            ITradingBrokerClient broker,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class AlwaysValidDataQualityGate : IMarketDataQualityGate
    {
        public DataQualityResult Evaluate(MultiTimeframeAnalysis analysis) => DataQualityResult.Valid;
        public void Reset(InstrumentKey? instrument = null)
        {
        }
    }

    /// <summary>
    /// A broker reference that is never actually used - the stub execution
    /// coordinator ignores it - but SafeTradingPipeline requires a non-null instance.
    /// </summary>
    private sealed class StubBrokerClient : ITradingBrokerClient
    {
        public BrokerDescriptor Descriptor => throw new NotSupportedException();
        public IMarketDataClient MarketData => throw new NotSupportedException();
        public IAccountClient Accounts => throw new NotSupportedException();
        public IPositionClient Positions => throw new NotSupportedException();
        public ICostClient Costs => throw new NotSupportedException();
        IOrderClient IBrokerClient.Orders => throw new NotSupportedException();
        public ITradingOrderClient Orders => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
