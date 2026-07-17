using Brokers.Models;
using ChartAnnotator.Models;
using ChartAnnotator.Value;

namespace Simulator.Tests;

/// <summary>
/// Proves the trend-quality evidence evaluator (efficiency ratio, ATR volatility regime,
/// Bollinger width regime, ADX strengthening) is a real, pure, reason-coded evaluation
/// over already-computed indicator regimes - soft evidence only, disabled by default,
/// never a hard veto.
/// </summary>
[TestFixture]
public sealed class TrendQualityEvidenceEvaluatorTests
{
    private static readonly InstrumentKey Instrument = new("FX:GBP/USD");
    private static readonly DateTimeOffset Time = new(2026, 1, 5, 10, 0, 0, TimeSpan.Zero);

    [Test]
    public void Disabled_ProducesNoEvidence()
    {
        AnalysisSnapshot snapshot = Snapshot(efficiency: MarketEfficiencyState.Choppy);

        TrendQualityEvidence evidence = TrendQualityEvidenceEvaluator.Evaluate(
            snapshot, new TrendQualityEvidenceOptions { Enabled = false });

        Assert.That(evidence.ReasonCodes, Is.Empty);
    }

    [Test]
    public void ChoppyMarket_AddsCautionaryEvidenceAndNegativeAdjustment()
    {
        AnalysisSnapshot snapshot = Snapshot(efficiency: MarketEfficiencyState.HighlyChoppy);

        TrendQualityEvidence evidence = TrendQualityEvidenceEvaluator.Evaluate(snapshot, EnabledOptions());

        Assert.Multiple(() =>
        {
            Assert.That(evidence.ReasonCodes, Does.Contain("MarketChoppy"));
            Assert.That(evidence.ConfidenceAdjustment, Is.LessThan(0m));
        });
    }

    [Test]
    public void EfficientMarket_AddsSupportiveEvidenceAndPositiveAdjustment()
    {
        AnalysisSnapshot snapshot = Snapshot(efficiency: MarketEfficiencyState.HighlyEfficient);

        TrendQualityEvidence evidence = TrendQualityEvidenceEvaluator.Evaluate(snapshot, EnabledOptions());

        Assert.Multiple(() =>
        {
            Assert.That(evidence.ReasonCodes, Does.Contain("MarketEfficient"));
            Assert.That(evidence.ConfidenceAdjustment, Is.GreaterThan(0m));
        });
    }

    [Test]
    public void ExtremeVolatilityRegime_AddsCautionaryEvidence()
    {
        AnalysisSnapshot snapshot = Snapshot(atrRegime: AtrVolatilityRegime.VeryHigh);

        TrendQualityEvidence evidence = TrendQualityEvidenceEvaluator.Evaluate(snapshot, EnabledOptions());

        Assert.Multiple(() =>
        {
            Assert.That(evidence.ReasonCodes, Does.Contain("VolatilityRegimeExtreme"));
            Assert.That(evidence.ConfidenceAdjustment, Is.LessThan(0m));
        });
    }

    [Test]
    public void DeadVolatilityRegime_AddsCautionaryEvidence()
    {
        AnalysisSnapshot snapshot = Snapshot(atrRegime: AtrVolatilityRegime.VeryLow);

        TrendQualityEvidence evidence = TrendQualityEvidenceEvaluator.Evaluate(snapshot, EnabledOptions());

        Assert.That(evidence.ReasonCodes, Does.Contain("VolatilityRegimeDead"));
    }

    [Test]
    public void UnresolvedSqueeze_AddsCautionaryEvidence()
    {
        AnalysisSnapshot snapshot = Snapshot(bollingerWidth: BollingerWidthRegime.Squeeze, squeezeReleased: false);

        TrendQualityEvidence evidence = TrendQualityEvidenceEvaluator.Evaluate(snapshot, EnabledOptions());

        Assert.Multiple(() =>
        {
            Assert.That(evidence.ReasonCodes, Does.Contain("BollingerSqueezeUnresolved"));
            Assert.That(evidence.ConfidenceAdjustment, Is.LessThan(0m));
        });
    }

    [Test]
    public void ConfirmedExpansion_AddsSupportiveEvidence()
    {
        AnalysisSnapshot snapshot = Snapshot(isExpansion: true);

        TrendQualityEvidence evidence = TrendQualityEvidenceEvaluator.Evaluate(snapshot, EnabledOptions());

        Assert.Multiple(() =>
        {
            Assert.That(evidence.ReasonCodes, Does.Contain("BollingerExpansionSupportsTrade"));
            Assert.That(evidence.ConfidenceAdjustment, Is.GreaterThan(0m));
        });
    }

    [Test]
    public void SqueezeReleased_AddsSupportiveEvidenceEvenIfStillLabelledSqueeze()
    {
        // Squeeze regime label plus a confirmed release must count as supportive, not
        // cautionary - the release is what matters, not the leftover regime label.
        AnalysisSnapshot snapshot = Snapshot(bollingerWidth: BollingerWidthRegime.Squeeze, squeezeReleased: true);

        TrendQualityEvidence evidence = TrendQualityEvidenceEvaluator.Evaluate(snapshot, EnabledOptions());

        Assert.Multiple(() =>
        {
            Assert.That(evidence.ReasonCodes, Does.Contain("BollingerExpansionSupportsTrade"));
            Assert.That(evidence.ReasonCodes, Does.Not.Contain("BollingerSqueezeUnresolved"));
        });
    }

    [Test]
    public void AdxStrengthening_AddsSupportiveEvidence()
    {
        AnalysisSnapshot snapshot = Snapshot(adxStrengthening: true);

        TrendQualityEvidence evidence = TrendQualityEvidenceEvaluator.Evaluate(snapshot, EnabledOptions());

        Assert.Multiple(() =>
        {
            Assert.That(evidence.ReasonCodes, Does.Contain("AdxTrendStrengthening"));
            Assert.That(evidence.ConfidenceAdjustment, Is.GreaterThan(0m));
        });
    }

    [Test]
    public void AdxWeakening_AddsCautionaryEvidence()
    {
        AnalysisSnapshot snapshot = Snapshot(adxDirection: MomentumDirection.Falling);

        TrendQualityEvidence evidence = TrendQualityEvidenceEvaluator.Evaluate(snapshot, EnabledOptions());

        Assert.Multiple(() =>
        {
            Assert.That(evidence.ReasonCodes, Does.Contain("AdxTrendWeakening"));
            Assert.That(evidence.ConfidenceAdjustment, Is.LessThan(0m));
        });
    }

    [Test]
    public void AllSignalsNeutral_ProducesNoEvidence()
    {
        AnalysisSnapshot snapshot = Snapshot();

        TrendQualityEvidence evidence = TrendQualityEvidenceEvaluator.Evaluate(snapshot, EnabledOptions());

        Assert.That(evidence.ReasonCodes, Is.Empty);
    }

    private static TrendQualityEvidenceOptions EnabledOptions() => new()
    {
        Enabled = true,
        ConfidenceAdjustmentPerSignal = 3m
    };

    private static AnalysisSnapshot Snapshot(
        MarketEfficiencyState efficiency = MarketEfficiencyState.Transitional,
        AtrVolatilityRegime atrRegime = AtrVolatilityRegime.Normal,
        BollingerWidthRegime bollingerWidth = BollingerWidthRegime.Normal,
        bool isExpansion = false,
        bool squeezeReleased = false,
        bool adxStrengthening = false,
        MomentumDirection adxDirection = MomentumDirection.Stable) => new()
    {
        Instrument = Instrument,
        Interval = BarInterval.Minutes(5),
        AvailableAt = Time,
        Version = 1,
        LatestCandle = TestCandles.Create(Instrument, Time, BarInterval.Minutes(5), 1.10m, 1.11m, 1.09m, 1.105m),
        Indicators = new IndicatorSnapshot
        {
            EfficiencyAnalysis = EfficiencyAnalysisSnapshot.Empty with { State = efficiency },
            AtrAnalysis = AtrAnalysisSnapshot.Empty with { Regime = atrRegime },
            BollingerAnalysis = BollingerAnalysisSnapshot.Empty with
            {
                WidthRegime = bollingerWidth,
                IsExpansion = isExpansion,
                SqueezeReleased = squeezeReleased
            },
            AdxAnalysis = AdxAnalysisSnapshot.Empty with
            {
                IsTrendStrengthening = adxStrengthening,
                StrengthDirection = adxDirection
            }
        },
        Swings = [],
        PriceZones = [],
        Trendlines = [],
        Channels = [],
        MarketStructure = MarketStructureSnapshot.Empty,
        Confidence = new ConfidenceScore { Total = 0m, Contributions = [] }
    };
}
