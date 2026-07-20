using Simulator.Calibration;

namespace Simulator.Experiments.IndicatorCalibration;

public enum ParameterSensitivityClassification
{
    Influential,
    Negligible,
    Unstable,
    InsufficientEvidence
}

public sealed record SensitivityGridPoint
{
    public required decimal Value { get; init; }
    public required bool IsValidCandidate { get; init; }
    public string? InvalidReason { get; init; }
    public bool PassedEligibilityGates { get; init; }
    public string? EligibilityFailureReason { get; init; }
    public decimal? Score { get; init; }
}

public sealed record ParameterSensitivityResult
{
    public required string ParameterId { get; init; }
    public required ParameterSensitivityClassification Classification { get; init; }
    public required decimal InfluenceScore { get; init; }
    public required string Reason { get; init; }
    public required IReadOnlyList<SensitivityGridPoint> GridResults { get; init; }
}

/// <summary>
/// Phase 1 of the search algorithm (blueprint §9.2). For each numeric parameter, sweeps its
/// coarse grid holding every other parameter at the starting configuration, and classifies the
/// parameter's practical impact. Parameters classified <see cref="ParameterSensitivityClassification.Negligible"/>,
/// <see cref="ParameterSensitivityClassification.Unstable"/>, or
/// <see cref="ParameterSensitivityClassification.InsufficientEvidence"/> are excluded from
/// coordinate descent - every exclusion carries a recorded reason.
/// </summary>
public static class SensitivityScreener
{
    /// <summary>Score range below this is treated as practically negligible influence.</summary>
    public const decimal DefaultNegligibleInfluenceThreshold = 0.02m;

    public static async Task<IReadOnlyList<ParameterSensitivityResult>> ScreenAsync<TOptions>(
        CalibrationCandidate startingCandidate,
        TOptions baselineOptions,
        IIndicatorCalibrationManifest<TOptions> manifest,
        ICandidateEvaluator evaluator,
        CalibrationScoringPolicy scoringPolicy,
        decimal negligibleInfluenceThreshold = DefaultNegligibleInfluenceThreshold,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startingCandidate);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(scoringPolicy);

        var results = new List<ParameterSensitivityResult>();
        // Deterministic evaluation order: the manifest's own declared order (its DeclaredSearchOrder,
        // falling back to declaration order for ties) - blueprint §9.2's own runtime re-ordering by
        // measured influence happens as a *separate* step after this method returns (see
        // OrderByInfluence below), never inside the screening sweep itself.
        foreach (ICalibrationParameterDescriptor<TOptions> parameter in manifest.Parameters
                     .OrderBy(item => item.DeclaredSearchOrder)
                     .ThenBy(item => item.ParameterId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var gridResults = new List<SensitivityGridPoint>();
            foreach (decimal value in parameter.CoarseGrid)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CalibrationCandidate candidate = startingCandidate.WithNumericValue(parameter.ParameterId, value);
                if (!CalibrationCandidateValidator.TryBuildValidOptions(
                        candidate, baselineOptions, manifest, out _, out string? rejectionReason))
                {
                    gridResults.Add(new SensitivityGridPoint
                    {
                        Value = value,
                        IsValidCandidate = false,
                        InvalidReason = rejectionReason
                    });
                    continue;
                }

                BacktestEvaluationResult evaluation = await evaluator.EvaluateAsync(candidate, cancellationToken)
                    .ConfigureAwait(false);
                CandidateScoreResult scored = CandidateScorer.Score(evaluation, scoringPolicy);
                gridResults.Add(new SensitivityGridPoint
                {
                    Value = value,
                    IsValidCandidate = true,
                    PassedEligibilityGates = scored.PassedEligibilityGates,
                    EligibilityFailureReason = scored.EligibilityFailureReason,
                    Score = scored.Score
                });
            }

            results.Add(Classify(parameter.ParameterId, gridResults, negligibleInfluenceThreshold));
        }

        return results;
    }

    private static ParameterSensitivityResult Classify(
        string parameterId, IReadOnlyList<SensitivityGridPoint> gridResults, decimal negligibleThreshold)
    {
        decimal[] eligibleScores = gridResults
            .Where(point => point.IsValidCandidate && point.PassedEligibilityGates && point.Score is not null)
            .Select(point => point.Score!.Value)
            .ToArray();

        if (eligibleScores.Length < 2)
        {
            return new ParameterSensitivityResult
            {
                ParameterId = parameterId,
                Classification = ParameterSensitivityClassification.InsufficientEvidence,
                InfluenceScore = 0m,
                Reason = $"Only {eligibleScores.Length} of {gridResults.Count} coarse-grid values produced an eligible score.",
                GridResults = gridResults
            };
        }

        decimal range = eligibleScores.Max() - eligibleScores.Min();
        if (range < negligibleThreshold)
        {
            return new ParameterSensitivityResult
            {
                ParameterId = parameterId,
                Classification = ParameterSensitivityClassification.Negligible,
                InfluenceScore = range,
                Reason = $"Score range {range:F4} across the coarse grid is below the negligible-influence threshold {negligibleThreshold:F4}.",
                GridResults = gridResults
            };
        }

        // Simple, deterministic "smoothness" check: count sign changes in the sequence of
        // consecutive differences across the (validity-preserving) grid order. Two or more sign
        // changes means the relationship is not monotonic-nor-single-peaked across the declared
        // grid - a jagged/unstable-looking response rather than a smooth one worth trusting for
        // coordinate descent to climb.
        int signChanges = CountSignChanges(gridResults);
        if (signChanges >= 2)
        {
            return new ParameterSensitivityResult
            {
                ParameterId = parameterId,
                Classification = ParameterSensitivityClassification.Unstable,
                InfluenceScore = range,
                Reason = $"Score sequence across the coarse grid changes direction {signChanges} times - not a smooth, trustable response.",
                GridResults = gridResults
            };
        }

        return new ParameterSensitivityResult
        {
            ParameterId = parameterId,
            Classification = ParameterSensitivityClassification.Influential,
            InfluenceScore = range,
            Reason = $"Score range {range:F4} across the coarse grid exceeds the negligible threshold with a stable response shape.",
            GridResults = gridResults
        };
    }

    private static int CountSignChanges(IReadOnlyList<SensitivityGridPoint> gridResults)
    {
        decimal?[] scores = gridResults
            .Select(point => point.IsValidCandidate && point.PassedEligibilityGates ? point.Score : null)
            .ToArray();
        var diffs = new List<decimal>();
        decimal? previous = null;
        foreach (decimal? score in scores)
        {
            if (score is null)
                continue;
            if (previous is not null)
                diffs.Add(score.Value - previous.Value);
            previous = score;
        }

        int changes = 0;
        for (int i = 1; i < diffs.Count; i++)
        {
            if (diffs[i] == 0m || diffs[i - 1] == 0m)
                continue;
            if (Math.Sign(diffs[i]) != Math.Sign(diffs[i - 1]))
                changes++;
        }

        return changes;
    }

    /// <summary>
    /// Blueprint §9.2: "Runtime ordering may place the most influential parameters first. The
    /// final order must be deterministic, with manifest order as the fallback." Ties (equal
    /// influence, including two Negligible/Unstable/InsufficientEvidence parameters both excluded
    /// from descent) fall back to the manifest's declared order, then parameter id.
    /// </summary>
    public static IReadOnlyList<string> OrderByInfluence<TOptions>(
        IReadOnlyList<ParameterSensitivityResult> results, IIndicatorCalibrationManifest<TOptions> manifest)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(manifest);
        Dictionary<string, int> declaredOrder = manifest.Parameters
            .ToDictionary(parameter => parameter.ParameterId, parameter => parameter.DeclaredSearchOrder, StringComparer.Ordinal);
        return results
            .Where(result => result.Classification == ParameterSensitivityClassification.Influential)
            .OrderByDescending(result => result.InfluenceScore)
            .ThenBy(result => declaredOrder.GetValueOrDefault(result.ParameterId, int.MaxValue))
            .ThenBy(result => result.ParameterId, StringComparer.Ordinal)
            .Select(result => result.ParameterId)
            .ToArray();
    }
}
