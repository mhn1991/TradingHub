using ChartAnnotator.PriceAction;

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
    public int RsiPeriod { get; init; } = 14;
    public int RsiMomentumLookback { get; init; } = 3;
    public decimal RsiMomentumThreshold { get; init; } = 1m;
    public decimal RsiMinimumDivergenceDifference { get; init; } = 2m;
    public decimal RsiMinimumPriceDifferenceAtr { get; init; } = 0.05m;
    public int RsiSignalLifetimeCandles { get; init; } = 50;
    public int BollingerPeriod { get; init; } = 20;
    public decimal BollingerStandardDeviations { get; init; } = 2m;
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
}
