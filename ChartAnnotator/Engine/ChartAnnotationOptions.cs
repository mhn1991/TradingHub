using ChartAnnotator.PriceAction;
using ChartAnnotator.NeoWave;
using ChartAnnotator.Regime;

namespace ChartAnnotator.Engine;

public sealed record ChartAnnotationOptions
{
    public int CandleCapacity { get; init; } = 2_000;
    public int SwingCapacity { get; init; } = 500;
    public int IndicatorCapacity { get; init; } = 2_000;
    public int AtrPeriod { get; init; } = 14;
    public int AdxPeriod { get; init; } = 14;
    public int AtrAnalysisHistoryPeriod { get; init; } = 50;
    public int AtrAnalysisChangeLookback { get; init; } = 5;
    public int AtrAnalysisMinimumSamples { get; init; } = 20;
    public decimal AtrDirectionThresholdPercent { get; init; } = 5m;
    public int VolumeHistoryPeriod { get; init; } = 50;
    public int VolumeMinimumSamples { get; init; } = 20;
    public decimal VolumeLowRelativeThreshold { get; init; } = 0.70m;
    public decimal VolumeHighRelativeThreshold { get; init; } = 1.25m;
    public decimal VolumeSpikeRelativeThreshold { get; init; } = 2.0m;
    public int RsiPeriod { get; init; } = 14;
    public int RsiMomentumLookback { get; init; } = 3;
    public decimal RsiMomentumThreshold { get; init; } = 1m;
    public decimal RsiMinimumDivergenceDifference { get; init; } = 2m;
    public decimal RsiMinimumPriceDifferenceAtr { get; init; } = 0.05m;
    public int RsiSignalLifetimeCandles { get; init; } = 50;
    public int BollingerPeriod { get; init; } = 20;
    public decimal BollingerStandardDeviations { get; init; } = 2m;
    public int CciPeriod { get; init; } = 20;
    public int SmaFastPeriod { get; init; } = 50;
    public int SmaSlowPeriod { get; init; } = 200;
    public int BollingerWidthHistoryPeriod { get; init; } = 50;
    public int BollingerWidthChangeLookback { get; init; } = 5;
    public int BollingerWidthMinimumSamples { get; init; } = 20;
    public decimal BollingerWidthDirectionThresholdPercent { get; init; } = 5m;
    public decimal BollingerSqueezePercentile { get; init; } = 20m;
    public decimal BollingerWidePercentile { get; init; } = 80m;
    public int SwingLeftBars { get; init; } = 2;
    public int SwingRightBars { get; init; } = 2;
    public int HeavyAnalysisEveryCandles { get; init; } = 12;
    public decimal StructureDirectionToleranceAtr { get; init; } = 0.05m;
    public PriceActionOptions PriceAction { get; init; } = new();
    public PriceActionSetupOptions PriceActionSetups { get; init; } = new();

    public int EfficiencyRatioPeriod { get; init; } = 14;
    public int EfficiencyRatioAnalysisHistoryPeriod { get; init; } = 50;
    public int EfficiencyRatioAnalysisChangeLookback { get; init; } = 5;
    public int EfficiencyRatioAnalysisMinimumSamples { get; init; } = 20;
    public decimal EfficiencyRatioDirectionThresholdPercent { get; init; } = 10m;
    public decimal EfficiencyRatioHighlyChoppyMaximum { get; init; } = 0.20m;
    public decimal EfficiencyRatioChoppyMaximum { get; init; } = 0.40m;
    public decimal EfficiencyRatioTransitionalMaximum { get; init; } = 0.60m;
    public decimal EfficiencyRatioEfficientMaximum { get; init; } = 0.80m;
    public decimal EfficiencyRatioChoppyPercentile { get; init; } = 20m;
    public decimal EfficiencyRatioTransitionalPercentile { get; init; } = 40m;
    public decimal EfficiencyRatioEfficientPercentile { get; init; } = 60m;
    public decimal EfficiencyRatioHighlyEfficientPercentile { get; init; } = 80m;

    public int DonchianPeriod { get; init; } = 20;

    public int MaximumAnchoredValueReferences { get; init; } = 4;
    public decimal MinimumValueReferenceVolumeCoveragePercent { get; init; } = 80m;
    public int SessionValueAnchorHourUtc { get; init; }

    public MarketRegimeOptions MarketRegime { get; init; } = new();
    public NeoWaveOptions NeoWave { get; init; } = new();

    public void Validate()
    {
        if (CandleCapacity < 1 ||
            SwingCapacity < 1 ||
            IndicatorCapacity < 1 ||
            HeavyAnalysisEveryCandles < 1 ||
            AtrPeriod <= 1 ||
            AdxPeriod <= 1 ||
            AtrAnalysisHistoryPeriod < 2 ||
            AtrAnalysisChangeLookback < 1 ||
            AtrAnalysisChangeLookback >= AtrAnalysisHistoryPeriod ||
            AtrAnalysisMinimumSamples < 2 ||
            AtrAnalysisMinimumSamples > AtrAnalysisHistoryPeriod ||
            AtrDirectionThresholdPercent < 0m ||
            VolumeHistoryPeriod < 2 ||
            VolumeMinimumSamples < 2 ||
            VolumeMinimumSamples > VolumeHistoryPeriod ||
            VolumeLowRelativeThreshold is <= 0m or >= 1m ||
            VolumeHighRelativeThreshold <= 1m ||
            VolumeSpikeRelativeThreshold <= VolumeHighRelativeThreshold ||
            RsiPeriod <= 1 ||
            RsiMomentumLookback < 1 ||
            RsiMomentumThreshold < 0m ||
            RsiMinimumDivergenceDifference < 0m ||
            RsiMinimumPriceDifferenceAtr < 0m ||
            RsiSignalLifetimeCandles < 1 ||
            BollingerPeriod <= 1 ||
            BollingerStandardDeviations <= 0m ||
            CciPeriod < 2 ||
            SmaFastPeriod < 1 ||
            SmaSlowPeriod < 1 ||
            SmaSlowPeriod < SmaFastPeriod ||
            BollingerWidthHistoryPeriod < 2 ||
            BollingerWidthChangeLookback < 1 ||
            BollingerWidthChangeLookback >= BollingerWidthHistoryPeriod ||
            BollingerWidthMinimumSamples < 2 ||
            BollingerWidthMinimumSamples > BollingerWidthHistoryPeriod ||
            BollingerWidthDirectionThresholdPercent < 0m ||
            BollingerSqueezePercentile is < 0m or > 100m ||
            BollingerWidePercentile is < 0m or > 100m ||
            BollingerWidePercentile <= BollingerSqueezePercentile ||
            SwingLeftBars < 1 ||
            SwingRightBars < 1 ||
            StructureDirectionToleranceAtr < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(CandleCapacity));
        }

        if (EfficiencyRatioPeriod <= 1 ||
            EfficiencyRatioAnalysisHistoryPeriod < 2 ||
            EfficiencyRatioAnalysisChangeLookback < 1 ||
            EfficiencyRatioAnalysisChangeLookback >= EfficiencyRatioAnalysisHistoryPeriod ||
            EfficiencyRatioAnalysisMinimumSamples < 2 ||
            EfficiencyRatioAnalysisMinimumSamples > EfficiencyRatioAnalysisHistoryPeriod ||
            EfficiencyRatioDirectionThresholdPercent < 0m ||
            EfficiencyRatioHighlyChoppyMaximum is <= 0m or > 1m ||
            EfficiencyRatioChoppyMaximum <= EfficiencyRatioHighlyChoppyMaximum || EfficiencyRatioChoppyMaximum > 1m ||
            EfficiencyRatioTransitionalMaximum <= EfficiencyRatioChoppyMaximum || EfficiencyRatioTransitionalMaximum > 1m ||
            EfficiencyRatioEfficientMaximum <= EfficiencyRatioTransitionalMaximum || EfficiencyRatioEfficientMaximum > 1m ||
            EfficiencyRatioChoppyPercentile is < 0m or > 100m ||
            EfficiencyRatioTransitionalPercentile <= EfficiencyRatioChoppyPercentile || EfficiencyRatioTransitionalPercentile > 100m ||
            EfficiencyRatioEfficientPercentile <= EfficiencyRatioTransitionalPercentile || EfficiencyRatioEfficientPercentile > 100m ||
            EfficiencyRatioHighlyEfficientPercentile <= EfficiencyRatioEfficientPercentile || EfficiencyRatioHighlyEfficientPercentile > 100m)
        {
            throw new ArgumentOutOfRangeException(nameof(EfficiencyRatioPeriod));
        }

        if (DonchianPeriod <= 1 ||
            MaximumAnchoredValueReferences < 2 ||
            MinimumValueReferenceVolumeCoveragePercent is < 0m or > 100m ||
            SessionValueAnchorHourUtc is < 0 or > 23)
        {
            throw new ArgumentOutOfRangeException(nameof(DonchianPeriod));
        }

        PriceAction.Validate();
        PriceActionSetups.Validate();
        MarketRegime.Validate();
        NeoWave.Validate();
    }
}
