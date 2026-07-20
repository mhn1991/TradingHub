using Simulator.Calibration;

namespace Simulator.Experiments.IndicatorCalibration;

public sealed record LocalRefinementStepRecord
{
    public required string ParameterId { get; init; }
    public required decimal AttemptedValue { get; init; }
    public required bool IsValidCandidate { get; init; }
    public string? InvalidReason { get; init; }
    public bool PassedEligibilityGates { get; init; }
    public string? EligibilityFailureReason { get; init; }
    public decimal? Score { get; init; }
    public required bool Accepted { get; init; }
}

public sealed record LocalRefinementResult
{
    public required CalibrationCandidate BestCandidate { get; init; }
    public required decimal? BestScore { get; init; }
    public required IReadOnlyList<LocalRefinementStepRecord> Steps { get; init; }
}

/// <summary>
/// Phase 4 of the search algorithm (blueprint §9.5). For each parameter to refine, generates a
/// narrow 5-point grid centred on the current winner (offsets of -2, -1, 0, +1, +2 times the
/// descriptor's <see cref="ICalibrationParameterDescriptor{TOptions}.RefinementStep"/> - matching
/// the blueprint's own worked example: winner 2.0, step 0.15 → grid 1.7, 1.85, 2.0, 2.15, 2.3),
/// drops any point outside the descriptor's hard bounds before scheduling, and prefers a stable
/// local plateau over an isolated spike by tie-breaking near-equal scores toward the smallest
/// offset from the original winner (the same "centre of a stable plateau" preference blueprint
/// §10 states for final tie-breaking, applied here at refinement-selection time too).
/// </summary>
public static class LocalRefiner
{
    private static readonly decimal[] Offsets = [-2m, -1m, 0m, 1m, 2m];

    /// <summary>Scores within this tolerance of each other are treated as a tie for plateau-centre preference.</summary>
    public const decimal DefaultPlateauTieEpsilon = 0.001m;

    public static async Task<LocalRefinementResult> RunAsync<TOptions>(
        CalibrationCandidate startingCandidate,
        IReadOnlyList<string> parameterIdsToRefine,
        TOptions baselineOptions,
        IIndicatorCalibrationManifest<TOptions> manifest,
        ICandidateEvaluator evaluator,
        CalibrationScoringPolicy scoringPolicy,
        decimal minimumMaterialImprovement,
        decimal plateauTieEpsilon = DefaultPlateauTieEpsilon,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startingCandidate);
        ArgumentNullException.ThrowIfNull(parameterIdsToRefine);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(scoringPolicy);

        Dictionary<string, ICalibrationParameterDescriptor<TOptions>> byId = manifest.Parameters
            .ToDictionary(parameter => parameter.ParameterId, StringComparer.Ordinal);

        CalibrationCandidate currentBest = startingCandidate;
        decimal? currentBestScore = await CoordinateDescentEngine.ScoreCandidateAsync(
            currentBest, baselineOptions, manifest, evaluator, scoringPolicy, cancellationToken).ConfigureAwait(false);
        var steps = new List<LocalRefinementStepRecord>();

        foreach (string parameterId in parameterIdsToRefine)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byId.TryGetValue(parameterId, out ICalibrationParameterDescriptor<TOptions>? parameter))
                continue;

            decimal winner = currentBest.RequireNumericValue(parameterId);
            (decimal Offset, decimal Value, decimal Score)? bestThisRefinement = null;

            foreach (decimal offset in Offsets)
            {
                decimal candidateValue = winner + parameter.RefinementStep * offset;
                if (candidateValue < parameter.HardMinimum || candidateValue > parameter.HardMaximum)
                {
                    steps.Add(new LocalRefinementStepRecord
                    {
                        ParameterId = parameterId,
                        AttemptedValue = candidateValue,
                        IsValidCandidate = false,
                        InvalidReason = $"Refined value {candidateValue} is outside hard bounds " +
                            $"[{parameter.HardMinimum}, {parameter.HardMaximum}].",
                        Accepted = false
                    });
                    continue;
                }

                CalibrationCandidate attempt = currentBest.WithNumericValue(parameterId, candidateValue);
                if (!CalibrationCandidateValidator.TryBuildValidOptions(
                        attempt, baselineOptions, manifest, out _, out string? invalidReason))
                {
                    steps.Add(new LocalRefinementStepRecord
                    {
                        ParameterId = parameterId,
                        AttemptedValue = candidateValue,
                        IsValidCandidate = false,
                        InvalidReason = invalidReason,
                        Accepted = false
                    });
                    continue;
                }

                BacktestEvaluationResult evaluation = await evaluator.EvaluateAsync(attempt, cancellationToken).ConfigureAwait(false);
                CandidateScoreResult scored = CandidateScorer.Score(evaluation, scoringPolicy);
                if (scored.PassedEligibilityGates)
                {
                    bool isBetter = bestThisRefinement is null ||
                        scored.Score!.Value > bestThisRefinement.Value.Score + plateauTieEpsilon;
                    bool isPlateauTieCloserToCentre = !isBetter && bestThisRefinement is not null &&
                        scored.Score!.Value >= bestThisRefinement.Value.Score - plateauTieEpsilon &&
                        Math.Abs(offset) < Math.Abs(bestThisRefinement.Value.Offset);
                    if (isBetter || isPlateauTieCloserToCentre)
                        bestThisRefinement = (offset, candidateValue, scored.Score!.Value);
                }

                steps.Add(new LocalRefinementStepRecord
                {
                    ParameterId = parameterId,
                    AttemptedValue = candidateValue,
                    IsValidCandidate = true,
                    PassedEligibilityGates = scored.PassedEligibilityGates,
                    EligibilityFailureReason = scored.EligibilityFailureReason,
                    Score = scored.Score,
                    Accepted = false
                });
            }

            decimal baseline = currentBestScore ?? decimal.MinValue;
            if (bestThisRefinement is not null && bestThisRefinement.Value.Score - baseline >= minimumMaterialImprovement)
            {
                currentBest = currentBest.WithNumericValue(parameterId, bestThisRefinement.Value.Value);
                currentBestScore = bestThisRefinement.Value.Score;
                int lastIndex = steps.FindLastIndex(step =>
                    step.ParameterId == parameterId && step.AttemptedValue == bestThisRefinement.Value.Value);
                if (lastIndex >= 0)
                    steps[lastIndex] = steps[lastIndex] with { Accepted = true };
            }
        }

        return new LocalRefinementResult
        {
            BestCandidate = currentBest,
            BestScore = currentBestScore,
            Steps = steps
        };
    }
}
