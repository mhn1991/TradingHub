using Agent.Abstractions;
using Agent.Models;
using Brokers.Models;
using ChartAnnotator.Models;
using Simulator.Engine;
using Simulator.MarketData;
using Simulator.Models;
using TradeManager;
using TradingCore.MarketData;

namespace Simulator.Tests;

/// <summary>
/// Proves <see cref="BacktestRuntimeOptions.DetailedExcursionTracking"/> is a true opt-in:
/// off by default (existing 645-test baseline is the regression proof - none of it changed
/// behavior), and when on, populates a real bar-by-bar MFE/MAE path.
/// </summary>
[TestFixture]
public sealed class SimulatedTradeRecordExcursionPathTests
{
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");
    private static readonly BarInterval Interval = BarInterval.Minutes(1);
    private static readonly DateTimeOffset Start = new(2026, 3, 2, 8, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task Disabled_ExcursionPathStaysNull()
    {
        await using StrategySimulationSession session = CreateSession(detailedExcursionTracking: false);

        await session.ProcessFrameAsync(SessionFrame(1, 100m, 101m, 99m, 100m));
        await session.ProcessFrameAsync(SessionFrame(2, 100m, 103m, 100m, 102m));
        await session.BuildSimulationResultAsync();

        Assert.That(session.Trades, Has.Count.EqualTo(1));
        Assert.That(session.Trades[0].ExcursionPath, Is.Null);
    }

    [Test]
    public async Task Enabled_PopulatesMonotonicPathWithMatchingMfe()
    {
        await using StrategySimulationSession session = CreateSession(detailedExcursionTracking: true);

        await session.ProcessFrameAsync(SessionFrame(1, 100m, 101m, 99m, 100m));
        await session.ProcessFrameAsync(SessionFrame(2, 100m, 103m, 100m, 102m));
        await session.ProcessFrameAsync(SessionFrame(3, 102m, 105m, 101m, 104m));
        await session.BuildSimulationResultAsync();

        Assert.That(session.Trades, Has.Count.EqualTo(1));
        IReadOnlyList<SimulatedTradePathPoint>? path = session.Trades[0].ExcursionPath;
        Assert.That(path, Is.Not.Null);
        Assert.That(path, Is.Not.Empty);

        Assert.Multiple(() =>
        {
            // Strictly increasing bar index, starting at 0.
            for (int i = 0; i < path!.Count; i++)
                Assert.That(path[i].BarsAfterEntry, Is.EqualTo(i));

            // The trade rallied every frame - MFE-R at the final path point must equal the
            // trade's own peak MaximumFavourableExcursionR (both derived from the same data).
            decimal finalPathMfe = path![^1].MfeR;
            Assert.That(finalPathMfe, Is.EqualTo(session.Trades[0].MaximumFavourableExcursionR ?? 0m));
            // MFE-R never decreases bar-over-bar in a monotonic rally.
            for (int i = 1; i < path.Count; i++)
                Assert.That(path[i].MfeR, Is.GreaterThanOrEqualTo(path[i - 1].MfeR));
        });
    }

    private static StrategySimulationSession CreateSession(bool detailedExcursionTracking) =>
        StrategySimulationSession.Create(
            "excursion-path-test",
            new BuyOnceAgent(Interval),
            new SimulationOptions
            {
                StartingBalance = 100_000m,
                Leverage = 20m,
                CommissionRate = 0m,
                SpreadBasisPoints = 0m,
                SlippageBasisPoints = 0m,
                CloseOpenPositionsAtEnd = true
            },
            dataQualityOptions: new MarketDataQualityOptions
            {
                RequireIndicatorsReady = false,
                RejectGaps = false
            },
            positionManagementOptions: new PositionManagementOptions
            {
                Mode = TrailingStopMode.Disabled,
                ManagementInterval = Interval
            },
            detailedExcursionTracking: detailedExcursionTracking);

    private static MarketFrame SessionFrame(long sequence, decimal open, decimal high, decimal low, decimal close)
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
            InputStreamId = "excursion-path-test-stream",
            IsWarmup = false,
            IsLastCandle = false
        };
    }

    private sealed class BuyOnceAgent(BarInterval interval) : ITradingAgent
    {
        private bool _submitted;
        public string Name => "Excursion path test agent";
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
            return Task.FromResult(new AgentDecision
            {
                StrategyName = Name,
                SetupId = "excursion-path-buy",
                SetupStartedAt = context.Timestamp,
                SignalInterval = interval,
                Action = AgentAction.Buy,
                Instrument = context.Instrument,
                SuggestedQuantity = 1m,
                QuantityUnit = QuantityUnit.Units,
                OrderType = StandardOrderType.Market,
                ReferencePrice = 100m,
                StopLossPrice = 98m,
                Confidence = 100m,
                CreatedAt = context.Timestamp,
                Reason = "Deterministic excursion-path entry"
            });
        }
    }
}
