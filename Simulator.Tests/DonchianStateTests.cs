using Brokers.Models;
using ChartAnnotator.Indicators;
using ChartAnnotator.Models;

namespace Simulator.Tests;

[TestFixture]
public sealed class DonchianStateTests
{
    private static readonly InstrumentKey Instrument = new("FX:GBP/USD");
    private static readonly BarInterval Interval = BarInterval.Minutes(5);
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Candle Candle(int index, decimal open, decimal high, decimal low, decimal close) =>
        TestCandles.Create(Instrument, Start + TimeSpan.FromMinutes(5 * index), Interval, open, high, low, close);

    [Test]
    public void CurrentCandle_CannotBreakItsOwnBoundary()
    {
        var state = new DonchianState(period: 3);

        // Warm up: three flat candles establish Upper=10, Lower=5.
        state.Update(Candle(0, 8m, 10m, 5m, 8m));
        state.Update(Candle(1, 8m, 10m, 5m, 8m));
        DonchianSnapshot warm = state.Update(Candle(2, 8m, 10m, 5m, 8m));
        Assert.Multiple(() =>
        {
            Assert.That(warm.Upper, Is.EqualTo(10m));
            Assert.That(warm.Lower, Is.EqualTo(5m));
        });

        // This candle both extends the range (own high 15) and closes above the
        // *previous* upper boundary (10). If the boundary were computed after
        // folding this bar in first, Upper would already be 15 and the close
        // (15) could never be judged greater than the boundary that contains it.
        DonchianSnapshot breakout = state.Update(Candle(3, 11m, 15m, 9m, 15m));

        Assert.Multiple(() =>
        {
            Assert.That(breakout.ClosedAbovePreviousUpper, Is.True);
            Assert.That(breakout.BarsSinceUpperBreak, Is.EqualTo(0));
            Assert.That(breakout.Upper, Is.EqualTo(15m), "Next call's boundary should now include this bar.");
        });
    }

    [Test]
    public void CurrentCandle_OwnExtremeAloneDoesNotCountAsABreak()
    {
        var state = new DonchianState(period: 3);
        state.Update(Candle(0, 8m, 10m, 5m, 8m));
        state.Update(Candle(1, 8m, 10m, 5m, 8m));
        state.Update(Candle(2, 8m, 10m, 5m, 8m));

        // Own high (20) sets a new extreme, but the close (10) does not exceed
        // the previous upper boundary (10) -> not a genuine close-through break.
        DonchianSnapshot result = state.Update(Candle(3, 9m, 20m, 9m, 10m));

        Assert.Multiple(() =>
        {
            Assert.That(result.ClosedAbovePreviousUpper, Is.False);
            Assert.That(result.BarsSinceUpperBreak, Is.EqualTo(-1));
            Assert.That(result.Upper, Is.EqualTo(20m), "The window should still absorb the new extreme for future bars.");
        });
    }

    [Test]
    public void WindowEviction_DropsOldestExtremeCorrectly()
    {
        var state = new DonchianState(period: 3);
        state.Update(Candle(0, 20m, 20m, 18m, 19m)); // sets the initial extreme high
        state.Update(Candle(1, 12m, 13m, 11m, 12m));
        DonchianSnapshot ready = state.Update(Candle(2, 12m, 13m, 11m, 12m));
        Assert.That(ready.Upper, Is.EqualTo(20m));

        // A 4th bar evicts bar 0 (the period-3 window keeps bars 1-3).
        DonchianSnapshot afterEviction = state.Update(Candle(3, 12m, 13m, 11m, 12m));
        Assert.That(afterEviction.Upper, Is.EqualTo(13m));
    }

    [Test]
    public void BreakoutDirection_UpAndDownFireExclusively()
    {
        var state = new DonchianState(period: 3);
        state.Update(Candle(0, 10m, 12m, 8m, 10m));
        state.Update(Candle(1, 10m, 12m, 8m, 10m));
        state.Update(Candle(2, 10m, 12m, 8m, 10m));

        DonchianSnapshot up = state.Update(Candle(3, 12m, 14m, 12m, 14m));
        Assert.Multiple(() =>
        {
            Assert.That(up.ClosedAbovePreviousUpper, Is.True);
            Assert.That(up.ClosedBelowPreviousLower, Is.False);
        });
    }

    [Test]
    public void BarsSinceBreak_CountsAndResetsCorrectly()
    {
        var state = new DonchianState(period: 3);
        state.Update(Candle(0, 10m, 12m, 8m, 10m));
        state.Update(Candle(1, 10m, 12m, 8m, 10m));
        state.Update(Candle(2, 10m, 12m, 8m, 10m));

        DonchianSnapshot breakout = state.Update(Candle(3, 12m, 14m, 12m, 14m));
        Assert.That(breakout.BarsSinceUpperBreak, Is.EqualTo(0));

        DonchianSnapshot next1 = state.Update(Candle(4, 10m, 10m, 9m, 9.5m));
        Assert.That(next1.BarsSinceUpperBreak, Is.EqualTo(1));

        DonchianSnapshot next2 = state.Update(Candle(5, 9m, 10m, 8m, 9m));
        Assert.That(next2.BarsSinceUpperBreak, Is.EqualTo(2));

        DonchianSnapshot freshBreak = state.Update(Candle(6, 15m, 16m, 15m, 16m));
        Assert.That(freshBreak.BarsSinceUpperBreak, Is.EqualTo(0));
    }

    [Test]
    public void HistoricalSequence_IsDeterministic()
    {
        Candle[] candles =
        [
            Candle(0, 10m, 12m, 8m, 10m),
            Candle(1, 10m, 13m, 9m, 11m),
            Candle(2, 11m, 12m, 7m, 9m),
            Candle(3, 9m, 15m, 9m, 14m),
            Candle(4, 14m, 14m, 10m, 11m),
            Candle(5, 11m, 12m, 5m, 6m)
        ];

        var first = new DonchianState(period: 3);
        var second = new DonchianState(period: 3);

        foreach (Candle candle in candles)
        {
            DonchianSnapshot a = first.Update(candle);
            DonchianSnapshot b = second.Update(candle);
            Assert.That(a, Is.EqualTo(b));
        }
    }

    [Test]
    public void BeforeWarmup_ReturnsEmptySnapshot()
    {
        var state = new DonchianState(period: 3);
        DonchianSnapshot snapshot = state.Update(Candle(0, 10m, 12m, 8m, 10m));

        Assert.Multiple(() =>
        {
            Assert.That(state.IsReady, Is.False);
            Assert.That(snapshot, Is.EqualTo(DonchianSnapshot.Empty));
        });
    }

    [Test]
    public void Constructor_RejectsInvalidPeriod()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DonchianState(period: 1));
    }
}
