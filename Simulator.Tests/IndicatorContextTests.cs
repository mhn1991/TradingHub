using Brokers.Models;
using ChartAnnotator.Engine;
using ChartAnnotator.Indicators;
using ChartAnnotator.Models;

namespace Simulator.Tests;

[TestFixture]
public sealed class IndicatorContextTests
{
    private static readonly InstrumentKey Instrument = new("FX:GBP/USD");
    private static readonly BarInterval Interval = BarInterval.Minutes(5);
    private static readonly DateTimeOffset Start =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public void BollingerAnalysis_DetectsSqueezeThenExpansion()
    {
        var bands = new BollingerState(period: 3, standardDeviations: 2m);
        var analysis = new BollingerAnalysisState(
            historyPeriod: 8,
            changeLookback: 1,
            minimumSamples: 4,
            directionThresholdPercent: 1m,
            squeezePercentile: 25m,
            widePercentile: 75m);

        BollingerAnalysisSnapshot snapshot = BollingerAnalysisSnapshot.Empty;
        foreach (decimal close in new[] { 90m, 110m, 90m, 110m, 90m, 110m, 100m, 100m, 100m, 100m })
        {
            bands.Update(close);
            snapshot = analysis.Update(close, bands);
        }

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.IsSqueeze, Is.True);
            Assert.That(snapshot.WidthRegime, Is.EqualTo(BollingerWidthRegime.Squeeze));
            Assert.That(snapshot.WidthPercentile, Is.LessThanOrEqualTo(25m));
        });

        bands.Update(90m);
        snapshot = analysis.Update(90m, bands);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.WidthDirection, Is.EqualTo(VolatilityDirection.Expanding));
            Assert.That(snapshot.SqueezeReleased || snapshot.IsExpansion, Is.True);
            Assert.That(snapshot.BandwidthPercent, Is.GreaterThan(0m));
            Assert.That(snapshot.PercentB, Is.Not.Null);
        });
    }

    [Test]
    public void AtrAnalysis_ClassifiesExpansionAndContractionRelativeToHistory()
    {
        var analysis = new AtrAnalysisState(
            historyPeriod: 6,
            changeLookback: 1,
            minimumSamples: 4,
            directionThresholdPercent: 5m);

        AtrAnalysisSnapshot snapshot = AtrAnalysisSnapshot.Empty;
        for (int index = 0; index < 4; index++)
        {
            snapshot = analysis.Update(atr: 1m, close: 100m);
        }

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Regime, Is.EqualTo(AtrVolatilityRegime.Normal));
            Assert.That(snapshot.Direction, Is.EqualTo(VolatilityDirection.Stable));
            Assert.That(snapshot.NormalizedPercent, Is.EqualTo(1m));
        });

        snapshot = analysis.Update(atr: 2m, close: 100m);
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Direction, Is.EqualTo(VolatilityDirection.Expanding));
            Assert.That(snapshot.Regime, Is.EqualTo(AtrVolatilityRegime.VeryHigh));
            Assert.That(snapshot.ChangePercent, Is.EqualTo(100m));
        });

        snapshot = analysis.Update(atr: 0.5m, close: 100m);
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Direction, Is.EqualTo(VolatilityDirection.Contracting));
            Assert.That(snapshot.Regime, Is.EqualTo(AtrVolatilityRegime.VeryLow));
        });
    }

    [Test]
    public void RsiAnalysis_DetectsRegularBullishDivergenceOnlyAfterSwingConfirmation()
    {
        var analysis = new RsiAnalysisState(
            sampleCapacity: 20,
            pivotCapacity: 10,
            momentumLookback: 1,
            momentumThreshold: 0.5m,
            minimumRsiDifference: 2m,
            minimumPriceDifferenceAtr: 0.05m,
            signalLifetimeCandles: 20);

        analysis.Update(Candle(0, 100m), 25m, [], atr: 2m);
        analysis.Update(Candle(1, 101m), 30m, [], atr: 2m);
        RsiAnalysisSnapshot first = analysis.Update(
            Candle(2, 102m),
            35m,
            [Swing(0, 100m, SwingType.Low, confirmedAtIndex: 2)],
            atr: 2m);

        Assert.That(first.LatestRelationship, Is.Null);

        analysis.Update(Candle(3, 98m), 35m, [], atr: 2m);
        analysis.Update(Candle(4, 99m), 40m, [], atr: 2m);
        RsiAnalysisSnapshot second = analysis.Update(
            Candle(5, 100m),
            45m,
            [Swing(3, 95m, SwingType.Low, confirmedAtIndex: 5)],
            atr: 2m);

        RsiRelationshipSnapshot relationship = second.LatestRelationship!;
        Assert.Multiple(() =>
        {
            Assert.That(second.IsNewRelationship, Is.True);
            Assert.That(relationship.Type, Is.EqualTo(RsiRelationshipType.RegularBullishDivergence));
            Assert.That(relationship.IsDivergence, Is.True);
            Assert.That(relationship.FirstRsi, Is.EqualTo(25m));
            Assert.That(relationship.SecondRsi, Is.EqualTo(35m));
            Assert.That(relationship.SecondPrice, Is.LessThan(relationship.FirstPrice));
            Assert.That(relationship.ConfirmedAt, Is.EqualTo(Candle(5, 100m).CloseTime));
        });
    }

    [Test]
    public void RsiAnalysis_DetectsSameDirectionConvergence()
    {
        var analysis = new RsiAnalysisState(
            sampleCapacity: 20,
            pivotCapacity: 10,
            momentumLookback: 1,
            momentumThreshold: 0.5m,
            minimumRsiDifference: 2m);

        analysis.Update(Candle(0, 100m), 60m, [], atr: 2m);
        analysis.Update(Candle(1, 101m), 62m, [], atr: 2m);
        analysis.Update(
            Candle(2, 102m),
            64m,
            [Swing(0, 105m, SwingType.High, confirmedAtIndex: 2)],
            atr: 2m);
        analysis.Update(Candle(3, 105m), 70m, [], atr: 2m);
        analysis.Update(Candle(4, 106m), 71m, [], atr: 2m);
        RsiAnalysisSnapshot result = analysis.Update(
            Candle(5, 107m),
            72m,
            [Swing(3, 110m, SwingType.High, confirmedAtIndex: 5)],
            atr: 2m);

        Assert.Multiple(() =>
        {
            Assert.That(result.LatestRelationship?.Type,
                Is.EqualTo(RsiRelationshipType.BullishConvergence));
            Assert.That(result.LatestRelationship?.IsConvergence, Is.True);
            Assert.That(result.Zone, Is.EqualTo(RsiZone.Overbought));
            Assert.That(result.MomentumDirection, Is.EqualTo(MomentumDirection.Rising));
        });
    }

    [Test]
    public void RsiAnalysis_DetectsRegularBearishAndHiddenBullishDivergence()
    {
        RsiRelationshipType bearish = DetectRelationship(
            SwingType.High,
            firstPrice: 105m,
            secondPrice: 110m,
            firstRsi: 72m,
            secondRsi: 60m);
        RsiRelationshipType hiddenBullish = DetectRelationship(
            SwingType.Low,
            firstPrice: 100m,
            secondPrice: 105m,
            firstRsi: 32m,
            secondRsi: 22m);

        Assert.Multiple(() =>
        {
            Assert.That(bearish, Is.EqualTo(RsiRelationshipType.RegularBearishDivergence));
            Assert.That(hiddenBullish, Is.EqualTo(RsiRelationshipType.HiddenBullishDivergence));
        });
    }

    [Test]
    public async Task AnnotationEngine_ExposesDerivedIndicatorContextAndHistory()
    {
        var engine = new ChartAnnotationEngine(new ChartAnnotationOptions
        {
            CandleCapacity = 50,
            SwingCapacity = 20,
            IndicatorCapacity = 50,
            AtrPeriod = 3,
            AtrAnalysisHistoryPeriod = 8,
            AtrAnalysisChangeLookback = 1,
            AtrAnalysisMinimumSamples = 4,
            RsiPeriod = 3,
            RsiMomentumLookback = 1,
            BollingerPeriod = 3,
            BollingerWidthHistoryPeriod = 8,
            BollingerWidthChangeLookback = 1,
            BollingerWidthMinimumSamples = 4,
            HeavyAnalysisEveryCandles = 1
        });

        AnalysisSnapshot? latest = null;
        decimal[] closes = [100m, 102m, 99m, 103m, 98m, 104m, 100m, 100m, 100m, 105m];
        for (int index = 0; index < closes.Length; index++)
        {
            Candle candle = Candle(index, closes[index]);
            latest = await engine.ProcessAsync(new CandleClosedEvent(
                Instrument,
                Interval,
                candle,
                index + 1));
        }

        IReadOnlyList<IndicatorPoint> history = engine.GetIndicatorHistory(Instrument, Interval);
        Assert.Multiple(() =>
        {
            Assert.That(latest!.Indicators.AtrAnalysis.NormalizedPercent, Is.Not.Null);
            Assert.That(latest.Indicators.AtrAnalysis.SampleCount, Is.GreaterThan(0));
            Assert.That(latest.Indicators.BollingerAnalysis.BandwidthPercent, Is.Not.Null);
            Assert.That(latest.Indicators.RsiAnalysis.Zone, Is.Not.EqualTo(RsiZone.Unknown));
            Assert.That(history[^1].AtrAnalysis, Is.Not.Null);
            Assert.That(history[^1].RsiAnalysis, Is.Not.Null);
            Assert.That(history[^1].BollingerAnalysis, Is.Not.Null);
        });
    }

    private static RsiRelationshipType DetectRelationship(
        SwingType swingType,
        decimal firstPrice,
        decimal secondPrice,
        decimal firstRsi,
        decimal secondRsi)
    {
        var analysis = new RsiAnalysisState(
            sampleCapacity: 20,
            pivotCapacity: 10,
            momentumLookback: 1,
            minimumRsiDifference: 2m);

        analysis.Update(Candle(0, firstPrice), firstRsi, [], atr: 2m);
        analysis.Update(Candle(1, firstPrice), firstRsi, [], atr: 2m);
        analysis.Update(
            Candle(2, firstPrice),
            firstRsi,
            [Swing(0, firstPrice, swingType, confirmedAtIndex: 2)],
            atr: 2m);
        analysis.Update(Candle(3, secondPrice), secondRsi, [], atr: 2m);
        analysis.Update(Candle(4, secondPrice), secondRsi, [], atr: 2m);
        RsiAnalysisSnapshot result = analysis.Update(
            Candle(5, secondPrice),
            secondRsi,
            [Swing(3, secondPrice, swingType, confirmedAtIndex: 5)],
            atr: 2m);

        return result.LatestRelationship?.Type ?? RsiRelationshipType.None;
    }

    private static Candle Candle(int index, decimal close) => TestCandles.Create(
        Instrument,
        Start.AddMinutes(index * 5),
        Interval,
        close,
        close + 1m,
        close - 1m,
        close);

    private static SwingPoint Swing(
        int pivotIndex,
        decimal price,
        SwingType type,
        int confirmedAtIndex) => new()
        {
            PivotTime = Start.AddMinutes(pivotIndex * 5),
            ConfirmedAt = Start.AddMinutes((confirmedAtIndex + 1) * 5),
            Price = price,
            Type = type,
            Strength = 2
        };
}
