using QuantResearch.Models;

namespace QuantResearch.Validation;

public sealed record WalkForwardPlan
{
    public required TimeSpan TrainingWindow { get; init; }
    public required TimeSpan ValidationWindow { get; init; }
    public required TimeSpan TestWindow { get; init; }
    public required TimeSpan Step { get; init; }
    public required TimeSpan PurgeGap { get; init; }

    public void Validate()
    {
        if (TrainingWindow <= TimeSpan.Zero || ValidationWindow <= TimeSpan.Zero || TestWindow <= TimeSpan.Zero ||
            Step <= TimeSpan.Zero || PurgeGap < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(WalkForwardPlan));
    }
}

public sealed record WalkForwardFold
{
    public required int Fold { get; init; }
    public required DateTimeOffset TrainingFrom { get; init; }
    public required DateTimeOffset TrainingTo { get; init; }
    public required DateTimeOffset ValidationFrom { get; init; }
    public required DateTimeOffset ValidationTo { get; init; }
    public required DateTimeOffset TestFrom { get; init; }
    public required DateTimeOffset TestTo { get; init; }
}

public sealed record WalkForwardFoldResult
{
    public required WalkForwardFold Window { get; init; }
    /// <summary>Instrument this fold was run for (plans may contain multiple).</summary>
    public required string Instrument { get; init; }
    /// <summary>Strategy id this fold was run for (plans may contain multiple).</summary>
    public required string StrategyId { get; init; }
    /// <summary>In-sample window used to select parameters.</summary>
    public required ResearchPerformance Training { get; init; }
    /// <summary>First out-of-sample check with the selected parameters.</summary>
    public required ResearchPerformance Validation { get; init; }
    /// <summary>Final holdout window with the selected parameters.</summary>
    public required ResearchPerformance Test { get; init; }
    public required IReadOnlyDictionary<string, string> SelectedParameters { get; init; }
    /// <summary>Validation Sharpe minus test Sharpe (positive means test degraded).</summary>
    public required decimal ValidationToTestDegradation { get; init; }
}

public static class WalkForwardPlanner
{
    public static IReadOnlyList<WalkForwardFold> Create(
        DateTimeOffset from,
        DateTimeOffset to,
        WalkForwardPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate();
        var folds = new List<WalkForwardFold>();
        DateTimeOffset trainingFrom = from;
        int index = 0;
        while (true)
        {
            DateTimeOffset trainingTo = trainingFrom + plan.TrainingWindow;
            DateTimeOffset validationFrom = trainingTo + plan.PurgeGap;
            DateTimeOffset validationTo = validationFrom + plan.ValidationWindow;
            DateTimeOffset testFrom = validationTo + plan.PurgeGap;
            DateTimeOffset testTo = testFrom + plan.TestWindow;
            if (testTo > to) break;
            folds.Add(new WalkForwardFold
            {
                Fold = index++,
                TrainingFrom = trainingFrom,
                TrainingTo = trainingTo,
                ValidationFrom = validationFrom,
                ValidationTo = validationTo,
                TestFrom = testFrom,
                TestTo = testTo
            });
            trainingFrom += plan.Step;
        }
        return folds;
    }
}

public sealed record PurgedTimeSeriesFold<T>
{
    public required IReadOnlyList<T> Training { get; init; }
    public required IReadOnlyList<T> Test { get; init; }
}

public static class PurgedTimeSeriesCrossValidator
{
    public static IReadOnlyList<PurgedTimeSeriesFold<T>> Split<T>(
        IReadOnlyList<T> samples,
        Func<T, DateTimeOffset> labelStart,
        Func<T, DateTimeOffset> labelEnd,
        int folds,
        TimeSpan embargo)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (folds < 2 || embargo < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(folds));
        T[] ordered = samples.OrderBy(labelStart).ThenBy(labelEnd).ToArray();
        int size = (int)Math.Ceiling(ordered.Length / (double)folds);
        var result = new List<PurgedTimeSeriesFold<T>>();
        for (int fold = 0; fold < folds; fold++)
        {
            T[] test = ordered.Skip(fold * size).Take(size).ToArray();
            if (test.Length == 0) continue;
            DateTimeOffset testFrom = test.Min(labelStart);
            DateTimeOffset testTo = test.Max(labelEnd);
            T[] training = ordered.Where(item => !test.Contains(item) &&
                (labelEnd(item) < testFrom - embargo || labelStart(item) > testTo + embargo)).ToArray();
            result.Add(new PurgedTimeSeriesFold<T> { Training = training, Test = test });
        }
        return result;
    }
}
