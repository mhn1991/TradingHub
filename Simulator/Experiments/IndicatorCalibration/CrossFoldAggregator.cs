using Simulator.Calibration;

namespace Simulator.Experiments.IndicatorCalibration;

public sealed record ParameterAggregationResult
{
    public required string ParameterId { get; init; }
    public required decimal AggregatedValue { get; init; }
    public required decimal DefaultValue { get; init; }
    public required decimal FoldSupportPercent { get; init; }
    public required decimal PlateauWidth { get; init; }
    public required bool UsedExistingValueFallback { get; init; }
    public required string Reason { get; init; }
}

public sealed record CrossFoldAggregationResult
{
    public required CalibrationCandidate AggregatedCandidate { get; init; }
    public required IReadOnlyList<ParameterAggregationResult> ParameterResults { get; init; }
    public required int TotalFolds { get; init; }
    public required int ContributingFolds { get; init; }
    public required int AcceptableFoldCount { get; init; }
    public required decimal AcceptableFoldPercent { get; init; }
    public required bool IsStable { get; init; }
    public required string Reason { get; init; }
}

/// <summary>
/// Phase 7 of the search algorithm (blueprint §9.7). Never takes "the last fold's winner" and
/// never blindly averages: each parameter's per-fold values are clustered into plateaus (values
/// within 2x the descriptor's refinement step of one another), the best-supported plateau wins,
/// and the aggregated value is the actual observed fold value closest to that plateau's mean -
/// never a synthetic never-evaluated number. A parameter whose best plateau doesn't reach
/// <see cref="CalibrationAcceptancePolicy.MinimumPlateauSupport"/>, or that has no contributing
/// fold evidence at all, falls back to its manifest default rather than guessing.
/// </summary>
public static class CrossFoldAggregator
{
    /// <summary>Fold states whose selected candidate is trustworthy evidence for aggregation - a fold that failed data quality/evidence/quality gates contributes nothing.</summary>
    private static readonly HashSet<FoldResultState> ContributingStates = [FoldResultState.Improved, FoldResultState.NoImprovement];

    public static CrossFoldAggregationResult Aggregate<TOptions>(
        IReadOnlyList<FoldCandidateSelectionResult> foldResults,
        IIndicatorCalibrationManifest<TOptions> manifest,
        CalibrationAcceptancePolicy acceptancePolicy)
    {
        ArgumentNullException.ThrowIfNull(foldResults);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(acceptancePolicy);
        if (foldResults.Count == 0)
            throw new ArgumentException("At least one fold result is required.", nameof(foldResults));

        FoldCandidateSelectionResult[] contributing = foldResults
            .Where(fold => ContributingStates.Contains(fold.State))
            .ToArray();
        int acceptableCount = foldResults.Count(fold => fold.State == FoldResultState.Improved);
        decimal acceptableFoldPercent = 100m * acceptableCount / foldResults.Count;

        var parameterResults = new List<ParameterAggregationResult>();
        var aggregatedNumeric = new Dictionary<string, decimal>(StringComparer.Ordinal);
        bool anyUnstableFallback = false;

        foreach (ICalibrationParameterDescriptor<TOptions> parameter in manifest.Parameters
                     .OrderBy(item => item.DeclaredSearchOrder)
                     .ThenBy(item => item.ParameterId, StringComparer.Ordinal))
        {
            decimal[] values = contributing
                .Where(fold => fold.Candidate.NumericValues.ContainsKey(parameter.ParameterId))
                .Select(fold => fold.Candidate.NumericValues[parameter.ParameterId])
                .OrderBy(value => value)
                .ToArray();

            if (values.Length == 0)
            {
                aggregatedNumeric[parameter.ParameterId] = parameter.DefaultValue;
                parameterResults.Add(new ParameterAggregationResult
                {
                    ParameterId = parameter.ParameterId,
                    AggregatedValue = parameter.DefaultValue,
                    DefaultValue = parameter.DefaultValue,
                    FoldSupportPercent = 0m,
                    PlateauWidth = 0m,
                    UsedExistingValueFallback = true,
                    Reason = "No contributing fold produced a value for this parameter."
                });
                anyUnstableFallback = true;
                continue;
            }

            IReadOnlyList<decimal[]> clusters = ClusterByPlateau(values, parameter.RefinementStep * 2m);
            decimal[] bestCluster = clusters
                .OrderByDescending(cluster => cluster.Length)
                .ThenBy(cluster => Math.Abs(cluster.Average() - parameter.DefaultValue))
                .First();
            decimal supportPercent = 100m * bestCluster.Length / contributing.Length;

            if (supportPercent < acceptancePolicy.MinimumPlateauSupport)
            {
                aggregatedNumeric[parameter.ParameterId] = parameter.DefaultValue;
                parameterResults.Add(new ParameterAggregationResult
                {
                    ParameterId = parameter.ParameterId,
                    AggregatedValue = parameter.DefaultValue,
                    DefaultValue = parameter.DefaultValue,
                    FoldSupportPercent = supportPercent,
                    PlateauWidth = bestCluster.Length > 0 ? bestCluster[^1] - bestCluster[0] : 0m,
                    UsedExistingValueFallback = true,
                    Reason = $"Best plateau support {supportPercent:F1}% is below the minimum {acceptancePolicy.MinimumPlateauSupport:F1}% - folds disagree materially."
                });
                anyUnstableFallback = true;
                continue;
            }

            decimal mean = bestCluster.Average();
            decimal aggregatedValue = bestCluster.OrderBy(value => Math.Abs(value - mean)).First();
            aggregatedNumeric[parameter.ParameterId] = aggregatedValue;
            parameterResults.Add(new ParameterAggregationResult
            {
                ParameterId = parameter.ParameterId,
                AggregatedValue = aggregatedValue,
                DefaultValue = parameter.DefaultValue,
                FoldSupportPercent = supportPercent,
                PlateauWidth = bestCluster[^1] - bestCluster[0],
                UsedExistingValueFallback = false,
                Reason = $"{bestCluster.Length} of {contributing.Length} contributing folds ({supportPercent:F1}%) support a stable plateau around {aggregatedValue}."
            });
        }

        var aggregatedAblations = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (ICalibrationAblationDescriptor<TOptions> ablation in manifest.Ablations)
        {
            // Ablation aggregation: majority vote among contributing folds, default on a tie.
            bool[] votes = contributing
                .Where(fold => fold.Candidate.AblationValues.ContainsKey(ablation.ParameterId))
                .Select(fold => fold.Candidate.AblationValues[ablation.ParameterId])
                .ToArray();
            if (votes.Length == 0)
            {
                aggregatedAblations[ablation.ParameterId] = ablation.DefaultValue;
                continue;
            }
            int trueVotes = votes.Count(vote => vote);
            int falseVotes = votes.Length - trueVotes;
            aggregatedAblations[ablation.ParameterId] = trueVotes > falseVotes
                ? true
                : falseVotes > trueVotes ? false : ablation.DefaultValue;
        }

        var aggregatedCandidate = new CalibrationCandidate
        {
            NumericValues = aggregatedNumeric,
            AblationValues = aggregatedAblations
        };

        return new CrossFoldAggregationResult
        {
            AggregatedCandidate = aggregatedCandidate,
            ParameterResults = parameterResults,
            TotalFolds = foldResults.Count,
            ContributingFolds = contributing.Length,
            AcceptableFoldCount = acceptableCount,
            AcceptableFoldPercent = acceptableFoldPercent,
            IsStable = !anyUnstableFallback,
            Reason = anyUnstableFallback
                ? "One or more parameters fell back to their default value due to weak or disagreeing fold evidence."
                : "Every parameter reached a stable, well-supported plateau across contributing folds."
        };
    }

    /// <summary>Sorted values are grouped so each cluster member is within <paramref name="tolerance"/> of the previous member of its own cluster (chained, not pairwise-to-first).</summary>
    private static IReadOnlyList<decimal[]> ClusterByPlateau(IReadOnlyList<decimal> sortedValues, decimal tolerance)
    {
        var clusters = new List<List<decimal>> { new() { sortedValues[0] } };
        for (int i = 1; i < sortedValues.Count; i++)
        {
            List<decimal> current = clusters[^1];
            if (sortedValues[i] - current[^1] <= tolerance)
                current.Add(sortedValues[i]);
            else
                clusters.Add([sortedValues[i]]);
        }
        return clusters.Select(cluster => cluster.ToArray()).ToArray();
    }
}
