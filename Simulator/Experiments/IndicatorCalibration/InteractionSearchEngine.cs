using Simulator.Calibration;

namespace Simulator.Experiments.IndicatorCalibration;

public sealed record InteractionSearchStepRecord
{
    public required string GroupId { get; init; }
    public required IReadOnlyDictionary<string, decimal> NumericValues { get; init; }
    public required IReadOnlyDictionary<string, bool> AblationValues { get; init; }
    public required bool IsValidCandidate { get; init; }
    public string? InvalidReason { get; init; }
    public bool PassedEligibilityGates { get; init; }
    public string? EligibilityFailureReason { get; init; }
    public decimal? Score { get; init; }
    public required bool Accepted { get; init; }
}

public sealed record InteractionGroupResult
{
    public required string GroupId { get; init; }
    public required bool WasCapped { get; init; }
    public required int TotalCombinations { get; init; }
    public required int EvaluatedCombinations { get; init; }
    public required IReadOnlyList<InteractionSearchStepRecord> Steps { get; init; }
}

public sealed record InteractionSearchResult
{
    public required CalibrationCandidate BestCandidate { get; init; }
    public required decimal? BestScore { get; init; }
    public required IReadOnlyList<InteractionGroupResult> GroupResults { get; init; }
}

/// <summary>
/// Phase 3 of the search algorithm (blueprint §9.4). Only explicitly declared
/// <see cref="CalibrationInteractionGroup"/> combinations are searched - never an automatic
/// all-pairs sweep. Each group is seeded around the current best candidate (typically the
/// coordinate-descent winner) and only its own members vary; every other parameter stays fixed.
/// A group whose full cartesian product exceeds the cap is deterministically truncated to the
/// first N combinations in declared grid order, and the truncation is reported, never silent.
/// </summary>
public static class InteractionSearchEngine
{
    public static async Task<InteractionSearchResult> RunAsync<TOptions>(
        CalibrationCandidate seedCandidate,
        TOptions baselineOptions,
        IIndicatorCalibrationManifest<TOptions> manifest,
        ICandidateEvaluator evaluator,
        CalibrationScoringPolicy scoringPolicy,
        decimal minimumMaterialImprovement,
        int maximumCombinationsPerGroup,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seedCandidate);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(scoringPolicy);
        if (maximumCombinationsPerGroup < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumCombinationsPerGroup));

        CalibrationCandidate currentBest = seedCandidate;
        decimal? currentBestScore = await CoordinateDescentEngine.ScoreCandidateAsync(
            currentBest, baselineOptions, manifest, evaluator, scoringPolicy, cancellationToken).ConfigureAwait(false);

        var groupResults = new List<InteractionGroupResult>();
        foreach (CalibrationInteractionGroup group in manifest.InteractionGroups
                     .OrderBy(item => item.GroupId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<(IReadOnlyDictionary<string, decimal> Numeric, IReadOnlyDictionary<string, bool> Ablation)> combinations =
                BuildCombinations(group, manifest);
            bool capped = combinations.Count > maximumCombinationsPerGroup;
            IReadOnlyList<(IReadOnlyDictionary<string, decimal> Numeric, IReadOnlyDictionary<string, bool> Ablation)> evaluated =
                capped ? combinations.Take(maximumCombinationsPerGroup).ToArray() : combinations;

            var steps = new List<InteractionSearchStepRecord>();
            decimal? bestScoreThisGroup = null;
            CalibrationCandidate? bestCandidateThisGroup = null;

            foreach ((IReadOnlyDictionary<string, decimal> numeric, IReadOnlyDictionary<string, bool> ablation) in evaluated)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CalibrationCandidate attempt = currentBest;
                foreach ((string id, decimal value) in numeric)
                    attempt = attempt.WithNumericValue(id, value);
                foreach ((string id, bool value) in ablation)
                    attempt = attempt.WithAblationValue(id, value);

                if (!CalibrationCandidateValidator.TryBuildValidOptions(
                        attempt, baselineOptions, manifest, out _, out string? invalidReason))
                {
                    steps.Add(new InteractionSearchStepRecord
                    {
                        GroupId = group.GroupId,
                        NumericValues = numeric,
                        AblationValues = ablation,
                        IsValidCandidate = false,
                        InvalidReason = invalidReason,
                        Accepted = false
                    });
                    continue;
                }

                BacktestEvaluationResult evaluation = await evaluator.EvaluateAsync(attempt, cancellationToken).ConfigureAwait(false);
                CandidateScoreResult scored = CandidateScorer.Score(evaluation, scoringPolicy);
                if (scored.PassedEligibilityGates && (bestScoreThisGroup is null || scored.Score!.Value > bestScoreThisGroup.Value))
                {
                    bestScoreThisGroup = scored.Score;
                    bestCandidateThisGroup = attempt;
                }

                steps.Add(new InteractionSearchStepRecord
                {
                    GroupId = group.GroupId,
                    NumericValues = numeric,
                    AblationValues = ablation,
                    IsValidCandidate = true,
                    PassedEligibilityGates = scored.PassedEligibilityGates,
                    EligibilityFailureReason = scored.EligibilityFailureReason,
                    Score = scored.Score,
                    Accepted = false
                });
            }

            decimal baseline = currentBestScore ?? decimal.MinValue;
            if (bestScoreThisGroup is not null && bestScoreThisGroup.Value - baseline >= minimumMaterialImprovement)
            {
                currentBest = bestCandidateThisGroup!;
                currentBestScore = bestScoreThisGroup;
                int lastIndex = steps.FindLastIndex(step =>
                    step.PassedEligibilityGates && step.Score == bestScoreThisGroup.Value);
                if (lastIndex >= 0)
                    steps[lastIndex] = steps[lastIndex] with { Accepted = true };
            }

            groupResults.Add(new InteractionGroupResult
            {
                GroupId = group.GroupId,
                WasCapped = capped,
                TotalCombinations = combinations.Count,
                EvaluatedCombinations = evaluated.Count,
                Steps = steps
            });
        }

        return new InteractionSearchResult
        {
            BestCandidate = currentBest,
            BestScore = currentBestScore,
            GroupResults = groupResults
        };
    }

    private static IReadOnlyList<(IReadOnlyDictionary<string, decimal> Numeric, IReadOnlyDictionary<string, bool> Ablation)>
        BuildCombinations<TOptions>(CalibrationInteractionGroup group, IIndicatorCalibrationManifest<TOptions> manifest)
    {
        var axes = new List<(string Id, bool IsAblation, IReadOnlyList<decimal> NumericValues, IReadOnlyList<bool> AblationValues)>();
        foreach (string id in group.ParameterIds)
        {
            ICalibrationParameterDescriptor<TOptions>? numeric = manifest.Parameters
                .FirstOrDefault(item => string.Equals(item.ParameterId, id, StringComparison.Ordinal));
            if (numeric is not null)
            {
                axes.Add((id, false, numeric.CoarseGrid, []));
                continue;
            }
            ICalibrationAblationDescriptor<TOptions>? ablation = manifest.Ablations
                .FirstOrDefault(item => string.Equals(item.ParameterId, id, StringComparison.Ordinal));
            if (ablation is not null)
            {
                axes.Add((id, true, [], [false, true]));
                continue;
            }
            throw new InvalidOperationException(
                $"Interaction group '{group.GroupId}' references unknown parameter id '{id}'.");
        }

        var combinations = new List<(Dictionary<string, decimal> Numeric, Dictionary<string, bool> Ablation)>
        {
            (new Dictionary<string, decimal>(StringComparer.Ordinal), new Dictionary<string, bool>(StringComparer.Ordinal))
        };
        foreach ((string id, bool isAblation, IReadOnlyList<decimal> numericValues, IReadOnlyList<bool> ablationValues) in axes)
        {
            var next = new List<(Dictionary<string, decimal>, Dictionary<string, bool>)>();
            foreach ((Dictionary<string, decimal> numericSoFar, Dictionary<string, bool> ablationSoFar) in combinations)
            {
                if (isAblation)
                {
                    foreach (bool value in ablationValues)
                    {
                        var newAblation = new Dictionary<string, bool>(ablationSoFar, StringComparer.Ordinal) { [id] = value };
                        next.Add((numericSoFar, newAblation));
                    }
                }
                else
                {
                    foreach (decimal value in numericValues)
                    {
                        var newNumeric = new Dictionary<string, decimal>(numericSoFar, StringComparer.Ordinal) { [id] = value };
                        next.Add((newNumeric, ablationSoFar));
                    }
                }
            }
            combinations = next;
        }

        return combinations
            .Select(item => ((IReadOnlyDictionary<string, decimal>)item.Numeric, (IReadOnlyDictionary<string, bool>)item.Ablation))
            .ToArray();
    }
}
