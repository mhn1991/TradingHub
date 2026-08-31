using Agent.Models;
using Agent.Strategies.BreakoutDetector;
using Brokers.Models;
using ChartAnnotator.Models;
using NUnit.Framework;

namespace Simulator.Tests;

/// <summary>
/// Exercises the end-of-rising-move condition in isolation: upper-band tag + RSI still rising +
/// CCI rolling over. Deterministic and data-free, so it answers "does my rule fire when I think
/// it should" in milliseconds - which a backtest cannot, because a zero-trade run there is
/// equally consistent with "rule never true", "warm-up", and "risk layer rejected it".
/// </summary>
[TestFixture]
public sealed class BreakoutDetectorSignalTests
{
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");
    private static readonly BarInterval Context = BarInterval.Minutes(15);
    private static readonly BarInterval Trigger = BarInterval.Minutes(5);
    private static readonly BarInterval Confirm = BarInterval.Minutes(1);
    private static readonly DateTimeOffset Now = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

    private static BreakoutDetectorAgent Agent() => new(new BreakoutDetectorStrategyOptions());

    /// <param name="high">Candle high — at/above upperBand tags the band.</param>
    private static AnalysisSnapshot Snapshot(
        BarInterval interval,
        decimal high = 110m,
        decimal low = 107m,
        decimal close = 109m,
        decimal? lowerBand = 105m,
        decimal? upperBand = 109.5m,
        decimal? rsi = 61m,
        decimal? previousRsi = 60m,
        decimal? cci = 90m,
        decimal? previousCci = 120m,
        decimal? atr = 1m,
        MarketStructureDirection structure = MarketStructureDirection.Unknown,
        decimal open = 108m) => new()
    {
        Instrument = Instrument,
        Interval = interval,
        AvailableAt = Now,
        Version = 1,
        MarketStructure = new MarketStructureSnapshot { Direction = structure },
        LatestCandle = TestCandles.Create(Instrument, Now.AddMinutes(-5), interval, open, high, low, close),
        Indicators = new IndicatorSnapshot
        {
            Atr = atr,
            Rsi = rsi,
            Cci = cci,
            BollingerUpper = upperBand,
            BollingerMiddle = 107m,
            BollingerLower = lowerBand,
            RsiAnalysis = new RsiAnalysisSnapshot { PreviousValue = previousRsi },
            CciAnalysis = new CciAnalysisSnapshot { PreviousValue = previousCci }
        },
        Swings = [],
        PriceZones = [],
        Trendlines = [],
        Channels = [],
        Confidence = new ConfidenceScore { Total = 70m, Contributions = [] }
    };

    /// <param name="structure">15m structure the signal must agree with (Rising qualifies a long).</param>
    /// <param name="confirmUp">true makes the 1m candle close up, confirming a long.</param>
    private static AgentMarketContext Context3(
        AnalysisSnapshot trigger,
        IReadOnlyList<BrokerPosition>? positions = null,
        MarketStructureDirection structure = MarketStructureDirection.Unknown,
        bool confirmUp = true) => new()
    {
        Instrument = Instrument,
        Timestamp = Now,
        Analysis = new MultiTimeframeAnalysis(Instrument, Now, new Dictionary<BarInterval, AnalysisSnapshot>
        {
            [Trigger] = trigger,
            [Context] = Snapshot(Context, structure: structure),
            [Confirm] = Snapshot(Confirm, open: confirmUp ? 100m : 120m, close: 110m)
        }),
        Account = new AccountSnapshot
        {
            AccountId = "a", Currency = "USD", Balance = 100_000m, Available = 100_000m, CanTrade = true
        },
        Positions = positions ?? [],
        OpenOrders = []
    };

    [Test]
    public async Task Fires_WhenBandTagged_RsiRising_CciRollingOver()
    {
        AgentDecision decision = await Agent().EvaluateAsync(Context3(Snapshot(Trigger),
            structure: MarketStructureDirection.Falling, confirmUp: false));

        Assert.That(decision.Action, Is.EqualTo(AgentAction.Sell), decision.Reason);
    }

    [Test]
    public async Task Sell_PlacesStopAboveAndTargetBelowEntry()
    {
        // The geometry PreTradeRiskManager enforces. Getting it backwards yields zero trades in a
        // backtest rather than an error, so it is pinned here instead.
        AgentDecision decision = await Agent().EvaluateAsync(Context3(Snapshot(Trigger),
            structure: MarketStructureDirection.Falling, confirmUp: false));

        Assert.Multiple(() =>
        {
            Assert.That(decision.StopLossPrice!.Value, Is.GreaterThan(decision.ReferencePrice!.Value));
            Assert.That(decision.TakeProfitPrice!.Value, Is.LessThan(decision.ReferencePrice!.Value));
            Assert.That(decision.ExpectedRewardRisk, Is.GreaterThanOrEqualTo(1.5m));
        });
    }

    // decimal is not a valid attribute argument type, so the cases are declared as double.
    [TestCase(108d, 61d, 60d, 90d, 120d, TestName = "high below the upper band")]
    [TestCase(110d, 59d, 60d, 90d, 120d, TestName = "RSI falling, not rising")]
    [TestCase(110d, 61d, 60d, 130d, 120d, TestName = "CCI rising, not rolling over")]
    public async Task DoesNotFire_WhenAnyLegIsMissing(
        double high, double rsi, double previousRsi, double cci, double previousCci)
    {
        AgentDecision decision = await Agent().EvaluateAsync(Context3(
            Snapshot(
                Trigger,
                high: (decimal)high,
                rsi: (decimal)rsi,
                previousRsi: (decimal)previousRsi,
                cci: (decimal)cci,
                previousCci: (decimal)previousCci)));

        Assert.That(decision.Action, Is.EqualTo(AgentAction.Observe), decision.Reason);
    }

    [Test]
    public async Task Warmup_ReportsWhichIndicatorIsMissing_RatherThanASilentNoSignal()
    {
        // The failure mode GetValueOrDefault() would have hidden: a null indicator must produce a
        // distinct, diagnosable reason, not a false that looks like a genuine no-signal bar.
        AgentDecision noRsi = await Agent().EvaluateAsync(Context3(Snapshot(Trigger, rsi: null)));
        AgentDecision noPrevious = await Agent().EvaluateAsync(Context3(Snapshot(Trigger, previousRsi: null)));
        AgentDecision noBand = await Agent().EvaluateAsync(Context3(Snapshot(Trigger, upperBand: null)));

        Assert.Multiple(() =>
        {
            Assert.That(noRsi.Reason, Does.Contain("RSI"));
            Assert.That(noPrevious.Reason, Does.Contain("previous-candle"));
            Assert.That(noBand.Reason, Does.Contain("Bollinger"));
        });
    }

    [Test]
    public async Task Fires_Buy_WhenLowerBandTagged_RsiFalling_CciTurningUp()
    {
        // Mirror of the sell case: price makes its low while CCI turns up.
        AgentDecision decision = await Agent().EvaluateAsync(Context3(Snapshot(
            Trigger, high: 108m, low: 104m, close: 105m,
            rsi: 39m, previousRsi: 40m, cci: 120m, previousCci: 90m),
            structure: MarketStructureDirection.Rising, confirmUp: true));

        Assert.That(decision.Action, Is.EqualTo(AgentAction.Buy), decision.Reason);
    }

    [Test]
    public async Task Buy_PlacesStopBelowAndTargetAboveEntry()
    {
        AgentDecision decision = await Agent().EvaluateAsync(Context3(Snapshot(
            Trigger, high: 108m, low: 104m, close: 105m,
            rsi: 39m, previousRsi: 40m, cci: 120m, previousCci: 90m),
            structure: MarketStructureDirection.Rising, confirmUp: true));

        Assert.Multiple(() =>
        {
            Assert.That(decision.StopLossPrice!.Value, Is.LessThan(decision.ReferencePrice!.Value));
            Assert.That(decision.TakeProfitPrice!.Value, Is.GreaterThan(decision.ReferencePrice!.Value));
            Assert.That(decision.ExpectedRewardRisk, Is.GreaterThanOrEqualTo(1.5m));
        });
    }

    [Test]
    public async Task DoesNotFire_WhenMomentumIsFlat()
    {
        // Previously this fixture fired BOTH readings at once: with >= and <= an unchanged RSI and
        // CCI satisfied "still rising" and "already rolling over" simultaneously, so a candle with
        // no momentum information at all produced a signal (measured at 1.8% of live signals).
        // The comparisons are now strict, which makes the two readings mutually exclusive - flat
        // momentum is simply no evidence, and the ambiguity branch is unreachable by construction.
        AgentDecision decision = await Agent().EvaluateAsync(Context3(Snapshot(
            Trigger, high: 110m, low: 104m, close: 107m,
            rsi: 50m, previousRsi: 50m, cci: 100m, previousCci: 100m)));

        Assert.Multiple(() =>
        {
            Assert.That(decision.Action, Is.EqualTo(AgentAction.Observe));
            Assert.That(decision.Reason, Does.Contain("No end-of-move signal"));
        });
    }

    [Test]
    public async Task DoesNotReSignal_WhileAPositionIsOpen()
    {
        var open = new BrokerPosition
        {
            PositionId = "p1", Instrument = Instrument, Side = OrderSide.Sell, Quantity = 1_000m
        };

        AgentDecision decision = await Agent().EvaluateAsync(Context3(Snapshot(Trigger), [open]));

        Assert.That(decision.Action, Is.EqualTo(AgentAction.Observe), decision.Reason);
    }
}
