using Agent.Abstractions;
using Agent.Models;
using Brokers.Models;
using ChartAnnotator.Models;
using ChartAnnotator.TargetManagement;
using Simulator.Engine;
using Simulator.MarketData;
using Simulator.Models;
using TradeManager;
using TradingCore.MarketData;

namespace Simulator.Tests;

/// <summary>
/// Drives a real <see cref="StrategySimulationSession"/> (not just the isolated builder/manager
/// unit tests) through a deterministic v2 PartialThenRunner trade, proving Phase 3 routing
/// (decision.TakeProfitPrice null, ExitPolicy/TargetPlan populated), Phase 4 management (a real
/// target-aware checkpoint partial fires exactly once), and Phase 5 reporting (PlannedR/RealizedR/
/// InitialRiskCash populated on the closed trade) all work together end-to-end.
/// </summary>
[TestFixture]
public sealed class AdaptiveTargetManagementEndToEndTests
{
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");
    private static readonly BarInterval Interval = BarInterval.Minutes(1);
    private static readonly DateTimeOffset Start = new(2026, 3, 2, 8, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task PartialThenRunnerTrade_PartialsAtCheckpointAndReportsPlannedAndRealizedR()
    {
        await using StrategySimulationSession session = StrategySimulationSession.Create(
            "adaptive-target-e2e-test",
            new BuyOnceAdaptivePartialAgent(Interval),
            new SimulationOptions
            {
                StartingBalance = 100_000m,
                Leverage = 20m,
                CommissionRate = 0m,
                SpreadBasisPoints = 0m,
                SlippageBasisPoints = 0m,
                CloseOpenPositionsAtEnd = true
            },
            dataQualityOptions: new MarketDataQualityOptions { RequireIndicatorsReady = false, RejectGaps = false },
            positionManagementOptions: new PositionManagementOptions
            {
                Mode = TrailingStopMode.Disabled,
                ManagementInterval = Interval
            });

        // Entry ~100, stop 98 (risk~2). Rally through the 103 checkpoint (R=1.5), then further to
        // 108 before the stream ends and the remaining runner is closed.
        await session.ProcessFrameAsync(Frame(1, 100m, 101m, 99m, 100m));
        await session.ProcessFrameAsync(Frame(2, 100m, 104m, 100m, 103.5m));
        await session.ProcessFrameAsync(Frame(3, 103.5m, 109m, 103m, 108m, isLastCandle: true));
        await session.BuildSimulationResultAsync();

        Assert.That(session.Trades, Has.Count.EqualTo(1));
        SimulatedTradeRecord trade = session.Trades[0];

        Assert.Multiple(() =>
        {
            Assert.That(trade.TakeProfitPrice, Is.Null, "a managed policy must never submit a hard broker target");
            Assert.That(trade.ExitPolicy, Is.EqualTo(TradeExitPolicy.PartialThenRunner));
            Assert.That(trade.TargetPlan, Is.Not.Null);
            Assert.That(trade.PartialExits, Has.Count.EqualTo(1));
            Assert.That(trade.PartialExits[0].Reason, Is.EqualTo(PartialExitReason.AdaptiveTargetCheckpoint));
            // Desired 25% of 10 units is 2.5, rounded down to the resolved quantity increment.
            Assert.That(trade.PartialExits[0].QuantityClosed, Is.EqualTo(2m));
            Assert.That(trade.RemainingQuantity, Is.EqualTo(0m), "the runner must be fully closed once the stream ends");
            Assert.That(trade.PlannedR, Is.EqualTo(trade.TargetPlan!.PlannedR));
            Assert.That(trade.RealizedR, Is.Not.Null);
            Assert.That(trade.InitialRiskCash, Is.Not.Null.And.GreaterThan(0m));
        });
    }

    private static MarketFrame Frame(
        long sequence, decimal open, decimal high, decimal low, decimal close, bool isLastCandle = false)
    {
        DateTimeOffset openTime = Start.AddMinutes(sequence - 1);
        Candle candle = TestCandles.Create(Instrument, openTime, Interval, open, high, low, close);
        AnalysisSnapshot snapshot = new()
        {
            Instrument = Instrument,
            Interval = Interval,
            AvailableAt = candle.CloseTime!.Value,
            Version = sequence,
            LatestCandle = candle,
            Indicators = new IndicatorSnapshot { Atr = 1m },
            Swings = [],
            PriceZones = [],
            Trendlines = [],
            Channels = [],
            MarketStructure = new MarketStructureSnapshot(),
            Confidence = new ConfidenceScore { Total = 0m, Contributions = [] }
        };
        return new MarketFrame
        {
            Sequence = sequence,
            AvailableAt = candle.CloseTime.Value,
            ExecutionCandle = MarketCandle.FromMid(candle),
            AnalysisBaseCandle = candle,
            ClosedIntervals = new HashSet<BarInterval> { Interval },
            Snapshots = new Dictionary<BarInterval, AnalysisSnapshot> { [Interval] = snapshot },
            InputStreamId = "adaptive-target-e2e-stream",
            IsWarmup = false,
            IsLastCandle = isLastCandle
        };
    }

    /// <summary>Submits one deterministic v2 PartialThenRunner buy, mirroring what Phase 3's playbook routing now produces.</summary>
    private sealed class BuyOnceAdaptivePartialAgent(BarInterval interval) : ITradingAgent
    {
        private bool _submitted;
        public string Name => "Adaptive target e2e test agent";
        public IReadOnlySet<BarInterval> RequiredIntervals { get; } = new HashSet<BarInterval> { interval };
        public BarInterval TriggerInterval => interval;
        public AgentExitManagementMode ExitManagementMode => AgentExitManagementMode.ProtectiveStopAndStrategyExit;

        public Task<AgentDecision> EvaluateAsync(
            AgentMarketContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_submitted)
            {
                return Task.FromResult(new AgentDecision
                {
                    Action = AgentAction.Observe,
                    Instrument = context.Instrument,
                    Confidence = 0m,
                    CreatedAt = context.Timestamp,
                    Reason = "Entry already submitted"
                });
            }

            _submitted = true;
            var plan = new TradeTargetPlan
            {
                PlanVersion = 1,
                Revision = 0,
                ExitPolicy = TradeExitPolicy.PartialThenRunner,
                OriginalEntry = 100m,
                OriginalStop = 98m,
                InitialRiskPrice = 2m,
                SelectedCheckpointId = "checkpoint-1",
                SelectedTerminalId = "terminal-1",
                Candidates =
                [
                    new TradeTargetCandidate
                    {
                        CandidateId = "checkpoint-1",
                        ClusterId = "checkpoint-1",
                        SourceKind = TradeTargetSourceKind.Swing,
                        SourceId = "swing-1",
                        SourceInterval = interval,
                        LifecycleState = "Confirmed",
                        LowerBoundary = 103m,
                        UpperBoundary = 103m,
                        ExecutionPrice = 103m,
                        Role = TradeTargetRole.Checkpoint,
                        Tier = TradeTargetSignificanceTier.TierC,
                        DistanceAtr = 1.5m,
                        TargetR = 1.5m,
                        AvailableAt = context.Timestamp
                    }
                ],
                PartialFraction = 0.25m,
                MinimumRunnerFraction = 0.5m,
                PlannedR = 0.25m * 1.5m + 0.75m * 3m,
                ConservativeOpportunityR = 3m,
                CreatedAt = context.Timestamp,
                LastRevisedAt = context.Timestamp
            };
            return Task.FromResult(new AgentDecision
            {
                StrategyName = Name,
                SetupId = "adaptive-target-e2e-buy",
                SetupStartedAt = context.Timestamp,
                SignalInterval = interval,
                Action = AgentAction.Buy,
                Instrument = context.Instrument,
                SuggestedQuantity = 10m,
                QuantityUnit = QuantityUnit.Units,
                OrderType = StandardOrderType.Market,
                ReferencePrice = 100m,
                StopLossPrice = 98m,
                TakeProfitPrice = null,
                ExitPolicy = TradeExitPolicy.PartialThenRunner,
                TargetPlan = plan,
                Confidence = 100m,
                CreatedAt = context.Timestamp,
                Reason = "Deterministic adaptive target-management entry"
            });
        }
    }
}
