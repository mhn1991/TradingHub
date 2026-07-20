namespace Simulator.Experiments.IndicatorCalibration;

/// <summary>Deterministic overflow handling when a planned search would exceed its budget (blueprint §11). Never silent truncation.</summary>
public enum CalibrationBudgetOverflowPolicy
{
    Reject,
    ReduceRefinement,
    ReduceStartingPoints,
    SkipLowerPriorityInteractions
}

/// <summary>Hard and warning limits on how large a calibration run is allowed to become (blueprint §11).</summary>
public sealed record CalibrationEvaluationBudget
{
    public int WarningEvaluationCount { get; init; }
    public int MaximumEvaluationCount { get; init; }
    public int MaximumEvaluationsPerFold { get; init; }
    public int MaximumInteractionCombinationsPerGroup { get; init; }
    public TimeSpan HardRuntimeLimit { get; init; }
    public CalibrationBudgetOverflowPolicy OverflowPolicy { get; init; } = CalibrationBudgetOverflowPolicy.Reject;

    public void Validate()
    {
        if (WarningEvaluationCount < 1)
            throw new ArgumentOutOfRangeException(nameof(WarningEvaluationCount));
        if (MaximumEvaluationCount < WarningEvaluationCount)
        {
            throw new ArgumentException(
                "MaximumEvaluationCount must be >= WarningEvaluationCount.", nameof(MaximumEvaluationCount));
        }
        if (MaximumEvaluationsPerFold < 1)
            throw new ArgumentOutOfRangeException(nameof(MaximumEvaluationsPerFold));
        if (MaximumInteractionCombinationsPerGroup < 1)
            throw new ArgumentOutOfRangeException(nameof(MaximumInteractionCombinationsPerGroup));
        if (HardRuntimeLimit <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(HardRuntimeLimit));
        if (!Enum.IsDefined(OverflowPolicy))
            throw new ArgumentOutOfRangeException(nameof(OverflowPolicy));
    }
}

/// <summary>
/// The upfront evaluation-count breakdown shown to a caller before any backtest runs (blueprint
/// §11). Data shape only - computing this from a manifest/request/budget is Phase 4's
/// <c>EvaluationBudgetEstimator</c> responsibility, not defined here.
/// </summary>
public sealed record CalibrationBudgetPreview
{
    public required int BaselineEvaluations { get; init; }
    public required int SensitivityEvaluations { get; init; }
    public required int StartingPointEvaluations { get; init; }
    public required int MaximumCoordinatePasses { get; init; }
    public required int InteractionEvaluations { get; init; }
    public required int RefinementEvaluations { get; init; }
    public required int InternalFoldMultiplier { get; init; }
    public required int ExternalHoldoutEvaluations { get; init; }
    public required int EstimatedCacheHits { get; init; }
    public required int ExpectedUncachedBacktests { get; init; }
    public required long EstimatedCandleEvaluations { get; init; }
    public required TimeSpan EstimatedDurationLow { get; init; }
    public required TimeSpan EstimatedDurationHigh { get; init; }
    public required TimeSpan HardRuntimeLimit { get; init; }
    public required bool ExceedsBudget { get; init; }
    public required CalibrationBudgetOverflowPolicy? AppliedOverflowPolicy { get; init; }
    public required IReadOnlyList<string> OverflowAdjustments { get; init; }

    public int TotalPlannedEvaluations =>
        BaselineEvaluations + SensitivityEvaluations + StartingPointEvaluations +
        InteractionEvaluations + RefinementEvaluations + ExternalHoldoutEvaluations;
}
