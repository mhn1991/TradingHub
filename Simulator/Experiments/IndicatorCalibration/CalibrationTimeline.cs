namespace Simulator.Experiments.IndicatorCalibration;

/// <summary>
/// The complete historical range a calibration request spans (blueprint §8): a learning period
/// (from which internal purged walk-forward folds are later carved - the actual fold-carving
/// algorithm is Phase 5 scope, deliberately not implemented here) followed by an embargo gap and
/// a final external holdout range that is never touched during search. Data contract only.
/// </summary>
public sealed record CalibrationTimeline
{
    public required DateTimeOffset LearningFrom { get; init; }
    public required DateTimeOffset LearningTo { get; init; }
    public required DateTimeOffset ExternalHoldoutFrom { get; init; }
    public required DateTimeOffset ExternalHoldoutTo { get; init; }
    public int EmbargoDays { get; init; } = 10;
    public int WarmupDays { get; init; } = 21;

    public void Validate()
    {
        if (LearningFrom.Offset != TimeSpan.Zero || LearningTo.Offset != TimeSpan.Zero ||
            ExternalHoldoutFrom.Offset != TimeSpan.Zero || ExternalHoldoutTo.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Calibration timeline boundaries must be UTC.");
        }
        if (LearningFrom >= LearningTo)
            throw new ArgumentException("LearningFrom must be earlier than exclusive LearningTo.");
        if (LearningTo > ExternalHoldoutFrom)
            throw new ArgumentException("Learning and external-holdout ranges must not overlap.");
        if (ExternalHoldoutFrom >= ExternalHoldoutTo)
            throw new ArgumentException("ExternalHoldoutFrom must be earlier than exclusive ExternalHoldoutTo.");
        if (EmbargoDays < 0 || WarmupDays < 0)
            throw new ArgumentException("Embargo and warmup days cannot be negative.");
        if (ExternalHoldoutFrom - LearningTo < TimeSpan.FromDays(EmbargoDays))
            throw new ArgumentException("The learning/external-holdout embargo gap is too short.");
    }

    public DateTimeOffset ResolveLearningStreamFrom() => LearningFrom.AddDays(-WarmupDays);
    public DateTimeOffset ResolveExternalHoldoutStreamFrom() => ExternalHoldoutFrom.AddDays(-WarmupDays);
}
