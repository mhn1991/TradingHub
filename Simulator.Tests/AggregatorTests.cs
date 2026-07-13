using Brokers.Models;
using ChartAnnotator.MarketData;
using ChartAnnotator.Models;

namespace Simulator.Tests;

[TestFixture]
public sealed class AggregatorTests
{
    [Test]
    public void FiveMinuteInput_BuildsAlignedFiveFifteenAndOneHourCandles()
    {
        InstrumentKey instrument = new("CRYPTO:BTC/USDT");
        BarInterval five = BarInterval.Minutes(5);
        BarInterval fifteen = BarInterval.Minutes(15);
        BarInterval hour = BarInterval.Hours(1);
        var aggregator = new MultiTimeframeAggregator(
            instrument,
            [five, fifteen, hour],
            candleCapacity: 2_000);

        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var events = new List<CandleClosedEvent>();
        for (int index = 0; index < 12; index++)
        {
            decimal price = 100m + index;
            events.AddRange(aggregator.Apply(TestCandles.Create(
                instrument,
                start.AddMinutes(index * 5),
                five,
                price,
                price + 2m,
                price - 1m,
                price + 1m)));
        }

        Candle hourCandle = events.Single(item => item.Interval == hour).Candle;
        Assert.Multiple(() =>
        {
            Assert.That(events.Count(item => item.Interval == five), Is.EqualTo(12));
            Assert.That(events.Count(item => item.Interval == fifteen), Is.EqualTo(4));
            Assert.That(events.Count(item => item.Interval == hour), Is.EqualTo(1));
            Assert.That(hourCandle.OpenTime, Is.EqualTo(start));
            Assert.That(hourCandle.CloseTime, Is.EqualTo(start.AddHours(1)));
            Assert.That(hourCandle.Prices.Open, Is.EqualTo(100m));
            Assert.That(hourCandle.Prices.Close, Is.EqualTo(112m));
            Assert.That(hourCandle.Prices.High, Is.EqualTo(113m));
            Assert.That(hourCandle.Prices.Low, Is.EqualTo(99m));
        });
    }

    [Test]
    public void GapInBaseCandles_IsRejectedInsteadOfCompletingPartialBucket()
    {
        InstrumentKey instrument = new("FX:GBP/USD");
        BarInterval five = BarInterval.Minutes(5);
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var aggregator = new MultiTimeframeAggregator(
            instrument,
            [BarInterval.Minutes(15)]);

        aggregator.Apply(TestCandles.Create(
            instrument, start, five, 100m, 101m, 99m, 100m));

        Assert.That(
            () => aggregator.Apply(TestCandles.Create(
                instrument,
                start.AddMinutes(15),
                five,
                100m,
                101m,
                99m,
                100m)),
            Throws.InvalidOperationException.With.Message.Contains("discontinuous"));
    }

    [Test]
    public void IncompleteBaseCandle_IsRejected()
    {
        InstrumentKey instrument = new("FX:GBP/USD");
        BarInterval five = BarInterval.Minutes(5);
        Candle candle = TestCandles.Create(
            instrument,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            five,
            100m,
            101m,
            99m,
            100m) with
        {
            IsComplete = false
        };
        var aggregator = new MultiTimeframeAggregator(instrument, [five]);

        Assert.That(
            () => aggregator.Apply(candle),
            Throws.ArgumentException.With.Message.Contains("complete"));
    }

    [Test]
    public void Constructor_RejectsInvalidConfiguration()
    {
        InstrumentKey instrument = new("FX:GBP/USD");
        Assert.Multiple(() =>
        {
            Assert.That(
                () => new MultiTimeframeAggregator(default, [BarInterval.Minutes(5)]),
                Throws.ArgumentException);
            Assert.That(
                () => new MultiTimeframeAggregator(instrument, []),
                Throws.ArgumentException);
            Assert.That(
                () => new MultiTimeframeAggregator(instrument, [default(BarInterval)]),
                Throws.ArgumentException);
            Assert.That(
                () => new MultiTimeframeAggregator(instrument, [BarInterval.Minutes(5)], 0),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => new MultiTimeframeAggregator(instrument, null!),
                Throws.TypeOf<ArgumentNullException>());
        });
    }

    [Test]
    public void Apply_RejectsWrongInstrumentAndChangingBaseInterval()
    {
        InstrumentKey instrument = new("FX:GBP/USD");
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var aggregator = new MultiTimeframeAggregator(instrument, [BarInterval.Minutes(15)]);

        Assert.That(
            () => aggregator.Apply(TestCandles.Create(
                "FX:EUR/USD",
                start,
                BarInterval.Minutes(5),
                100m,
                101m,
                99m,
                100m)),
            Throws.ArgumentException);

        aggregator.Apply(TestCandles.Create(
            instrument,
            start,
            BarInterval.Minutes(5),
            100m,
            101m,
            99m,
            100m));
        Assert.That(
            () => aggregator.Apply(TestCandles.Create(
                instrument,
                start.AddMinutes(5),
                BarInterval.Minutes(1),
                100m,
                101m,
                99m,
                100m)),
            Throws.ArgumentException.With.Message.Contains("base interval changed"));
    }

    [Test]
    public void BaseCandleCloseTime_MustMatchIntervalBoundary()
    {
        InstrumentKey instrument = new("FX:GBP/USD");
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Candle valid = TestCandles.Create(
            instrument,
            start,
            BarInterval.Minutes(5),
            100m,
            101m,
            99m,
            100m);

        Assert.Multiple(() =>
        {
            Assert.That(
                () => new MultiTimeframeAggregator(instrument, [BarInterval.Minutes(5)])
                    .Apply(valid with { CloseTime = null }),
                Throws.ArgumentException.With.Message.Contains("close time"));
            Assert.That(
                () => new MultiTimeframeAggregator(instrument, [BarInterval.Minutes(5)])
                    .Apply(valid with { CloseTime = start.AddMinutes(4) }),
                Throws.ArgumentException.With.Message.Contains("boundary"));
            Assert.That(
                () => new MultiTimeframeAggregator(instrument, [BarInterval.Minutes(5)])
                    .Apply(valid with { CloseTime = start.AddMinutes(6) }),
                Throws.ArgumentException.With.Message.Contains("boundary"));
        });
    }

    [Test]
    public void InclusiveBrokerCloseTimeWithinTolerance_CompletesAtLogicalBoundary()
    {
        InstrumentKey instrument = new("CRYPTO:BTC/USD");
        BarInterval interval = BarInterval.Minutes(5);
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Candle candle = TestCandles.Create(
            instrument,
            start,
            interval,
            100m,
            101m,
            99m,
            100m) with
        {
            CloseTime = start.AddMinutes(5).AddMilliseconds(-1)
        };
        var aggregator = new MultiTimeframeAggregator(instrument, [interval]);

        Candle result = aggregator.Apply(candle).Single().Candle;

        Assert.That(result.CloseTime, Is.EqualTo(start.AddMinutes(5)));
    }

    [Test]
    public void BaseIntervalCrossingSmallerTargetBoundary_IsRejected()
    {
        InstrumentKey instrument = new("FX:GBP/USD");
        BarInterval five = BarInterval.Minutes(5);
        var aggregator = new MultiTimeframeAggregator(instrument, [BarInterval.Minutes(3)]);

        Assert.That(
            () => aggregator.Apply(TestCandles.Create(
                instrument,
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                five,
                100m,
                101m,
                99m,
                100m)),
            Throws.InvalidOperationException.With.Message.Contains("crosses"));
    }

    [Test]
    public void Flush_IncompleteCandleIsOptionalAndNotStoredAsCompleted()
    {
        InstrumentKey instrument = new("FX:GBP/USD");
        BarInterval five = BarInterval.Minutes(5);
        BarInterval fifteen = BarInterval.Minutes(15);
        var aggregator = new MultiTimeframeAggregator(instrument, [fifteen]);
        aggregator.Apply(TestCandles.Create(
            instrument,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            five,
            100m,
            101m,
            99m,
            100m));

        Assert.That(aggregator.Flush(), Is.Empty);
        Candle flushed = aggregator.Flush(includeIncomplete: true).Single().Candle;

        Assert.Multiple(() =>
        {
            Assert.That(flushed.IsComplete, Is.False);
            Assert.That(aggregator.GetCandles(fifteen), Is.Empty);
            Assert.That(aggregator.Flush(includeIncomplete: true), Is.Empty);
        });
    }

    [Test]
    public void CompletedCandleBuffer_RespectsConfiguredCapacity()
    {
        InstrumentKey instrument = new("FX:GBP/USD");
        BarInterval five = BarInterval.Minutes(5);
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var aggregator = new MultiTimeframeAggregator(instrument, [five], candleCapacity: 2);
        for (int index = 0; index < 3; index++)
        {
            aggregator.Apply(TestCandles.Create(
                instrument,
                start.AddMinutes(index * 5),
                five,
                100m + index,
                101m + index,
                99m + index,
                100m + index));
        }

        IReadOnlyList<Candle> retained = aggregator.GetCandles(five);

        Assert.That(
            retained.Select(candle => candle.OpenTime),
            Is.EqualTo(new[] { start.AddMinutes(5), start.AddMinutes(10) }));
    }

    [Test]
    public void AggregatedVolume_IsSummedAndMixedKindsBecomeUnknown()
    {
        InstrumentKey instrument = new("FX:GBP/USD");
        BarInterval five = BarInterval.Minutes(5);
        BarInterval fifteen = BarInterval.Minutes(15);
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var aggregator = new MultiTimeframeAggregator(instrument, [fifteen]);
        VolumeKind[] kinds =
            [VolumeKind.TickCount, VolumeKind.TickCount, VolumeKind.BaseAssetQuantity];
        var events = new List<CandleClosedEvent>();
        for (int index = 0; index < 3; index++)
        {
            Candle candle = TestCandles.Create(
                instrument,
                start.AddMinutes(index * 5),
                five,
                100m,
                101m,
                99m,
                100m,
                volume: index + 1) with
            {
                Volume = new MarketVolume(index + 1, kinds[index])
            };
            events.AddRange(aggregator.Apply(candle));
        }

        MarketVolume volume = events.Single().Candle.Volume!;
        Assert.Multiple(() =>
        {
            Assert.That(volume.Value, Is.EqualTo(6m));
            Assert.That(volume.Kind, Is.EqualTo(VolumeKind.Unknown));
        });
    }
}
