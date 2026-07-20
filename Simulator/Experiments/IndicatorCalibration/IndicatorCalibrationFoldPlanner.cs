namespace Simulator.Experiments.IndicatorCalibration;

public sealed record CalibrationInternalFold
{
    public required int FoldId { get; init; }
    public required DateTimeOffset TrainingFrom { get; init; }
    public required DateTimeOffset TrainingTo { get; init; }
    public required DateTimeOffset ValidationFrom { get; init; }
    public required DateTimeOffset ValidationTo { get; init; }

    public void Validate()
    {
        if (FoldId < 0)
            throw new ArgumentOutOfRangeException(nameof(FoldId));
        if (TrainingFrom >= TrainingTo)
            throw new ArgumentException($"Fold {FoldId}: TrainingFrom must be earlier than exclusive TrainingTo.");
        if (TrainingTo > ValidationFrom)
            throw new ArgumentException($"Fold {FoldId}: training and validation ranges must not overlap.");
        if (ValidationFrom >= ValidationTo)
            throw new ArgumentException($"Fold {FoldId}: ValidationFrom must be earlier than exclusive ValidationTo.");
    }
}

/// <summary>
/// Carves <see cref="CalibrationTimeline.LearningFrom"/>..<see cref="CalibrationTimeline.LearningTo"/>
/// into deterministic, purged internal folds (blueprint §8, Phase 5's "internal fold planner").
/// Each fold is a contiguous, equal-length slice of the learning period; within a slice, the last
/// <c>validationFraction</c> is held out as that fold's validation range, with an embargo
/// gap immediately before it removed from the training range - the same purged walk-forward
/// discipline as <c>Simulator.Experiments.Models.SimulationExperimentTimeline</c>'s embargo gap,
/// applied per-fold instead of once for the whole run.
/// </summary>
public static class IndicatorCalibrationFoldPlanner
{
    public const decimal DefaultValidationFraction = 0.2m;

    public static IReadOnlyList<CalibrationInternalFold> BuildFolds(
        CalibrationTimeline timeline,
        int foldCount,
        int internalEmbargoDays,
        decimal validationFraction = DefaultValidationFraction)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        timeline.Validate();
        if (foldCount < 1)
            throw new ArgumentOutOfRangeException(nameof(foldCount));
        if (internalEmbargoDays < 0)
            throw new ArgumentOutOfRangeException(nameof(internalEmbargoDays));
        if (validationFraction is <= 0m or >= 1m)
            throw new ArgumentOutOfRangeException(nameof(validationFraction));

        TimeSpan totalSpan = timeline.LearningTo - timeline.LearningFrom;
        TimeSpan sliceSpan = TimeSpan.FromTicks(totalSpan.Ticks / foldCount);
        if (sliceSpan <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"The learning period ({totalSpan}) is too short to carve into {foldCount} folds.", nameof(foldCount));
        }

        var folds = new List<CalibrationInternalFold>(foldCount);
        for (int index = 0; index < foldCount; index++)
        {
            DateTimeOffset sliceStart = timeline.LearningFrom + TimeSpan.FromTicks(sliceSpan.Ticks * index);
            DateTimeOffset sliceEnd = index == foldCount - 1
                ? timeline.LearningTo
                : timeline.LearningFrom + TimeSpan.FromTicks(sliceSpan.Ticks * (index + 1));

            TimeSpan sliceLength = sliceEnd - sliceStart;
            DateTimeOffset validationFrom = sliceEnd - TimeSpan.FromTicks((long)(sliceLength.Ticks * validationFraction));
            DateTimeOffset trainingTo = validationFrom - TimeSpan.FromDays(internalEmbargoDays);

            if (trainingTo <= sliceStart)
            {
                throw new ArgumentException(
                    $"Fold {index}'s training window collapses to zero or less after applying the " +
                    $"{internalEmbargoDays}-day internal embargo - reduce the fold count or the embargo, " +
                    "or extend the learning period.");
            }

            var fold = new CalibrationInternalFold
            {
                FoldId = index,
                TrainingFrom = sliceStart,
                TrainingTo = trainingTo,
                ValidationFrom = validationFrom,
                ValidationTo = sliceEnd
            };
            fold.Validate();
            folds.Add(fold);
        }

        return folds;
    }
}
