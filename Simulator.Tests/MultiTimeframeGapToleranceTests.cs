using Brokers.Models;
using ChartAnnotator.MarketData;
using ChartAnnotator.Models;
using NUnit.Framework;

namespace Simulator.Tests;

/// <summary>
/// A market that closes every day cannot produce a daily candle under a gap policy that discards any
/// bucket touched by a gap.
/// <para>
/// Gold's stream contains a maintenance break in every 24-hour span, so a daily bucket always spans
/// one. With zero tolerance every one of them is discarded: a 3.5-year run configured with `1d`
/// emitted 1m, 5m, 15m, 1h and 4h closes and not a single daily close, and the agent that required
/// daily analysis observed on every bar and took zero trades without error. These tests pin both the
/// old behaviour and the proportional alternative.
/// </para>
/// </summary>
[TestFixture]
public sealed class MultiTimeframeGapToleranceTests
{
    private static readonly InstrumentKey Instrument = new("METAL:XAU/USD");
    private static readonly BarInterval Minute = BarInterval.Minutes(1);
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Ten days of one-minute candles with a one-hour break each day, as gold trades.</summary>
    private static Dictionary<BarInterval, int> Run(double tolerance, int breakMinutes = 60)
    {
        BarInterval[] targets = [BarInterval.Hours(1), BarInterval.Hours(4), BarInterval.Days(1)];
        MultiTimeframeAggregator aggregator = new(
            Instrument, targets, 5_000, BaseCandleGapPolicy.ResetIncompleteBuckets, tolerance);

        Dictionary<BarInterval, int> closes = targets.ToDictionary(interval => interval, _ => 0);

        for (int minute = 0; minute < 60 * 24 * 10; minute++)
        {
            // The daily break sits at 21:00-22:00 UTC, inside the last 4h bucket of each day.
            int minuteOfDay = minute % (60 * 24);
            if (minuteOfDay >= 21 * 60 && minuteOfDay < (21 * 60) + breakMinutes)
                continue;

            Candle candle = new()
            {
                Instrument = Instrument,
                Interval = Minute,
                OpenTime = Start.AddMinutes(minute),
                CloseTime = Start.AddMinutes(minute + 1),
                Prices = new Ohlc(100m, 101m, 99m, 100.5m),
                IsComplete = true
            };

            foreach (CandleClosedEvent closed in aggregator.Apply(candle))
                closes[closed.Interval]++;
        }

        return closes;
    }

    [Test]
    public void WithZeroToleranceADailyMarketBreakDestroysEveryDailyCandle()
    {
        Dictionary<BarInterval, int> closes = Run(tolerance: 0.0);

        Assert.That(closes[BarInterval.Days(1)], Is.Zero,
            "this is the defect: a daily bucket always spans the break and is always discarded");
        Assert.That(closes[BarInterval.Hours(1)], Is.GreaterThan(200),
            "hourly buckets mostly avoid the break and are unaffected");
    }

    [Test]
    public void ProportionalToleranceLetsTheDailyCandleSurviveABreakThatBarelyTouchesIt()
    {
        // A one-hour break is 4.2% of a day but 100% of an hour, so a 10% tolerance keeps the daily
        // bucket while still discarding any hourly bucket the break lands in.
        Dictionary<BarInterval, int> closes = Run(tolerance: 0.10);

        Assert.That(closes[BarInterval.Days(1)], Is.GreaterThan(8),
            "ten days of candles should yield daily closes");
        // Ten days of six 4h buckets is sixty. The break is 25% of a 4h bucket, past the 10%
        // tolerance, so the 20:00-24:00 bucket is discarded each day and exactly fifty survive -
        // the same gap keeping the daily bucket and discarding the 4h one is the whole point.
        Assert.That(closes[BarInterval.Hours(4)], Is.EqualTo(50));
    }

    [Test]
    public void ToleranceIsRelativeToTheBucketNotAbsolute()
    {
        // A six-hour break is 25% of a day, past a 10% tolerance, so the daily bucket goes too.
        Dictionary<BarInterval, int> wide = Run(tolerance: 0.10, breakMinutes: 6 * 60);

        Assert.That(wide[BarInterval.Days(1)], Is.Zero,
            "a gap large relative to the bucket must still discard it");
    }

    [Test]
    public void ZeroToleranceIsTheDefaultSoExistingBehaviourIsUnchanged()
    {
        BarInterval[] targets = [BarInterval.Hours(1)];
        MultiTimeframeAggregator defaulted = new(
            Instrument, targets, 5_000, BaseCandleGapPolicy.ResetIncompleteBuckets);

        Assert.That(defaulted, Is.Not.Null);
        Assert.Throws<ArgumentOutOfRangeException>(() => new MultiTimeframeAggregator(
            Instrument, targets, 5_000, BaseCandleGapPolicy.ResetIncompleteBuckets, 1.0));
    }
}
