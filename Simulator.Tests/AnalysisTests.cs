using Brokers.Models;
using ChartAnnotator.Engine;
using ChartAnnotator.Models;
using ChartAnnotator.Structure;

namespace Simulator.Tests;

[TestFixture]
public sealed class AnalysisTests
{
    [Test]
    public void SwingHigh_IsOnlyAvailableAfterRightHandCandlesClose()
    {
        InstrumentKey instrument = new("FX:GBP/JPY");
        BarInterval interval = BarInterval.Minutes(5);
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var detector = new SwingDetector(left: 2, right: 2);
        decimal[] highs = [1m, 2m, 5m, 2m, 1m];
        IReadOnlyList<SwingPoint> result = [];

        for (int index = 0; index < highs.Length; index++)
        {
            result = detector.Update(TestCandles.Create(
                instrument,
                start.AddMinutes(index * 5),
                interval,
                0.5m,
                highs[index],
                0.1m,
                0.5m));

            if (index < 4)
            {
                Assert.That(result, Is.Empty);
            }
        }

        SwingPoint swing = result.Single(item => item.Type == SwingType.High);
        Assert.Multiple(() =>
        {
            Assert.That(swing.PivotTime, Is.EqualTo(start.AddMinutes(10)));
            Assert.That(swing.ConfirmedAt, Is.EqualTo(start.AddMinutes(25)));
            Assert.That(swing.Price, Is.EqualTo(5m));
        });
    }

    [Test]
    public void Dbscan_ClustersNearbySwingPricesIntoZones()
    {
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        SwingPoint[] swings =
        [
            Swing(start, 100m, SwingType.Low),
            Swing(start.AddMinutes(5), 100.1m, SwingType.Low),
            Swing(start.AddMinutes(10), 99.9m, SwingType.Low),
            Swing(start.AddMinutes(15), 110m, SwingType.High),
            Swing(start.AddMinutes(20), 110.1m, SwingType.High),
            Swing(start.AddMinutes(25), 109.9m, SwingType.High)
        ];

        var detector = new SupportResistanceDetector(new DbscanOptions(
            EpsilonAtr: 0.25m,
            MinimumPoints: 2,
            MaximumPivots: 100));

        IReadOnlyList<PriceZone> zones = detector.Detect(swings, atr: 1m);

        Assert.Multiple(() =>
        {
            Assert.That(zones, Has.Count.EqualTo(2));
            Assert.That(zones.Any(zone => zone.Type == PriceZoneType.Support && zone.CentrePrice == 100m), Is.True);
            Assert.That(zones.Any(zone => zone.Type == PriceZoneType.Resistance && zone.CentrePrice == 110m), Is.True);
        });
    }

    [Test]
    public void Ransac_WithFixedSeed_ReturnsDeterministicSupportLine()
    {
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        SwingPoint[] swings = Enumerable.Range(0, 8)
            .Select(index => Swing(
                start.AddMinutes(index * 5),
                100m + index,
                SwingType.Low))
            .ToArray();

        var detector = new RansacTrendlineDetector(new RansacOptions(
            MaximumIterations: 200,
            DistanceThresholdAtr: 0.1m,
            MinimumInliers: 3,
            MaximumPivots: 100,
            RandomSeed: 12345));

        Trendline first = detector.Detect(swings, atr: 1m, version: 1).Single();
        Trendline second = detector.Detect(swings, atr: 1m, version: 1).Single();

        Assert.Multiple(() =>
        {
            Assert.That(first.InlierCount, Is.EqualTo(8));
            Assert.That(first.SlopePerSecond, Is.EqualTo(second.SlopePerSecond));
            Assert.That(first.OriginPrice, Is.EqualTo(second.OriginPrice));
            Assert.That(first.Type, Is.EqualTo(TrendlineType.Support));
        });
    }

    [Test]
    public async Task AnnotationEngine_KeepsOnlyConfiguredCandleCapacity()
    {
        InstrumentKey instrument = new("CRYPTO:BTC/USDT");
        BarInterval interval = BarInterval.Minutes(5);
        var engine = new ChartAnnotationEngine(new ChartAnnotationOptions
        {
            CandleCapacity = 3,
            SwingCapacity = 10,
            IndicatorCapacity = 3,
            AtrPeriod = 2,
            RsiPeriod = 2,
            BollingerPeriod = 2,
            HeavyAnalysisEveryCandles = 1
        });
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        for (int index = 0; index < 5; index++)
        {
            Candle candle = TestCandles.Create(
                instrument,
                start.AddMinutes(index * 5),
                interval,
                100m + index,
                102m + index,
                99m + index,
                101m + index);
            await engine.ProcessAsync(new CandleClosedEvent(
                instrument,
                interval,
                candle,
                index + 1));
        }

        IReadOnlyList<Candle> candles = engine.GetCandles(instrument, interval);
        Assert.Multiple(() =>
        {
            Assert.That(candles, Has.Count.EqualTo(3));
            Assert.That(candles[0].OpenTime, Is.EqualTo(start.AddMinutes(10)));
            Assert.That(candles[^1].OpenTime, Is.EqualTo(start.AddMinutes(20)));
        });
    }

    [Test]
    public async Task AnnotationEngine_ProcessesDifferentChartKeysConcurrently()
    {
        var engine = new ChartAnnotationEngine();
        BarInterval interval = BarInterval.Minutes(5);
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        InstrumentKey[] instruments = Enumerable.Range(0, 32)
            .Select(index => new InstrumentKey($"TEST:ASSET{index}/USD"))
            .ToArray();

        await Task.WhenAll(instruments.Select((instrument, index) => Task.Run(async () =>
        {
            Candle candle = TestCandles.Create(
                instrument,
                start,
                interval,
                100m + index,
                101m + index,
                99m + index,
                100m + index);
            await engine.ProcessAsync(new CandleClosedEvent(
                instrument,
                interval,
                candle,
                Sequence: 1));
        })));

        Assert.That(
            instruments.All(instrument => engine.GetLatest(instrument, interval) is not null),
            Is.True);
    }

    private static SwingPoint Swing(DateTimeOffset time, decimal price, SwingType type) => new()
    {
        PivotTime = time,
        ConfirmedAt = time.AddMinutes(10),
        Price = price,
        Type = type,
        Strength = 2
    };
}
