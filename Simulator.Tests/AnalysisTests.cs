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
            Assert.That(
                zones.Any(zone =>
                    zone.Type == PriceZoneType.Support &&
                    zone.CentrePrice == 100m),
                Is.True);
            Assert.That(
                zones.Any(zone =>
                    zone.Type == PriceZoneType.Resistance &&
                    zone.CentrePrice == 110m),
                Is.True);
        });
    }

    [Test]
    public void Ransac_UnchangedInput_IsDeterministicAcrossEngineVersions()
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
            MaximumPivots: 100));

        Trendline first = detector.Detect(swings, atr: 1m, version: 1).Single();
        Trendline second = detector.Detect(swings, atr: 1m, version: 999).Single();

        Assert.Multiple(() =>
        {
            Assert.That(first.InlierCount, Is.EqualTo(8));
            Assert.That(first.SlopePerSecond, Is.EqualTo(second.SlopePerSecond));
            Assert.That(first.OriginPrice, Is.EqualTo(second.OriginPrice));
            Assert.That(first.StartTime, Is.EqualTo(second.StartTime));
            Assert.That(first.EndTime, Is.EqualTo(second.EndTime));
            Assert.That(first.Type, Is.EqualTo(TrendlineType.Support));
        });
    }

    [Test]
    public void Ransac_ReturnsMultipleBoundedLinesForDistinctPivotGroups_RecentFirst()
    {
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        SwingPoint[] firstSegment = Enumerable.Range(0, 4)
            .Select(index => Swing(
                start.AddMinutes(index * 5),
                100m + index,
                SwingType.Low))
            .ToArray();
        SwingPoint[] secondSegment = Enumerable.Range(0, 4)
            .Select(index => Swing(
                start.AddMinutes(60 + index * 5),
                120m - index * 2m,
                SwingType.Low))
            .ToArray();

        var detector = new RansacTrendlineDetector(new RansacOptions(
            MaximumIterations: 300,
            DistanceThresholdAtr: 0.01m,
            MinimumInliers: 3,
            MaximumPivots: 100,
            MaximumLinesPerType: 4));

        IReadOnlyList<Trendline> lines = detector.Detect(
            [.. firstSegment, .. secondSegment],
            atr: 1m,
            version: 1);

        Assert.Multiple(() =>
        {
            Assert.That(lines, Has.Count.EqualTo(2));
            Assert.That(lines.All(line => line.InlierCount == 4), Is.True);
            Assert.That(lines.All(line => line.StartTime < line.EndTime), Is.True);

            // TrendlineDetector now returns active/recent structures first.
            Assert.That(lines[0].StartTime, Is.GreaterThan(lines[1].EndTime));
        });
    }

    [Test]
    public void ChannelDetector_UsesConfirmedSwingsAndReturnsMostRecentValidChannel()
    {
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        Trendline firstSupport = Line(
            start,
            start.AddMinutes(20),
            100m,
            TrendlineType.Support);
        Trendline firstResistance = Line(
            start,
            start.AddMinutes(20),
            110m,
            TrendlineType.Resistance);
        Trendline secondSupport = Line(
            start.AddMinutes(30),
            start.AddMinutes(50),
            130m,
            TrendlineType.Support);
        Trendline secondResistance = Line(
            start.AddMinutes(30),
            start.AddMinutes(50),
            140m,
            TrendlineType.Resistance);

        SwingPoint[] swings =
        [
            Swing(start, 100m, SwingType.Low),
            Swing(start.AddMinutes(5), 110m, SwingType.High),
            Swing(start.AddMinutes(10), 100m, SwingType.Low),
            Swing(start.AddMinutes(15), 110m, SwingType.High),

            Swing(start.AddMinutes(30), 130m, SwingType.Low),
            Swing(start.AddMinutes(35), 140m, SwingType.High),
            Swing(start.AddMinutes(40), 130m, SwingType.Low),
            Swing(start.AddMinutes(45), 140m, SwingType.High)
        ];

        DateTimeOffset detectedAt = start.AddMinutes(60);
        var detector = new ChannelDetector(new ChannelOptions(MaximumChannels: 6));

        IReadOnlyList<PriceChannel> channels = detector.Detect(
            [firstSupport, firstResistance, secondSupport, secondResistance],
            swings,
            detectedAt,
            atr: 1m,
            currentPrice: 135m);

        PriceChannel channel = channels.Single();
        Assert.Multiple(() =>
        {
            Assert.That(channel.LowerLine, Is.SameAs(secondSupport));
            Assert.That(channel.UpperLine, Is.SameAs(secondResistance));
            Assert.That(channel.StartTime, Is.EqualTo(start.AddMinutes(30)));
            Assert.That(channel.EndTime, Is.EqualTo(detectedAt));
            Assert.That(channel.Width, Is.EqualTo(10m));
            Assert.That(channel.WidthAtr, Is.EqualTo(10m));
            Assert.That(channel.Direction, Is.EqualTo(ChannelDirection.Sideways));
        });
    }

    [Test]
    public void ChannelDetector_RejectsChannelWhenLatestCloseBreaksEnvelope()
    {
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Trendline support = Line(
            start,
            start.AddMinutes(20),
            100m,
            TrendlineType.Support);
        Trendline resistance = Line(
            start,
            start.AddMinutes(20),
            110m,
            TrendlineType.Resistance);
        SwingPoint[] swings =
        [
            Swing(start, 100m, SwingType.Low),
            Swing(start.AddMinutes(5), 110m, SwingType.High),
            Swing(start.AddMinutes(10), 100m, SwingType.Low),
            Swing(start.AddMinutes(15), 110m, SwingType.High)
        ];

        var detector = new ChannelDetector();
        DateTimeOffset detectedAt = start.AddMinutes(30);

        IReadOnlyList<PriceChannel> inside = detector.Detect(
            [support, resistance],
            swings,
            detectedAt,
            atr: 1m,
            currentPrice: 105m);
        IReadOnlyList<PriceChannel> broken = detector.Detect(
            [support, resistance],
            swings,
            detectedAt,
            atr: 1m,
            currentPrice: 111m);

        Assert.Multiple(() =>
        {
            Assert.That(inside, Has.Count.EqualTo(1));
            Assert.That(broken, Is.Empty);
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
                index));
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
                Sequence: 0));
        })));

        Assert.That(
            instruments.All(instrument =>
                engine.GetLatest(instrument, interval) is not null),
            Is.True);
    }

    private static SwingPoint Swing(
        DateTimeOffset time,
        decimal price,
        SwingType type) => new()
        {
            PivotTime = time,
            ConfirmedAt = time.AddMinutes(10),
            Price = price,
            Type = type,
            Strength = 2
        };

    private static Trendline Line(
        DateTimeOffset start,
        DateTimeOffset end,
        decimal price,
        TrendlineType type,
        decimal slopePerSecond = 0m) => new()
        {
            StartTime = start,
            EndTime = end,
            OriginTime = start,
            OriginPrice = price,
            SlopePerSecond = slopePerSecond,
            InlierCount = 4,
            MeanAbsoluteError = 0.01m,
            FitScore = 90m,
            Type = type
        };
}