using Simulator.Calibration;

namespace Simulator.Experiments.IndicatorCalibration;

public sealed record CoordinateDescentStepRecord
{
    public required int Pass { get; init; }
    public required string ParameterId { get; init; }
    public required decimal AttemptedValue { get; init; }
    public required bool IsValidCandidate { get; init; }
    public string? InvalidReason { get; init; }
    public bool PassedEligibilityGates { get; init; }
    public string? EligibilityFailureReason { get; init; }
    public decimal? Score { get; init; }
    public required bool Accepted { get; init; }
    public string? RejectionReason { get; init; }
}

public sealed record CoordinateDescentResult
{
    public required CalibrationCandidate BestCandidate { get; init; }
    public required decimal? BestScore { get; init; }
    public required IReadOnlyList<CoordinateDescentStepRecord> Steps { get; init; }
    public required int PassesRun { get; init; }
    public required bool Converged { get; init; }
}

/// <summary>
/// Phase 2 of the search algorithm (blueprint §9.3). Sweeps only the influential parameters
/// (in the order <see cref="SensitivityScreener.OrderByInfluence{TOptions}"/> determined),
/// holding every other parameter at the running best, for up to <c>maximumPasses</c> passes,
/// accepting a parameter change only when it beats the running best by at least
/// <c>minimumMaterialImprovement</c>. Stops early the moment a complete pass makes no accepted
/// change - never runs a fixed number of passes regardless of convergence.
/// </summary>
public static class CoordinateDescentEngine
{
    public static async Task<CoordinateDescentResult> RunAsync<TOptions>(
        CalibrationCandidate startingCandidate,
        IReadOnlyList<string> orderedInfluentialParameterIds,
        TOptions baselineOptions,
        IIndicatorCalibrationManifest<TOptions> manifest,
        ICandidateEvaluator evaluator,
        CalibrationScoringPolicy scoringPolicy,
        decimal minimumMaterialImprovement,
        int maximumPasses,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startingCandidate);
        ArgumentNullException.ThrowIfNull(orderedInfluentialParameterIds);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(scoringPolicy);
        if (maximumPasses < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumPasses));

        Dictionary<string, ICalibrationParameterDescriptor<TOptions>> byId = manifest.Parameters
            .ToDictionary(parameter => parameter.ParameterId, StringComparer.Ordinal);

        var steps = new List<CoordinateDescentStepRecord>();
        CalibrationCandidate currentBest = startingCandidate;
        decimal? currentBestScore = await ScoreCandidateAsync(
            currentBest, baselineOptions, manifest, evaluator, scoringPolicy, cancellationToken).ConfigureAwait(false);

        int passesRun = 0;
        bool converged = false;
        for (int pass = 1; pass <= maximumPasses; pass++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            passesRun = pass;
            bool anyChangeThisPass = false;

            foreach (string parameterId in orderedInfluentialParameterIds)
            {
                if (!byId.TryGetValue(parameterId, out ICalibrationParameterDescriptor<TOptions>? parameter))
                    continue; // an ablation-only interaction-group id would never appear here; defensive skip.

                decimal? bestValueThisSweep = null;
                decimal? bestScoreThisSweep = null;
                foreach (decimal value in parameter.CoarseGrid)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    CalibrationCandidate attempt = currentBest.WithNumericValue(parameterId, value);
                    if (!CalibrationCandidateValidator.TryBuildValidOptions(
                            attempt, baselineOptions, manifest, out _, out string? invalidReason))
                    {
                        steps.Add(new CoordinateDescentStepRecord
                        {
                            Pass = pass,
                            ParameterId = parameterId,
                            AttemptedValue = value,
                            IsValidCandidate = false,
                            InvalidReason = invalidReason,
                            Accepted = false,
                            RejectionReason = invalidReason
                        });
                        continue;
                    }

                    BacktestEvaluationResult evaluation = await evaluator.EvaluateAsync(attempt, cancellationToken)
                        .ConfigureAwait(false);
                    CandidateScoreResult scored = CandidateScorer.Score(evaluation, scoringPolicy);
                    bool isBestSoFarThisSweep = scored.PassedEligibilityGates &&
                        (bestScoreThisSweep is null || scored.Score!.Value > bestScoreThisSweep.Value);
                    if (isBestSoFarThisSweep)
                    {
                        bestValueThisSweep = value;
                        bestScoreThisSweep = scored.Score;
                    }

                    steps.Add(new CoordinateDescentStepRecord
                    {
                        Pass = pass,
                        ParameterId = parameterId,
                        AttemptedValue = value,
                        IsValidCandidate = true,
                        PassedEligibilityGates = scored.PassedEligibilityGates,
                        EligibilityFailureReason = scored.EligibilityFailureReason,
                        Score = scored.Score,
                        Accepted = false // corrected to true below for the one value actually accepted, if any.
                    });
                }

                decimal baseline = currentBestScore ?? decimal.MinValue;
                bool accept = bestScoreThisSweep is not null && bestScoreThisSweep.Value - baseline >= minimumMaterialImprovement;
                if (accept)
                {
                    currentBest = currentBest.WithNumericValue(parameterId, bestValueThisSweep!.Value);
                    currentBestScore = bestScoreThisSweep;
                    anyChangeThisPass = true;
                    int lastIndex = steps.FindLastIndex(step =>
                        step.Pass == pass && step.ParameterId == parameterId && step.AttemptedValue == bestValueThisSweep.Value);
                    if (lastIndex >= 0)
                        steps[lastIndex] = steps[lastIndex] with { Accepted = true };
                }
            }

            if (!anyChangeThisPass)
            {
                converged = true;
                break;
            }
        }

        return new CoordinateDescentResult
        {
            BestCandidate = currentBest,
            BestScore = currentBestScore,
            Steps = steps,
            PassesRun = passesRun,
            Converged = converged
        };
    }

    internal static async Task<decimal?> ScoreCandidateAsync<TOptions>(
        CalibrationCandidate candidate,
        TOptions baselineOptions,
        IIndicatorCalibrationManifest<TOptions> manifest,
        ICandidateEvaluator evaluator,
        CalibrationScoringPolicy scoringPolicy,
        CancellationToken cancellationToken)
    {
        if (!CalibrationCandidateValidator.TryBuildValidOptions(candidate, baselineOptions, manifest, out _, out _))
            return null;
        BacktestEvaluationResult evaluation = await evaluator.EvaluateAsync(candidate, cancellationToken).ConfigureAwait(false);
        CandidateScoreResult scored = CandidateScorer.Score(evaluation, scoringPolicy);
        return scored.PassedEligibilityGates ? scored.Score : null;
    }
}
