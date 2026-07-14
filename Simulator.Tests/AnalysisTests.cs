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
            MaximumPivots: 100,
            MinimumStrength: 20m));

        IReadOnlyList<PriceZone> zones = detector.Detect(swings, atr: 1m, currentPrice: 105m);

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
    public void SupportResistance_SplitsTemporallyDistantTouchesAtSamePrice()
    {
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var swings = new List<SwingPoint>
        {
            Swing(start, 100m, SwingType.Low),
            Swing(start.AddMinutes(5), 100.05m, SwingType.Low),
            Swing(start.AddMinutes(10), 99.95m, SwingType.Low)
        };

        // Large chronological gap of unrelated pivots between two visits to ~100.
        for (int index = 0; index < 40; index++)
        {
            swings.Add(Swing(
                start.AddMinutes(20 + index * 5),
                120m + index * 0.1m,
                index % 2 == 0 ? SwingType.High : SwingType.Low));
        }

        swings.Add(Swing(start.AddMinutes(300), 100.02m, SwingType.Low));
        swings.Add(Swing(start.AddMinutes(305), 99.98m, SwingType.Low));
        swings.Add(Swing(start.AddMinutes(310), 100.01m, SwingType.Low));

        var detector = new SupportResistanceDetector(new DbscanOptions(
            EpsilonAtr: 0.25m,
            MinimumPoints: 2,
            MaximumPivots: 200,
            MaximumTemporalGapSwings: 20,
            MinimumStrength: 20m,
            MaximumActiveDistanceAtr: 50m));

        IReadOnlyList<PriceZone> zones = detector.Detect(swings, atr: 1m, currentPrice: 100m);
        PriceZone[] nearHundred = zones
            .Where(zone => Math.Abs(zone.CentrePrice - 100m) <= 0.2m)
            .ToArray();

        // Old and new visits must not collapse into a single active zone.
        Assert.That(nearHundred.Length, Is.GreaterThanOrEqualTo(2));
    }

    [Test]
    public void SupportResistance_FlipsBrokenResistanceIntoNearbySupport()
    {
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        SwingPoint[] swings =
        [
            Swing(start, 110m, SwingType.High),
            Swing(start.AddMinutes(5), 110.1m, SwingType.High),
            Swing(start.AddMinutes(10), 109.9m, SwingType.High)
        ];

        var detector = new SupportResistanceDetector(new DbscanOptions(
            EpsilonAtr: 0.25m,
            MinimumPoints: 2,
            MinimumStrength: 20m,
            BreakToleranceAtr: 0.10m));

        // Close has broken above the old resistance but is still nearby.
        IReadOnlyList<PriceZone> zones = detector.Detect(swings, atr: 1m, currentPrice: 110.5m);

        Assert.That(zones, Is.Not.Empty);
        Assert.That(zones[0].Type, Is.EqualTo(PriceZoneType.Support));
    }

    [Test]
    public void SupportResistance_DropsZonesFarFromCurrentPrice()
    {
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        SwingPoint[] swings =
        [
            Swing(start, 100m, SwingType.Low),
            Swing(start.AddMinutes(5), 100.1m, SwingType.Low),
            Swing(start.AddMinutes(10), 99.9m, SwingType.Low)
        ];

        var detector = new SupportResistanceDetector(new DbscanOptions(
            EpsilonAtr: 0.25m,
            MinimumPoints: 2,
            MinimumStrength: 10m,
            MaximumActiveDistanceAtr: 2m));

        IReadOnlyList<PriceZone> zones = detector.Detect(swings, atr: 1m, currentPrice: 120m);
        Assert.That(zones, Is.Empty);
    }

    [Test]
    public void SupportResistance_UsesRecentTouchesForMixedRole()
    {
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        // Older highs at the level, then recent lows retesting it as demand.
        SwingPoint[] swings =
        [
            Swing(start, 100.05m, SwingType.High),
            Swing(start.AddMinutes(5), 99.95m, SwingType.High),
            Swing(start.AddMinutes(30), 100.02m, SwingType.Low),
            Swing(start.AddMinutes(35), 99.98m, SwingType.Low),
            Swing(start.AddMinutes(40), 100.00m, SwingType.Low)
        ];

        var detector = new SupportResistanceDetector(new DbscanOptions(
            EpsilonAtr: 0.25m,
            MinimumPoints: 2,
            RecentRoleTouchCount: 3,
            MinimumStrength: 20m));

        IReadOnlyList<PriceZone> zones = detector.Detect(swings, atr: 1m, currentPrice: 100.5m);
        Assert.That(zones, Is.Not.Empty);
        Assert.That(zones[0].Type, Is.EqualTo(PriceZoneType.Support));
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
    public void Ransac_OriginIsAnchoredAtMostRecentInlier_NotOldest()
    {
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        SwingPoint[] swings = Enumerable.Range(0, 6)
            .Select(index => Swing(
                start.AddMinutes(index * 5),
                100m + index,
                SwingType.Low))
            .ToArray();
        DateTimeOffset asOf = start.AddMinutes(30);

        var detector = new RansacTrendlineDetector(new RansacOptions(
            MaximumIterations: 200,
            DistanceThresholdAtr: 0.1m,
            MinimumInliers: 3));

        Trendline line = detector.Detect(
            swings,
            atr: 1m,
            version: 1,
            MarketStructureDirection.Unknown,
            asOf).Single();

        DateTimeOffset newestInlier = swings[^1].PivotTime;
        Assert.Multiple(() =>
        {
            // Origin must describe the recent end of the line.
            Assert.That(line.OriginTime, Is.EqualTo(newestInlier));
            Assert.That(line.StartTime, Is.EqualTo(swings[0].PivotTime));
            Assert.That(line.EndTime, Is.EqualTo(asOf));
            Assert.That(line.OriginPrice, Is.EqualTo(swings[^1].Price).Within(0.05m));
            // Price at the oldest touch still lands on the historical start.
            Assert.That(line.PriceAt(swings[0].PivotTime), Is.EqualTo(swings[0].Price).Within(0.05m));
            Assert.That(line.PriceAt(newestInlier), Is.EqualTo(swings[^1].Price).Within(0.05m));
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
            MaximumLinesPerType: 4,
            MaximumEndPivotAge: 8,
            RequiredRecentInliersWindow: 3));

        IReadOnlyList<Trendline> lines = detector.Detect(
            [.. firstSegment, .. secondSegment],
            atr: 1m,
            version: 1);

        Assert.Multiple(() =>
        {
            Assert.That(lines, Has.Count.EqualTo(2));
            Assert.That(lines.All(line => line.InlierCount == 4), Is.True);
            Assert.That(lines.All(line => line.StartTime < line.EndTime), Is.True);
            Assert.That(lines.All(line => line.OriginTime >= line.StartTime), Is.True);

            // Active/recent structures first; origin is the recent end of each line.
            Assert.That(lines[0].StartTime, Is.GreaterThan(lines[1].EndTime));
            Assert.That(lines[0].OriginTime, Is.EqualTo(lines[0].EndTime).Or.GreaterThanOrEqualTo(secondSegment[^1].PivotTime));
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
    public void ChannelDetector_BuildsParallelChannelFromSupportWhenResistanceSlopeDiffers()
    {
        // Independently fitted resistance has a different slope. The old detector
        // rejected this pair as non-parallel; the fixed detector projects a true
        // parallel upper boundary from the support line through swing highs.
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        decimal slope = 1m / 300m; // +1 price per 5 minutes
        Trendline support = new()
        {
            StartTime = start,
            EndTime = start.AddMinutes(20),
            OriginTime = start,
            OriginPrice = 100m,
            SlopePerSecond = slope,
            InlierCount = 4,
            MeanAbsoluteError = 0.01m,
            FitScore = 90m,
            Type = TrendlineType.Support
        };
        // Deliberately wrong / non-parallel resistance — should be ignored.
        Trendline mismatchedResistance = new()
        {
            StartTime = start,
            EndTime = start.AddMinutes(20),
            OriginTime = start,
            OriginPrice = 110m,
            SlopePerSecond = slope * 0.25m,
            InlierCount = 3,
            MeanAbsoluteError = 0.05m,
            FitScore = 70m,
            Type = TrendlineType.Resistance
        };

        SwingPoint[] swings =
        [
            Swing(start, 100m, SwingType.Low),
            Swing(start.AddMinutes(5), 111m, SwingType.High),
            Swing(start.AddMinutes(10), 102m, SwingType.Low),
            Swing(start.AddMinutes(15), 113m, SwingType.High),
            Swing(start.AddMinutes(20), 104m, SwingType.Low),
            Swing(start.AddMinutes(25), 115m, SwingType.High)
        ];

        DateTimeOffset detectedAt = start.AddMinutes(30);
        var detector = new ChannelDetector();
        IReadOnlyList<PriceChannel> channels = detector.Detect(
            [support, mismatchedResistance],
            swings,
            detectedAt,
            atr: 1m,
            currentPrice: 110m);

        Assert.That(channels, Is.Not.Empty);
        PriceChannel channel = channels[0];
        Assert.Multiple(() =>
        {
            Assert.That(channel.Direction, Is.EqualTo(ChannelDirection.Rising));
            Assert.That(channel.LowerLine.SlopePerSecond, Is.EqualTo(slope));
            Assert.That(channel.UpperLine.SlopePerSecond, Is.EqualTo(slope));
            Assert.That(channel.WidthAtr, Is.EqualTo(10m).Within(0.25m));
            Assert.That(
                channel.UpperLine.PriceAt(start) - channel.LowerLine.PriceAt(start),
                Is.EqualTo(10m).Within(0.25m));
        });
    }

    [Test]
    public void ChannelDetector_FromRansacTrendlines_FindsRisingChannel()
    {
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        // Perfect rising channel: support 100+i, resistance 110+i every 5 minutes.
        SwingPoint[] swings = Enumerable.Range(0, 5)
            .SelectMany(index => new[]
            {
                Swing(start.AddMinutes(index * 10), 100m + index, SwingType.Low),
                Swing(start.AddMinutes(index * 10 + 5), 110m + index, SwingType.High)
            })
            .ToArray();

        var trendlines = new RansacTrendlineDetector(new RansacOptions(
            MaximumIterations: 300,
            DistanceThresholdAtr: 0.15m,
            MinimumInliers: 3,
            MaximumPivots: 100,
            MaximumLinesPerType: 2));

        IReadOnlyList<Trendline> lines = trendlines.Detect(swings, atr: 1m);
        Assert.That(lines.Any(line => line.Type == TrendlineType.Support), Is.True);

        DateTimeOffset detectedAt = start.AddMinutes(55);
        var detector = new ChannelDetector();
        IReadOnlyList<PriceChannel> channels = detector.Detect(
            lines,
            swings,
            detectedAt,
            atr: 1m,
            currentPrice: 112m);

        Assert.That(channels, Is.Not.Empty);
        PriceChannel channel = channels[0];
        Assert.Multiple(() =>
        {
            Assert.That(channel.Direction, Is.EqualTo(ChannelDirection.Rising));
            Assert.That(channel.WidthAtr, Is.EqualTo(10m).Within(1m));
            Assert.That(channel.LowerLine.SlopePerSecond, Is.EqualTo(channel.UpperLine.SlopePerSecond));
            Assert.That(
                channel.LowerLine.PriceAt(detectedAt),
                Is.LessThan(channel.UpperLine.PriceAt(detectedAt)));
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