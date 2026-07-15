using Agent.Strategies;
using Brokers.Models;
using ChartAnnotator.Models;

namespace Simulator.Tests;

[TestFixture]
public sealed class ZoneVolumeSignalPolicyTests
{
    private static readonly InstrumentKey Instrument = new("FX:GBP/USD");
    private static readonly BarInterval Interval = BarInterval.Minutes(5);
    private static readonly DateTimeOffset Now = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public void SupportAndElevatedAlignedVolume_StrengthenRsiRelationship()
    {
        RsiRelationshipSnapshot relationship = Relationship(
            RsiRelationshipType.RegularBullishDivergence);
        AnalysisSnapshot snapshot = Snapshot(
            open: 99m,
            close: 100m,
            zones: [Zone(99m, 99.6m, PriceZoneType.Support, 80m)],
            volume: Volume(VolumeRegime.High, 1.6m));
        var rsi = new RsiBollingerSignalAssessment
        {
            AlignedRsiRelationship = relationship,
            Explanation = "test"
        };

        ZoneVolumeSignalAssessment result = ZoneVolumeSignalPolicy.Evaluate(
            snapshot,
            PriceActionDirection.Bullish,
            rsi);

        Assert.Multiple(() =>
        {
            Assert.That(result.SupportingZone, Is.Not.Null);
            Assert.That(result.ElevatedVolumeAligned, Is.True);
            Assert.That(result.RsiZoneConfluence, Is.True);
            Assert.That(result.RsiVolumeConfluence, Is.True);
            Assert.That(result.HasConfluenceTrigger, Is.True);
            Assert.That(result.ConfidenceAdjustment, Is.Positive);
            Assert.That(result.IsVetoed, Is.False);
        });
    }

    [Test]
    public void ResistanceAndElevatedAlignedVolume_StrengthenBearishRsiRelationship()
    {
        RsiRelationshipSnapshot relationship = Relationship(
            RsiRelationshipType.BearishConvergence);
        AnalysisSnapshot snapshot = Snapshot(
            open: 101m,
            close: 100m,
            zones: [Zone(100.4m, 101m, PriceZoneType.Resistance, 80m)],
            volume: Volume(VolumeRegime.High, 1.6m));
        var rsi = new RsiBollingerSignalAssessment
        {
            AlignedRsiRelationship = relationship,
            Explanation = "test"
        };

        ZoneVolumeSignalAssessment result = ZoneVolumeSignalPolicy.Evaluate(
            snapshot,
            PriceActionDirection.Bearish,
            rsi);

        Assert.Multiple(() =>
        {
            Assert.That(result.SupportingZone?.Type, Is.EqualTo(PriceZoneType.Resistance));
            Assert.That(result.ElevatedVolumeAligned, Is.True);
            Assert.That(result.RsiZoneConfluence, Is.True);
            Assert.That(result.RsiVolumeConfluence, Is.True);
            Assert.That(result.HasConfluenceTrigger, Is.True);
            Assert.That(result.ConfidenceAdjustment, Is.Positive);
            Assert.That(result.IsVetoed, Is.False);
        });
    }

    [Test]
    public void NearbyResistance_VetoesBuyUnlessHighVolumeSqueezeBreakoutConfirms()
    {
        AnalysisSnapshot snapshot = Snapshot(
            open: 99m,
            close: 100m,
            zones: [Zone(100.2m, 100.6m, PriceZoneType.Resistance, 80m)],
            volume: Volume(VolumeRegime.High, 1.7m));
        var ordinary = new RsiBollingerSignalAssessment
        {
            Explanation = "test"
        };
        RsiBollingerSignalAssessment breakout = ordinary with
        {
            BollingerReleaseTrigger = true
        };

        ZoneVolumeSignalAssessment blocked = ZoneVolumeSignalPolicy.Evaluate(
            snapshot,
            PriceActionDirection.Bullish,
            ordinary);
        ZoneVolumeSignalAssessment allowedBreakout = ZoneVolumeSignalPolicy.Evaluate(
            snapshot,
            PriceActionDirection.Bullish,
            breakout);

        Assert.Multiple(() =>
        {
            Assert.That(blocked.OpposingZone, Is.Not.Null);
            Assert.That(blocked.VetoReasonCode, Is.EqualTo("NearbyOpposingZone"));
            Assert.That(allowedBreakout.IsVetoed, Is.False);
            Assert.That(allowedBreakout.ElevatedVolumeAligned, Is.True);
        });
    }

    [Test]
    public void OpposingVolumeSpike_VetoesWithoutPretendingVolumeHasOwnDirection()
    {
        AnalysisSnapshot snapshot = Snapshot(
            open: 101m,
            close: 100m,
            zones: [],
            volume: Volume(VolumeRegime.Spike, 2.4m));
        var rsi = new RsiBollingerSignalAssessment
        {
            Explanation = "test"
        };

        ZoneVolumeSignalAssessment result = ZoneVolumeSignalPolicy.Evaluate(
            snapshot,
            PriceActionDirection.Bullish,
            rsi);

        Assert.Multiple(() =>
        {
            Assert.That(result.OpposingVolumeSpike, Is.True);
            Assert.That(result.ElevatedVolumeAligned, Is.False);
            Assert.That(result.HasConfluenceTrigger, Is.False);
            Assert.That(result.VetoReasonCode, Is.EqualTo("OpposingVolumeSpike"));
        });
    }

    [Test]
    public void FarOpposingZone_DoesNotReduceCurrentEntryConfidence()
    {
        AnalysisSnapshot snapshot = Snapshot(
            open: 99m,
            close: 100m,
            zones: [Zone(105m, 106m, PriceZoneType.Resistance, 90m)],
            volume: VolumeAnalysisSnapshot.Empty);
        var rsi = new RsiBollingerSignalAssessment
        {
            Explanation = "test"
        };

        ZoneVolumeSignalAssessment result = ZoneVolumeSignalPolicy.Evaluate(
            snapshot,
            PriceActionDirection.Bullish,
            rsi);

        Assert.Multiple(() =>
        {
            Assert.That(result.OpposingZone, Is.Null);
            Assert.That(result.ConfidenceAdjustment, Is.Zero);
            Assert.That(result.IsVetoed, Is.False);
        });
    }

    private static AnalysisSnapshot Snapshot(
        decimal open,
        decimal close,
        IReadOnlyList<PriceZone> zones,
        VolumeAnalysisSnapshot volume) => new()
    {
        Instrument = Instrument,
        Interval = Interval,
        AvailableAt = Now,
        Version = 1,
        LatestCandle = TestCandles.Create(
            Instrument,
            Now.AddMinutes(-5),
            Interval,
            open,
            Math.Max(open, close) + 0.5m,
            Math.Min(open, close) - 0.5m,
            close),
        Indicators = new IndicatorSnapshot
        {
            Atr = 2m,
            Rsi = 50m,
            BollingerMiddle = 100m,
            VolumeAnalysis = volume
        },
        Swings = [],
        PriceZones = zones,
        Trendlines = [],
        Channels = [],
        Confidence = new ConfidenceScore { Total = 60m, Contributions = [] }
    };

    private static PriceZone Zone(
        decimal lower,
        decimal upper,
        PriceZoneType type,
        decimal strength) => new()
    {
        LowerPrice = lower,
        UpperPrice = upper,
        CentrePrice = (lower + upper) / 2m,
        TouchCount = 3,
        Strength = strength,
        Type = type
    };

    private static VolumeAnalysisSnapshot Volume(
        VolumeRegime regime,
        decimal relative) => new()
    {
        Value = 160m,
        Kind = VolumeKind.TickCount,
        BaselineMedian = 100m,
        RelativeToBaseline = relative,
        Percentile = regime == VolumeRegime.Spike ? 98m : 85m,
        Regime = regime,
        SampleCount = 30,
        IsReliable = true
    };

    private static RsiRelationshipSnapshot Relationship(RsiRelationshipType type) => new()
    {
        Type = type,
        FirstPivotTime = Now.AddMinutes(-30),
        SecondPivotTime = Now.AddMinutes(-10),
        ConfirmedAt = Now.AddMinutes(-5),
        FirstPrice = 101m,
        SecondPrice = 100m,
        FirstRsi = 30m,
        SecondRsi = 40m,
        PriceChange = -1m,
        RsiChange = 10m,
        Strength = 75m,
        AgeCandles = 1
    };
}
