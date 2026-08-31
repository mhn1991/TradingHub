namespace TrendStatistics.Statistics;

/// <summary>
/// Empirical quantiles and their inverse, blueprint section 15.
/// <para>
/// Deliberately non-parametric. Section 66 rules out mean +/- standard deviation as the main model,
/// and the trend distributions measured on real data are strongly right-skewed - a normal
/// approximation would put the exhaustion zone in the wrong place in both tails.
/// </para>
/// </summary>
public static class QuantileCalculator
{
    /// <summary>The blueprint section 50 quantile grid.</summary>
    public static IReadOnlyList<decimal> DefaultQuantiles { get; } =
        [0.05m, 0.10m, 0.25m, 0.50m, 0.75m, 0.90m, 0.95m];

    /// <summary>
    /// Linear-interpolated empirical quantile. <paramref name="sorted"/> must be ascending.
    /// </summary>
    public static decimal Quantile(IReadOnlyList<decimal> sorted, decimal q)
    {
        ArgumentNullException.ThrowIfNull(sorted);
        if (sorted.Count == 0)
            throw new ArgumentException("Cannot take a quantile of an empty sample.", nameof(sorted));
        if (q is < 0m or > 1m)
            throw new ArgumentOutOfRangeException(nameof(q), "Quantile must be within [0, 1].");
        if (sorted.Count == 1)
            return sorted[0];

        decimal position = q * (sorted.Count - 1);
        int lower = (int)Math.Floor(position);
        int upper = Math.Min(lower + 1, sorted.Count - 1);
        return sorted[lower] + ((position - lower) * (sorted[upper] - sorted[lower]));
    }

    /// <summary>
    /// The inverse: what fraction of the sample does <paramref name="value"/> exceed. This is the
    /// quantity sections 23 and 24 call the price and time percentile.
    /// <para>
    /// Uses the midpoint of ties, so a value equal to several samples is not credited with beating
    /// all of them. A value beyond the largest sample returns 1 - section 33 is explicit that
    /// exceeding P95 is expected for tail trends and must not be treated as impossible.
    /// </para>
    /// </summary>
    public static decimal PercentileOf(IReadOnlyList<decimal> sorted, decimal value)
    {
        ArgumentNullException.ThrowIfNull(sorted);
        if (sorted.Count == 0)
            throw new ArgumentException("Cannot rank against an empty sample.", nameof(sorted));

        int below = 0;
        int equal = 0;
        foreach (decimal sample in sorted)
        {
            if (sample < value) below++;
            else if (sample == value) equal++;
        }

        return (below + (equal / 2m)) / sorted.Count;
    }

    public static decimal[] Sorted(IEnumerable<decimal> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        decimal[] copy = [.. values];
        Array.Sort(copy);
        return copy;
    }
}
