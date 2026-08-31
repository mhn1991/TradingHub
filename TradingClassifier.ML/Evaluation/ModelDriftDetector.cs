namespace TradingClassifier.ML.Evaluation;

public sealed record DriftReport
{
    public required string Name { get; init; }
    public required double PopulationStabilityIndex { get; init; }
    public required int ReferenceCount { get; init; }
    public required int CurrentCount { get; init; }

    /// <summary>
    /// Conventional PSI reading: &lt;0.10 stable, 0.10-0.25 moderate shift, &gt;0.25 significant.
    /// </summary>
    public string Verdict => PopulationStabilityIndex switch
    {
        < 0.10 => "stable",
        < 0.25 => "moderate shift",
        _ => "significant shift"
    };

    public bool RequiresAttention => PopulationStabilityIndex >= 0.25;
}

/// <summary>
/// Population Stability Index between a reference distribution and a current one — the ML
/// catalogue's Phase 7 "drift detection".
/// <para>
/// Answers a question that is <b>independent of whether the model has an edge</b>: are the inputs
/// (or outputs) still drawn from the distribution the model was fitted on? That is why it is worth
/// building now even though nothing here has validated — a model with no edge and a model whose
/// inputs have shifted are different failures, and PSI separates them.
/// </para>
/// <para>
/// Directly relevant to a defect this repo already measured: §3.12c found the feature that destroyed
/// walk-forward performance was an unbounded level with 48% of a test window falling outside the
/// entire training range. PSI is exactly the instrument that would have flagged it before the model
/// was trusted.
/// </para>
/// </summary>
public static class ModelDriftDetector
{
    public static DriftReport Compare(
        string name,
        IReadOnlyList<double> reference,
        IReadOnlyList<double> current,
        int bins = 10)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(current);

        if (reference.Count == 0 || current.Count == 0)
        {
            return new DriftReport
            {
                Name = name,
                PopulationStabilityIndex = 0,
                ReferenceCount = reference.Count,
                CurrentCount = current.Count
            };
        }

        // Quantile edges from the REFERENCE distribution: bins must be fixed by what the model was
        // trained on, otherwise a shifted population silently redefines its own baseline and PSI
        // reports zero drift no matter how far it has moved.
        double[] sorted = [.. reference.OrderBy(value => value)];
        double[] edges = new double[bins - 1];
        for (int index = 1; index < bins; index++)
            edges[index - 1] = sorted[(int)((long)index * sorted.Length / bins)];

        int[] referenceCounts = Bucket(reference, edges);
        int[] currentCounts = Bucket(current, edges);

        double psi = 0;
        for (int index = 0; index < bins; index++)
        {
            // Floor the shares so an empty bucket cannot drive the log to infinity.
            double referenceShare = Math.Max((double)referenceCounts[index] / reference.Count, 1e-6);
            double currentShare = Math.Max((double)currentCounts[index] / current.Count, 1e-6);
            psi += (currentShare - referenceShare) * Math.Log(currentShare / referenceShare);
        }

        return new DriftReport
        {
            Name = name,
            PopulationStabilityIndex = psi,
            ReferenceCount = reference.Count,
            CurrentCount = current.Count
        };
    }

    private static int[] Bucket(IReadOnlyList<double> values, double[] edges)
    {
        int[] counts = new int[edges.Length + 1];
        foreach (double value in values)
        {
            int index = Array.BinarySearch(edges, value);
            if (index < 0)
                index = ~index;
            counts[Math.Min(index, counts.Length - 1)]++;
        }
        return counts;
    }
}
