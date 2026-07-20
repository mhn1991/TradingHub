namespace Simulator.Experiments.Models;

/// <summary>Leakage boundary expressed as UTC half-open ranges.</summary>
public sealed record SimulationExperimentTimeline
{
    public required DateTimeOffset LearningFrom { get; init; }
    public required DateTimeOffset LearningTo { get; init; }
    public required DateTimeOffset EvaluationFrom { get; init; }
    public required DateTimeOffset EvaluationTo { get; init; }
    public int TrainingWarmupDays { get; init; } = 21;
    public int EvaluationWarmupDays { get; init; } = 21;
    public int EmbargoDays { get; init; } = 10;

    public DateTimeOffset ResolveTrainingStreamFrom() => LearningFrom.AddDays(-TrainingWarmupDays);

    public DateTimeOffset ResolveEvaluationStreamFrom() => EvaluationFrom.AddDays(-EvaluationWarmupDays);

    public void Validate()
    {
        DateTimeOffset[] boundaries = [LearningFrom, LearningTo, EvaluationFrom, EvaluationTo];
        if (boundaries.Any(item => item.Offset != TimeSpan.Zero))
            throw new ArgumentException("Experiment timeline boundaries must be UTC.");
        if (LearningFrom >= LearningTo)
            throw new ArgumentException("LearningFrom must be earlier than exclusive LearningTo.");
        if (LearningTo > EvaluationFrom)
            throw new ArgumentException("Learning and evaluation ranges must not overlap.");
        if (EvaluationFrom >= EvaluationTo)
            throw new ArgumentException("EvaluationFrom must be earlier than exclusive EvaluationTo.");
        if (TrainingWarmupDays < 0 || EvaluationWarmupDays < 0 || EmbargoDays < 0)
            throw new ArgumentException("Warm-up and embargo days cannot be negative.");
        if (EvaluationFrom - LearningTo < TimeSpan.FromDays(EmbargoDays))
            throw new ArgumentException("The external learning/evaluation embargo is too short.");
    }
}

public sealed record RelativeExperimentTimelineRequest
{
    public required DateTimeOffset EvaluationFrom { get; init; }
    public required DateTimeOffset EvaluationTo { get; init; }
    public int LearningMonths { get; init; }
    public int LearningDays { get; init; }
    public int EmbargoDays { get; init; } = 10;
    public int TrainingWarmupDays { get; init; } = 21;
    public int EvaluationWarmupDays { get; init; } = 21;

    public SimulationExperimentTimeline Resolve()
    {
        if (LearningMonths < 0 || LearningDays < 0 || LearningMonths + LearningDays == 0)
            throw new ArgumentException("A positive relative learning window is required.");
        DateTimeOffset learningTo = EvaluationFrom.AddDays(-EmbargoDays);
        var resolved = new SimulationExperimentTimeline
        {
            LearningFrom = learningTo.AddMonths(-LearningMonths).AddDays(-LearningDays),
            LearningTo = learningTo,
            EvaluationFrom = EvaluationFrom,
            EvaluationTo = EvaluationTo,
            EmbargoDays = EmbargoDays,
            TrainingWarmupDays = TrainingWarmupDays,
            EvaluationWarmupDays = EvaluationWarmupDays
        };
        resolved.Validate();
        return resolved;
    }
}
