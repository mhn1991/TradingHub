using TradingClassifier.Features;

namespace TradingClassifier.Dataset;

/// <summary>
/// Measures how much each training row's label window is shared with its neighbours.
/// <para>
/// A row at <c>t</c> is labelled from candles <c>t+1 .. t+horizon</c>, and the row at <c>t+1</c>
/// from <c>t+2 .. t+horizon+1</c>. With one row per candle, consecutive labels share
/// <c>horizon-1</c> of their <c>horizon</c> bars. The model is therefore shown many near-duplicate
/// examples, which inflates the effective sample size and lets it fit the same market episode
/// repeatedly without that showing up as overfitting during training.
/// </para>
/// </summary>
public static class SampleUniqueness
{
    /// <summary>
    /// Per-row uniqueness in (0, 1]: the mean of <c>1/concurrency</c> across the bars the row's
    /// label spans, where concurrency counts how many labels cover that bar.
    /// <para>
    /// Note what this returns on a contiguous dataset: every interior row scores about
    /// <c>1/horizon</c>, identically. Uniqueness weighting is therefore <b>not</b> the fix for
    /// uniformly-spaced rows - it is uniform and cancels out. It becomes meaningful only when
    /// sampling is uneven (event-based rows, gaps, mixed horizons). The fix for the uniform case is
    /// <see cref="Stride"/>.
    /// </para>
    /// </summary>
    public static double[] Compute(IReadOnlyList<LabeledFeatureRow> rows, int horizon)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (horizon <= 0)
            throw new ArgumentOutOfRangeException(nameof(horizon));

        int count = rows.Count;
        int[] concurrency = new int[count + horizon + 1];
        for (int index = 0; index < count; index++)
        {
            for (int offset = 1; offset <= horizon && index + offset < concurrency.Length; offset++)
                concurrency[index + offset]++;
        }

        double[] uniqueness = new double[count];
        for (int index = 0; index < count; index++)
        {
            double total = 0;
            int spans = 0;
            for (int offset = 1; offset <= horizon && index + offset < concurrency.Length; offset++)
            {
                int overlapping = concurrency[index + offset];
                if (overlapping <= 0)
                    continue;
                total += 1d / overlapping;
                spans++;
            }
            uniqueness[index] = spans == 0 ? 1d : total / spans;
        }

        return uniqueness;
    }

    /// <summary>
    /// Selects every <paramref name="stride"/>-th row, so that with
    /// <c>stride == horizon</c> no two selected labels share a candle.
    /// <para>
    /// This is the effective remedy for the uniform case. It trades sample count for independence:
    /// at horizon 10 it keeps a tenth of the rows, but those rows are genuinely distinct
    /// observations rather than ten views of the same ten candles.
    /// </para>
    /// </summary>
    public static IReadOnlyList<LabeledFeatureRow> Stride(IReadOnlyList<LabeledFeatureRow> rows, int stride)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (stride <= 1)
            return rows;

        List<LabeledFeatureRow> kept = new((rows.Count / stride) + 1);
        for (int index = 0; index < rows.Count; index += stride)
            kept.Add(rows[index]);
        return kept;
    }

    /// <summary>
    /// Effective sample size: the sum of per-row uniqueness. On a contiguous dataset with horizon
    /// <c>h</c> this lands near <c>rows / h</c>, which is the number to quote when describing how
    /// much independent data a model actually saw.
    /// </summary>
    public static double EffectiveSampleSize(IReadOnlyList<LabeledFeatureRow> rows, int horizon) =>
        Compute(rows, horizon).Sum();
}
