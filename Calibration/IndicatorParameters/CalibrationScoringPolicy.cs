namespace Simulator.Calibration;

/// <summary>
/// Eligibility-gate thresholds and objective weighting for scoring one calibration candidate
/// (blueprint §10). Eligibility gates apply first; only candidates that pass are ranked by the
/// configured risk-adjusted objective.
/// </summary>
public sealed record CalibrationScoringPolicy
{
    public required string PolicyVersion { get; init; }
    public required string ObjectiveId { get; init; }

    public required int MinimumTradesPerFold { get; init; }
    public required decimal MaximumDrawdownR { get; init; }
    public required decimal MinimumMedianExpectancyR { get; init; }
    public required decimal MinimumProfitFactor { get; init; }
    public required decimal DrawdownPenaltyWeight { get; init; }
    public required decimal TurnoverPenaltyWeight { get; init; }

    /// <summary>
    /// Blueprint §9.3's "minimum material-improvement threshold" - how much a candidate must beat
    /// the running best by, *during* the search itself, to be accepted as a step change. Distinct
    /// from <see cref="CalibrationAcceptancePolicy.MinimumImprovementOverBaseline"/>, which gates
    /// the final candidate against the real baseline, not one search step against the previous one.
    /// </summary>
    public decimal MinimumStepImprovement { get; init; } = 0.001m;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(PolicyVersion) || string.IsNullOrWhiteSpace(ObjectiveId))
            throw new ArgumentException("PolicyVersion and ObjectiveId are required.");
        if (MinimumTradesPerFold < 1)
            throw new ArgumentOutOfRangeException(nameof(MinimumTradesPerFold));
        if (MaximumDrawdownR <= 0m)
            throw new ArgumentOutOfRangeException(nameof(MaximumDrawdownR));
        if (MinimumProfitFactor < 0m)
            throw new ArgumentOutOfRangeException(nameof(MinimumProfitFactor));
        if (DrawdownPenaltyWeight < 0m || TurnoverPenaltyWeight < 0m)
            throw new ArgumentException("Penalty weights cannot be negative.");
        if (MinimumStepImprovement < 0m)
            throw new ArgumentOutOfRangeException(nameof(MinimumStepImprovement));
    }
}

/// <summary>
/// Fold-aggregation and final-holdout acceptance thresholds (blueprint §10). These are what
/// separate "improved on training" from "accepted as a promotable candidate."
/// </summary>
public sealed record CalibrationAcceptancePolicy
{
    public required string PolicyVersion { get; init; }

    public required decimal MinimumImprovementOverBaseline { get; init; }
    public required decimal MinimumAcceptableFoldPercent { get; init; }
    public required decimal MaximumTrainValidationDegradation { get; init; }
    public required decimal MinimumExternalHoldoutExpectancyR { get; init; }
    public required decimal MaximumExternalHoldoutDrawdownR { get; init; }
    public required int MinimumExternalHoldoutTrades { get; init; }
    public required decimal MinimumPlateauSupport { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(PolicyVersion))
            throw new ArgumentException("PolicyVersion is required.");
        if (MinimumImprovementOverBaseline < 0m)
            throw new ArgumentOutOfRangeException(nameof(MinimumImprovementOverBaseline));
        if (MinimumAcceptableFoldPercent is < 0m or > 100m)
            throw new ArgumentOutOfRangeException(nameof(MinimumAcceptableFoldPercent));
        if (MaximumTrainValidationDegradation < 0m)
            throw new ArgumentOutOfRangeException(nameof(MaximumTrainValidationDegradation));
        if (MaximumExternalHoldoutDrawdownR <= 0m)
            throw new ArgumentOutOfRangeException(nameof(MaximumExternalHoldoutDrawdownR));
        if (MinimumExternalHoldoutTrades < 1)
            throw new ArgumentOutOfRangeException(nameof(MinimumExternalHoldoutTrades));
        if (MinimumPlateauSupport is < 0m or > 100m)
            throw new ArgumentOutOfRangeException(nameof(MinimumPlateauSupport));
    }
}
