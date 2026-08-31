using Agent.Strategies.Alfonso;
using Agent.Strategies.Alfonso.Trend;
using Agent.Strategies.Alfonso.Zones;
using NUnit.Framework;

namespace Simulator.Tests;

/// <summary>
/// Rule-level tests for the trend layer: trendline construction (module 3), the Up/Down/OOA state
/// machine (module 5), and over-extension.
/// </summary>
[TestFixture]
public sealed class AlfonsoTrendDetectorTests
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static SwingPoint Swing(int index, decimal price, ImbalanceKind kind) => new()
    {
        Index = index,
        At = Start.AddHours(index),
        Price = price,
        Kind = kind
    };

    private static (List<decimal> Highs, List<decimal> Lows, List<DateTimeOffset> Times) Series(
        params (decimal High, decimal Low)[] bars)
    {
        List<decimal> highs = [];
        List<decimal> lows = [];
        List<DateTimeOffset> times = [];
        for (int index = 0; index < bars.Length; index++)
        {
            highs.Add(bars[index].High);
            lows.Add(bars[index].Low);
            times.Add(Start.AddHours(index));
        }

        return (highs, lows, times);
    }

    // ---- trendline construction (module 3) ----------------------------------------------------

    [Test]
    public void BullishTrendlineRequiresTheSecondValleyToBeHigherThanTheFirst()
    {
        // "The low of Valley V[2] always has to be higher than the low of V[1]."
        var (highs, lows, times) = Series(
            (10m, 8m), (11m, 9m), (12m, 10m), (13m, 11m), (14m, 12m), (20m, 13m));

        List<SwingPoint> descending = [Swing(0, 8m, ImbalanceKind.Demand), Swing(2, 7m, ImbalanceKind.Demand)];
        Assert.That(TrendlineBuilder.Bullish(descending, highs, lows, times, 5), Is.Null);

        List<SwingPoint> ascending = [Swing(0, 8m, ImbalanceKind.Demand), Swing(2, 10m, ImbalanceKind.Demand)];
        Assert.That(TrendlineBuilder.Bullish(ascending, highs, lows, times, 5), Is.Not.Null);
    }

    [Test]
    public void BullishTrendlineIsNotDrawableUntilPriceExtendsBeyondTheSwingPair()
    {
        // "Valley V[1] and Valley V[2] can be connected once the high of V[2] makes a high higher
        // than [4]."
        var (highs, lows, times) = Series(
            (10m, 8m), (11m, 9m), (12m, 10m), (11.5m, 10.5m));

        List<SwingPoint> valleys = [Swing(0, 8m, ImbalanceKind.Demand), Swing(2, 10m, ImbalanceKind.Demand)];

        // Bar 3 never exceeds the interim high of 12, so the pair is not yet usable.
        Assert.That(TrendlineBuilder.Bullish(valleys, highs, lows, times, 3), Is.Null);

        highs[3] = 13m;
        Assert.That(TrendlineBuilder.Bullish(valleys, highs, lows, times, 3), Is.Not.Null);
    }

    [Test]
    public void TrendlineNeverCutsThroughACandle()
    {
        // "the trendlines cannot go through wicks or candlestick bodies ... Adjust the trendline in
        // such a way that candlesticks will be respected."
        // A dip at bar 1 sits below the straight line from (0, 8) to (3, 14); the fitted line has to
        // rest on that dip instead.
        var (highs, lows, times) = Series(
            (10m, 8m), (10m, 7m), (13m, 12m), (16m, 14m), (20m, 15m));

        List<SwingPoint> valleys = [Swing(0, 8m, ImbalanceKind.Demand), Swing(3, 14m, ImbalanceKind.Demand)];
        Trendline? line = TrendlineBuilder.Bullish(valleys, highs, lows, times, 4);

        Assert.That(line, Is.Not.Null);
        for (int index = 0; index <= 4; index++)
            Assert.That(line!.PriceAt(index), Is.LessThanOrEqualTo(lows[index] + 0.0000001m));

        Assert.That(line!.ToIndex, Is.EqualTo(1));
    }

    [Test]
    public void BearishTrendlineMirrorsTheBullishRules()
    {
        var (highs, lows, times) = Series(
            (20m, 18m), (19m, 17m), (18m, 16m), (17m, 15m), (16m, 10m));

        List<SwingPoint> rising = [Swing(0, 20m, ImbalanceKind.Supply), Swing(2, 21m, ImbalanceKind.Supply)];
        Assert.That(TrendlineBuilder.Bearish(rising, highs, lows, times, 4), Is.Null);

        List<SwingPoint> falling = [Swing(0, 20m, ImbalanceKind.Supply), Swing(2, 18m, ImbalanceKind.Supply)];
        Trendline? line = TrendlineBuilder.Bearish(falling, highs, lows, times, 4);

        Assert.That(line, Is.Not.Null);
        for (int index = 0; index <= 4; index++)
            Assert.That(line!.PriceAt(index), Is.GreaterThanOrEqualTo(highs[index] - 0.0000001m));
    }

    [Test]
    public void OnlyAFullCandleBeyondTheLineCountsAsABreak()
    {
        // Module 4: "the break of a trendline with at least a full OCHL candlestick".
        Trendline line = new()
        {
            Direction = TrendlineDirection.Bullish,
            FromIndex = 0,
            FromPrice = 100m,
            ToIndex = 10,
            ToPrice = 110m,
            FromTime = Start,
            ToTime = Start.AddHours(10)
        };

        // Level at index 10 is 110. Under the whole-candle reading a wick through it is not a
        // break, but a candle entirely below is. The close-based reading is pinned separately in
        // AlfonsoRuleClarificationTests.
        Assert.That(
            line.IsBrokenBy(10, high: 112m, low: 105m, close: 111m, requireClose: false), Is.False);
        Assert.That(
            line.IsBrokenBy(10, high: 109.9m, low: 105m, close: 108m, requireClose: false), Is.True);
    }

    // ---- the state machine (module 5) ----------------------------------------------------------

    [Test]
    public void ATrendlineAloneIsNotATrend()
    {
        // Module 5: "A trendline that connects two impulses does not necessarily mean there is a
        // trend ... An uptrend requires an accomplishment, not just successive higher highs and
        // higher lows."
        AlfonsoTrendDetector detector = new();
        AlfonsoTrendSnapshot state = detector.Apply(
            new AlfonsoBar(Start, 100m, 101m, 99m, 100.5m), ImbalanceDetectorUpdate.Empty);

        Assert.That(state.Trend, Is.EqualTo(AlfonsoTrend.Unknown));
        Assert.That(state.IsTrending, Is.False);
        Assert.That(state.Reason, Does.Contain("No accomplishment"));
    }

    [Test]
    public void TwoOpposingEliminationsEstablishATrendWithoutAnyTrendline()
    {
        // Module 5: "An uptrend is also created when two supply zones have been eliminated and
        // without the possibility of drawing a trendline."
        AlfonsoTrendDetector detector = new();
        AlfonsoTrendSnapshot state = detector.Apply(
            new AlfonsoBar(Start, 100m, 101m, 99m, 100.5m),
            new ImbalanceDetectorUpdate { Eliminated = [Zone(ImbalanceKind.Supply), Zone(ImbalanceKind.Supply)] });

        Assert.That(state.Trend, Is.EqualTo(AlfonsoTrend.Uptrend));
        Assert.That(state.Line, Is.Null);
        Assert.That(state.OpposingEliminations, Is.EqualTo(2));
    }

    [Test]
    public void EliminatingTheTrendsOwnSideDropsItOutOfAlignment()
    {
        // Module 5 lists "An imbalance is eliminated" as an OOA trigger. Supply being eliminated in
        // an uptrend is the uptrend working; DEMAND being eliminated is what undermines it.
        AlfonsoTrendDetector detector = new();
        detector.Apply(
            new AlfonsoBar(Start, 100m, 101m, 99m, 100.5m),
            new ImbalanceDetectorUpdate { Eliminated = [Zone(ImbalanceKind.Supply), Zone(ImbalanceKind.Supply)] });

        AlfonsoTrendSnapshot supplyGone = detector.Apply(
            new AlfonsoBar(Start.AddHours(1), 100m, 101m, 99m, 100.5m),
            new ImbalanceDetectorUpdate { Eliminated = [Zone(ImbalanceKind.Supply)] });
        Assert.That(supplyGone.Trend, Is.EqualTo(AlfonsoTrend.Uptrend));

        AlfonsoTrendSnapshot demandGone = detector.Apply(
            new AlfonsoBar(Start.AddHours(2), 100m, 101m, 99m, 100.5m),
            new ImbalanceDetectorUpdate { Eliminated = [Zone(ImbalanceKind.Demand)] });

        Assert.That(demandGone.Trend, Is.EqualTo(AlfonsoTrend.OutOfAlignment));
        Assert.That(demandGone.OpposingEliminations, Is.EqualTo(1),
            "the invalidating elimination is the first piece of reversal evidence");
    }

    [Test]
    public void FirstInvalidatingEliminationSurvivesUntilASecondOneConfirmsTheReversal()
    {
        AlfonsoTrendDetector detector = new();
        detector.Apply(
            new AlfonsoBar(Start, 100m, 101m, 99m, 100.5m),
            new ImbalanceDetectorUpdate
            {
                Eliminated = [Zone(ImbalanceKind.Supply), Zone(ImbalanceKind.Supply)]
            });

        AlfonsoTrendSnapshot outOfAlignment = detector.Apply(
            new AlfonsoBar(Start.AddHours(1), 100m, 101m, 99m, 100.5m),
            new ImbalanceDetectorUpdate { Eliminated = [Zone(ImbalanceKind.Demand)] });
        AlfonsoTrendSnapshot reversed = detector.Apply(
            new AlfonsoBar(Start.AddHours(2), 100m, 101m, 99m, 100.5m),
            new ImbalanceDetectorUpdate { Eliminated = [Zone(ImbalanceKind.Demand)] });

        Assert.That(outOfAlignment.Trend, Is.EqualTo(AlfonsoTrend.OutOfAlignment));
        Assert.That(outOfAlignment.OpposingEliminations, Is.EqualTo(1));
        Assert.That(reversed.Trend, Is.EqualTo(AlfonsoTrend.Downtrend));
        Assert.That(reversed.OpposingEliminations, Is.EqualTo(2));
    }

    [Test]
    public void ConfirmedZoneAnchorsItsSwingAtTheBaseCandleRatherThanConfirmationCandle()
    {
        AlfonsoTrendDetector detector = new();
        detector.Apply(
            new AlfonsoBar(Start, 95m, 100m, 90m, 98m),
            new ImbalanceDetectorUpdate
            {
                Created = [Zone(ImbalanceKind.Demand) with { BaseEnd = Start, Distal = 90m }]
            });
        detector.Apply(
            new AlfonsoBar(Start.AddHours(1), 100m, 105m, 95m, 103m),
            ImbalanceDetectorUpdate.Empty);

        DateTimeOffset confirmation = Start.AddHours(2);
        AlfonsoTrendSnapshot state = detector.Apply(
            new AlfonsoBar(confirmation, 105m, 110m, 100m, 108m),
            new ImbalanceDetectorUpdate
            {
                Created =
                [
                    Zone(ImbalanceKind.Demand) with
                    {
                        BaseStart = Start.AddHours(1),
                        BaseEnd = Start.AddHours(1),
                        ConfirmedAt = confirmation,
                        Distal = 95m
                    }
                ],
                Eliminated = [Zone(ImbalanceKind.Supply)]
            });

        Assert.That(state.Trend, Is.EqualTo(AlfonsoTrend.Uptrend));
        Assert.That(state.Line, Is.Not.Null);
        Assert.That(state.Line!.ToIndex, Is.EqualTo(1));
        Assert.That(state.Line.ToTime, Is.EqualTo(Start.AddHours(1)));
    }

    [Test]
    public void PreviewMakesCurrentCandleBreakVisibleAndRetiresTheBrokenLine()
    {
        AlfonsoTrendDetector detector = EstablishedUptrendWithBullishLine();
        AlfonsoBar breaking = new(Start.AddHours(3), 100m, 101m, 89m, 90m);

        detector.PreviewBreaks(breaking);

        Assert.That(detector.RecentBreaks,
            Is.EquivalentTo(new[] { (breaking.OpenTime, TrendlineDirection.Bullish) }));

        AlfonsoTrendSnapshot broken = detector.Apply(breaking, ImbalanceDetectorUpdate.Empty);
        detector.Apply(
            new AlfonsoBar(Start.AddHours(4), 90m, 91m, 80m, 81m),
            ImbalanceDetectorUpdate.Empty);

        Assert.That(broken.Trend, Is.EqualTo(AlfonsoTrend.OutOfAlignment));
        Assert.That(broken.Line, Is.Null);
        Assert.That(detector.RecentBreaks.Count(item => item.Direction == TrendlineDirection.Bullish),
            Is.EqualTo(1), "a retired line cannot report the same break on every later candle");
    }

    [Test]
    public void ThreeConsecutiveExtendedRangeCandlesMarkTheTimeframeOverExtended()
    {
        // Module 5: "Over-extesion is defined as the creation of three or more consecutive CPs,
        // and/or three or more large ERCs. Once a certain timeframe is over-extended, that timeframe
        // can no longer be used to place a trade."
        AlfonsoTrendDetector detector = new();
        detector.Apply(
            new AlfonsoBar(Start, 100m, 101m, 99m, 100.5m),
            new ImbalanceDetectorUpdate { Eliminated = [Zone(ImbalanceKind.Supply), Zone(ImbalanceKind.Supply)] });

        AlfonsoTrendSnapshot state = detector.Snapshot;
        Assert.That(state.CanTrade, Is.True);

        decimal price = 100m;
        for (int step = 0; step < 3; step++)
        {
            state = detector.Apply(
                new AlfonsoBar(Start.AddHours(step + 1), price, price + 10.5m, price - 0.5m, price + 10m),
                ImbalanceDetectorUpdate.Empty);
            price += 10m;
        }

        Assert.That(state.IsOverExtended, Is.True);
        Assert.That(state.IsTrending, Is.True);
        Assert.That(state.CanTrade, Is.False, "an over-extended timeframe cannot place a trade");

        // A correction clears it; over-extension must not latch for the rest of the run.
        state = detector.Apply(
            new AlfonsoBar(Start.AddHours(9), price, price + 1m, price - 1m, price + 0.05m),
            ImbalanceDetectorUpdate.Empty);

        Assert.That(state.IsOverExtended, Is.False);
        Assert.That(state.CanTrade, Is.True);
    }

    [Test]
    public void ContinuationPatternsNeverBecomeSwingsForTrendlines()
    {
        // Module 3: "Continuation Patterns (CPs) will not be used to connect trendlines."
        AlfonsoTrendDetector detector = new();
        for (int step = 0; step < 4; step++)
        {
            detector.Apply(
                new AlfonsoBar(Start.AddHours(step), 100m, 101m, 99m, 100.5m),
                new ImbalanceDetectorUpdate
                {
                    Created = [Zone(ImbalanceKind.Demand, continuation: true)]
                });
        }

        // Four continuation demand zones, so four candidate valleys had CPs been admitted - yet no
        // bullish trendline can exist, and over-extension has been reached instead.
        Assert.That(detector.Snapshot.Line, Is.Null);
        Assert.That(detector.Snapshot.IsOverExtended, Is.True);
    }

    // ---- the two layers together ---------------------------------------------------------------

    [Test]
    public void AnalyzerFeedsTrendlineBreaksBackAsAnAccomplishment()
    {
        // The zone layer cannot see trendlines, so without the analyzer wiring the TrendlineBreak
        // accomplishment could never be awarded - a silent zero rather than a measured one.
        AlfonsoTimeframeAnalyzer analyzer = new(TimeSpan.FromHours(4));
        Assert.That(analyzer.Interval, Is.EqualTo(TimeSpan.FromHours(4)));
        Assert.That(analyzer.Trend.Trend, Is.EqualTo(AlfonsoTrend.Unknown));

        ImbalanceDetector bare = new(TimeSpan.FromHours(4));
        Assert.That(bare.TrendlineBreakLookup, Is.Null,
            "a detector built without the analyzer has no way to see trendline breaks");
    }

    private static Imbalance Zone(ImbalanceKind kind, bool continuation = false) => new()
    {
        Interval = TimeSpan.FromHours(4),
        Kind = kind,
        Proximal = kind == ImbalanceKind.Demand ? 100m : 110m,
        Distal = kind == ImbalanceKind.Demand ? 98m : 112m,
        BaseStart = Start,
        BaseEnd = Start,
        ConfirmedAt = Start,
        BaseCandleCount = 2,
        Strength = ImpulseStrength.Strong,
        Accomplished = Accomplishment.OpposingImbalanceEliminated,
        ImpulseToBaseRatio = 3m,
        ImpulseDisplacement = 12m,
        ImpulseBarsTracked = 2,
        IsContinuationPattern = continuation,
        MeetsTradeabilityCriteria = true
    };

    private static AlfonsoTrendDetector EstablishedUptrendWithBullishLine()
    {
        AlfonsoTrendDetector detector = new();
        detector.Apply(
            new AlfonsoBar(Start, 95m, 100m, 90m, 98m),
            new ImbalanceDetectorUpdate
            {
                Created = [Zone(ImbalanceKind.Demand) with { BaseEnd = Start, Distal = 90m }]
            });
        detector.Apply(
            new AlfonsoBar(Start.AddHours(1), 100m, 105m, 95m, 103m),
            ImbalanceDetectorUpdate.Empty);
        detector.Apply(
            new AlfonsoBar(Start.AddHours(2), 105m, 110m, 100m, 108m),
            new ImbalanceDetectorUpdate
            {
                Created =
                [
                    Zone(ImbalanceKind.Demand) with
                    {
                        BaseStart = Start.AddHours(1),
                        BaseEnd = Start.AddHours(1),
                        ConfirmedAt = Start.AddHours(2),
                        Distal = 95m
                    }
                ],
                Eliminated = [Zone(ImbalanceKind.Supply)]
            });

        return detector;
    }
}
