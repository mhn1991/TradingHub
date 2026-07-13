namespace ChartAnnotator.Engine;

public sealed record ChartAnnotationOptions
{
    public int CandleCapacity { get; init; } = 2_000;
    public int SwingCapacity { get; init; } = 500;
    public int IndicatorCapacity { get; init; } = 2_000;
    public int AtrPeriod { get; init; } = 14;
    public int RsiPeriod { get; init; } = 14;
    public int BollingerPeriod { get; init; } = 20;
    public decimal BollingerStandardDeviations { get; init; } = 2m;
    public int SwingLeftBars { get; init; } = 2;
    public int SwingRightBars { get; init; } = 2;
    public int HeavyAnalysisEveryCandles { get; init; } = 12;
}
