namespace ChartAnnotator.PriceAction;

public sealed record PriceActionOptions
{
    public decimal MinimumBreakCloseAtr { get; init; } = 0.10m;
    public decimal RetestProximityAtr { get; init; } = 0.25m;
    public decimal MaximumRetestDepthAtr { get; init; } = 0.20m;
    public int RetestExpiryBars { get; init; } = 15;
    public decimal MinimumRejectionWickToBody { get; init; } = 1.5m;
    public decimal MinimumRejectionClosePosition { get; init; } = 0.65m;
    public decimal MinimumRejectionRangeAtr { get; init; } = 0.40m;
    public decimal MaximumStructureDistanceAtr { get; init; } = 0.25m;
    public decimal MinimumDisplacementBodyMedianMultiple { get; init; } = 1.20m;
    public decimal MinimumDisplacementRangeAtr { get; init; } = 0.80m;
    public decimal MinimumDisplacementClosePosition { get; init; } = 0.75m;
    /// <summary>
    /// Used while the per-timeframe body calibration profile is still warming up.
    /// </summary>
    public decimal DisplacementBodyAtrFallback { get; init; } = 0.55m;
    public decimal MinimumSweepPenetrationAtr { get; init; } = 0.02m;
    public decimal MaximumSweepPenetrationAtr { get; init; } = 0.30m;
    public decimal MinimumSweepRecoveryAtr { get; init; } = 0.02m;
    /// <summary>
    /// How many recent confirmed swings of each type may seed a liquidity-sweep check.
    /// </summary>
    public int MaxSweepCandidateSwings { get; init; } = 12;
    /// <summary>
    /// Expansion-only (non-squeeze) Bollinger breakouts must also clear this range.
    /// </summary>
    public decimal MinimumExpansionBreakoutRangeAtr { get; init; } = 0.60m;
    public int CalibrationLookback { get; init; } = 250;
    public int CalibrationMinimumSamples { get; init; } = 50;
    public decimal MinimumTriggerConfidence { get; init; } = 55m;

    public void Validate()
    {
        if (MinimumBreakCloseAtr < 0m ||
            RetestProximityAtr < 0m ||
            MaximumRetestDepthAtr < 0m ||
            RetestExpiryBars < 1 ||
            MinimumRejectionWickToBody < 0m ||
            MinimumRejectionClosePosition is < 0m or > 1m ||
            MinimumRejectionRangeAtr < 0m ||
            MaximumStructureDistanceAtr < 0m ||
            MinimumDisplacementBodyMedianMultiple <= 0m ||
            MinimumDisplacementRangeAtr < 0m ||
            MinimumDisplacementClosePosition is < 0m or > 1m ||
            DisplacementBodyAtrFallback <= 0m ||
            MinimumSweepPenetrationAtr < 0m ||
            MaximumSweepPenetrationAtr < MinimumSweepPenetrationAtr ||
            MinimumSweepRecoveryAtr < 0m ||
            MaxSweepCandidateSwings < 1 ||
            MinimumExpansionBreakoutRangeAtr < 0m ||
            CalibrationLookback < 10 ||
            CalibrationMinimumSamples < 2 ||
            CalibrationMinimumSamples > CalibrationLookback ||
            MinimumTriggerConfidence is < 0m or > 100m)
        {
            throw new ArgumentOutOfRangeException(nameof(PriceActionOptions));
        }
    }
}
