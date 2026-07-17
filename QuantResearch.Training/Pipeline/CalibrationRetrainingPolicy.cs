namespace QuantResearch.Training.Pipeline;

/// <summary>
/// Config-bound "should we retrain this strategy's calibration right now" rule for one
/// (StrategyId, Instrument) pair, consumed by <c>LiveTradingHost</c>'s background training
/// scheduler. Deliberately simple for a first version: age-based, plus a manual override -
/// "do not rerun all three [calibration stages] automatically on every host restart; only start
/// a new research job when a configured training/retraining policy says retraining is needed."
/// </summary>
public sealed record CalibrationRetrainingPolicy
{
    public bool Enabled { get; init; }
    public required string StrategyId { get; init; }
    public required string StrategyVersion { get; init; }
    public required string Instrument { get; init; }
    /// <summary>How stale the newest ApprovedForDemo profile must be before a retrain is triggered.</summary>
    public TimeSpan MinimumArtifactAgeBeforeRetrain { get; init; } = TimeSpan.FromDays(7);
    /// <summary>Always retrain on this startup regardless of freshness - a manual override, not a steady-state setting.</summary>
    public bool ForceRetrainOnStartup { get; init; }
    public int TrainingWindowDays { get; init; } = 180;
    public int Folds { get; init; } = 5;
    public int EmbargoHours { get; init; } = 24;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(StrategyId))
            throw new ArgumentException("StrategyId is required.");
        if (string.IsNullOrWhiteSpace(StrategyVersion))
            throw new ArgumentException("StrategyVersion is required.");
        if (string.IsNullOrWhiteSpace(Instrument))
            throw new ArgumentException("Instrument is required.");
        if (MinimumArtifactAgeBeforeRetrain < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(MinimumArtifactAgeBeforeRetrain));
        if (TrainingWindowDays < 1)
            throw new ArgumentOutOfRangeException(nameof(TrainingWindowDays));
        if (Folds < 3)
            throw new ArgumentOutOfRangeException(nameof(Folds), "At least 3 folds are required (2 usable + 1 reserved test fold).");
        if (EmbargoHours < 0)
            throw new ArgumentOutOfRangeException(nameof(EmbargoHours));
    }
}
