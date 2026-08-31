namespace TrendStatistics.Statistics;

/// <summary>
/// Blueprint sections 26 and 27: how unusual a (price, time) pair is jointly, rather than on each
/// axis alone.
/// <para>
/// Section 34 is the reason this cannot be a simple average. Price 0.67 / time 0.97 means a trend
/// that has run out of time but not distance; price 0.96 / time 0.38 means one that covered an
/// unusual distance unusually fast. Both are exhaustion signals, and both would be flattened to an
/// unremarkable ~0.8 and ~0.67 by averaging.
/// </para>
/// </summary>
public sealed class JointDistribution
{
    private readonly (decimal Price, decimal Time)[] _observations;

    public JointDistribution(IEnumerable<(decimal Price, decimal Time)> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        _observations = [.. observations];
        if (_observations.Length == 0)
            throw new ArgumentException("A joint distribution needs at least one observation.", nameof(observations));
    }

    public int SampleCount => _observations.Length;

    /// <summary>
    /// Section 27's joint rarity: the fraction of historical trends that reached at least this far
    /// on BOTH axes. A low value means few comparable trends got this far in both dimensions.
    /// </summary>
    public decimal JointExceedance(decimal price, decimal time)
    {
        int atLeast = _observations.Count(o => o.Price >= price && o.Time >= time);
        return (decimal)atLeast / _observations.Length;
    }

    /// <summary>
    /// Rarity as an exhaustion score in [0, 1], where 1 means no historical trend went this far in
    /// both dimensions.
    /// </summary>
    public decimal Rarity(decimal price, decimal time) => 1m - JointExceedance(price, time);

    /// <summary>
    /// Section 34's asymmetry: how lopsided the two percentiles are. A trend far along on one axis
    /// and not the other is a distinct condition from one advanced on both, and the raw difference
    /// is retained rather than folded into the score - section 35 says the score must not hide
    /// information.
    /// </summary>
    public static decimal Imbalance(decimal price, decimal time) => Math.Abs(price - time);

    public static JointDistribution FromTrends(
        IReadOnlyList<decimal> movePct,
        IReadOnlyList<decimal> durationBars)
    {
        ArgumentNullException.ThrowIfNull(movePct);
        ArgumentNullException.ThrowIfNull(durationBars);
        if (movePct.Count != durationBars.Count)
            throw new ArgumentException("Move and duration samples must be paired.", nameof(durationBars));

        decimal[] sortedMove = QuantileCalculator.Sorted(movePct);
        decimal[] sortedTime = QuantileCalculator.Sorted(durationBars);

        // Each historical trend is converted to its own (price percentile, time percentile) pair,
        // so the joint distribution lives in percentile space and is comparable across symbols.
        return new JointDistribution(Enumerable.Range(0, movePct.Count).Select(index => (
            QuantileCalculator.PercentileOf(sortedMove, movePct[index]),
            QuantileCalculator.PercentileOf(sortedTime, durationBars[index]))));
    }
}
