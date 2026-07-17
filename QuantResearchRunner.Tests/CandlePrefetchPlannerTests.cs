using Brokers.Models;
using QuantResearchRunner.Experiments;

namespace QuantResearchRunner.Tests;

[TestFixture]
public sealed class CandlePrefetchPlannerTests
{
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public void ComputeMasterRange_ReturnsWidestSpanAcrossAllWindows()
    {
        (DateTimeOffset From, DateTimeOffset To)[] windows =
        [
            (Start.AddDays(5), Start.AddDays(10)),
            (Start, Start.AddDays(3)),
            (Start.AddDays(8), Start.AddDays(20))
        ];

        (DateTimeOffset From, DateTimeOffset To) result = CandlePrefetchPlanner.ComputeMasterRange(windows);

        Assert.Multiple(() =>
        {
            Assert.That(result.From, Is.EqualTo(Start));
            Assert.That(result.To, Is.EqualTo(Start.AddDays(20)));
        });
    }

    [Test]
    public void ComputeMasterRange_EmptyWindows_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            CandlePrefetchPlanner.ComputeMasterRange([]));
    }

    [Test]
    public void SliceForWindow_ReturnsOnlyCandlesWithinRange()
    {
        Candle[] master = BuildCandles(count: 100);

        IReadOnlyList<Candle> slice = CandlePrefetchPlanner.SliceForWindow(
            master, Start.AddMinutes(10), Start.AddMinutes(20));

        Assert.Multiple(() =>
        {
            Assert.That(slice, Has.Count.EqualTo(10));
            Assert.That(slice[0].OpenTime, Is.EqualTo(Start.AddMinutes(10)));
            Assert.That(slice[^1].OpenTime, Is.EqualTo(Start.AddMinutes(19)));
        });
    }

    [Test]
    public void SliceForWindow_ExclusiveUpperBound_ExcludesToCandle()
    {
        Candle[] master = BuildCandles(count: 5);

        IReadOnlyList<Candle> slice = CandlePrefetchPlanner.SliceForWindow(
            master, Start, Start.AddMinutes(3));

        Assert.That(slice.Select(candle => candle.OpenTime), Is.EqualTo(new[]
        {
            Start, Start.AddMinutes(1), Start.AddMinutes(2)
        }));
    }

    [Test]
    public void SliceForWindow_FromNotBeforeTo_Throws()
    {
        Candle[] master = BuildCandles(count: 5);

        Assert.Throws<ArgumentException>(() =>
            CandlePrefetchPlanner.SliceForWindow(master, Start.AddMinutes(3), Start));
    }

    [Test]
    public void SliceForWindow_WithStreamFrom_IncludesWarmupBars()
    {
        Candle[] master = BuildCandles(count: 30);
        DateTimeOffset from = Start.AddMinutes(10);
        DateTimeOffset to = Start.AddMinutes(20);
        DateTimeOffset streamFrom = Start.AddMinutes(5);

        IReadOnlyList<Candle> slice = CandlePrefetchPlanner.SliceForWindow(master, from, to, streamFrom);

        Assert.Multiple(() =>
        {
            Assert.That(slice, Has.Count.EqualTo(15));
            Assert.That(slice[0].OpenTime, Is.EqualTo(streamFrom));
            Assert.That(slice[^1].OpenTime, Is.EqualTo(Start.AddMinutes(19)));
        });
    }

    [Test]
    public void SliceForWindow_StreamFromAfterFrom_Throws()
    {
        Candle[] master = BuildCandles(count: 10);

        Assert.Throws<ArgumentException>(() =>
            CandlePrefetchPlanner.SliceForWindow(master, Start, Start.AddMinutes(5), Start.AddMinutes(1)));
    }

    private static Candle[] BuildCandles(int count)
    {
        var candles = new Candle[count];
        for (int i = 0; i < count; i++)
        {
            DateTimeOffset openTime = Start.AddMinutes(i);
            candles[i] = new Candle
            {
                Instrument = Instrument,
                Interval = BarInterval.Minutes(1),
                OpenTime = openTime,
                CloseTime = openTime.AddMinutes(1),
                Prices = new Ohlc(1.1m, 1.1005m, 1.0995m, 1.1002m),
                Volume = new MarketVolume(100m, VolumeKind.Unknown),
                IsComplete = true
            };
        }

        return candles;
    }
}
