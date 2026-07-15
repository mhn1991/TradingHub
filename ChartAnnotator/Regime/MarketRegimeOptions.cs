namespace ChartAnnotator.Regime;

/// <summary>
/// Configuration for <c>MarketRegimeClassifier</c>: master enable switch, hysteresis
/// (confirmation/persistence/switch-margin) and the deterministic rule-hierarchy
/// thresholds. Disabled by default so classification is fully opt-in.
/// </summary>
public sealed record MarketRegimeOptions
{
    public bool Enabled { get; init; }

    public int MinimumConfirmationBars { get; init; } = 2;
    public int MinimumPersistenceBars { get; init; } = 3;

    /// <summary>
    /// A challenger regime must clear this many points above the classifier's
    /// neutral baseline confidence (50) to become authoritative - an absolute floor
    /// on the challenger's own confidence, not a margin over the previously
    /// confirmed regime's confidence (which would eventually become unreachable
    /// once that regime stops being reconfirmed, since confidence is capped at 100).
    /// </summary>
    public decimal SwitchConfidenceMargin { get; init; } = 10m;
    public decimal MaximumTradeableSpreadAtr { get; init; } = 0.15m;
    public decimal HardMaximumSpreadAtr { get; init; } = 0.30m;

    public int AdxCalibrationHistoryPeriod { get; init; } = 100;
    public int AdxCalibrationMinimumSamples { get; init; } = 30;
    public decimal TrendAdxMinimumPercentile { get; init; } = 60m;
    public decimal RangeAdxMaximumPercentile { get; init; } = 30m;
    public decimal FallbackTrendAdxMinimum { get; init; } = 25m;
    public decimal FallbackRangeAdxMaximum { get; init; } = 18m;

    public decimal TrendMinimumEfficiencyRatio { get; init; } = 0.30m;
    public decimal RangeMaximumEfficiencyRatio { get; init; } = 0.30m;
    public decimal DisorderMaximumEfficiencyRatio { get; init; } = 0.25m;
    public int DisplacementLookbackBars { get; init; } = 5;

    public void Validate()
    {
        if (MinimumConfirmationBars < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumConfirmationBars));
        }

        if (MinimumPersistenceBars < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumPersistenceBars));
        }

        if (SwitchConfidenceMargin < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(SwitchConfidenceMargin));
        }

        if (MaximumTradeableSpreadAtr <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumTradeableSpreadAtr));
        }

        if (HardMaximumSpreadAtr <= MaximumTradeableSpreadAtr)
        {
            throw new ArgumentOutOfRangeException(nameof(HardMaximumSpreadAtr));
        }

        if (AdxCalibrationHistoryPeriod < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(AdxCalibrationHistoryPeriod));
        }

        if (AdxCalibrationMinimumSamples < 2 || AdxCalibrationMinimumSamples > AdxCalibrationHistoryPeriod)
        {
            throw new ArgumentOutOfRangeException(nameof(AdxCalibrationMinimumSamples));
        }

        if (TrendAdxMinimumPercentile is <= 0m or > 100m)
        {
            throw new ArgumentOutOfRangeException(nameof(TrendAdxMinimumPercentile));
        }

        if (RangeAdxMaximumPercentile is <= 0m or > 100m || RangeAdxMaximumPercentile >= TrendAdxMinimumPercentile)
        {
            throw new ArgumentOutOfRangeException(nameof(RangeAdxMaximumPercentile));
        }

        if (FallbackTrendAdxMinimum <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(FallbackTrendAdxMinimum));
        }

        if (FallbackRangeAdxMaximum <= 0m || FallbackRangeAdxMaximum >= FallbackTrendAdxMinimum)
        {
            throw new ArgumentOutOfRangeException(nameof(FallbackRangeAdxMaximum));
        }

        if (TrendMinimumEfficiencyRatio is <= 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(TrendMinimumEfficiencyRatio));
        }

        if (RangeMaximumEfficiencyRatio is <= 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(RangeMaximumEfficiencyRatio));
        }

        if (DisorderMaximumEfficiencyRatio is <= 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(DisorderMaximumEfficiencyRatio));
        }

        if (DisplacementLookbackBars < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(DisplacementLookbackBars));
        }
    }
}
