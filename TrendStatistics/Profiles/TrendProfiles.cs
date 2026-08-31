using TrendStatistics.Detection;
using TrendStatistics.Segmentation;
using TrendStatistics.Statistics;

namespace TrendStatistics.Profiles;

/// <summary>
/// Blueprint section 21's per-direction profile: the empirical price and duration distributions of
/// historical trends in one direction for one symbol.
/// </summary>
public sealed record DirectionTrendProfile
{
    public required string Symbol { get; init; }
    public required TrendDirection Direction { get; init; }
    public required int SampleCount { get; init; }

    /// <summary>Ascending total-move percentages of the trends this profile was built from.</summary>
    public required IReadOnlyList<decimal> SortedMovePct { get; init; }

    /// <summary>Ascending durations in bars.</summary>
    public required IReadOnlyList<decimal> SortedDurationBars { get; init; }

    /// <summary>
    /// Move and duration from the same historical trend. Kept paired for joint-distribution
    /// estimates; independently sorted marginals would manufacture perfect correlation.
    /// </summary>
    public required IReadOnlyList<(decimal MovePct, decimal DurationBars)> MoveDurationPairs { get; init; }

    /// <summary>
    /// Ascending maximum-adverse-excursion percentages, measured from each trend's confirmation
    /// price. This is what a stop placed at entry actually had to survive.
    /// </summary>
    public required IReadOnlyList<decimal> SortedAdverseExcursionPct { get; init; }

    /// <summary>The most recent trend end included, so a caller can prove no future leaked in.</summary>
    public required DateTimeOffset BuiltThrough { get; init; }

    /// <summary>Independent quarterly blocks available to the uncertainty estimate.</summary>
    public required int IndependentBlockCount { get; init; }

    /// <summary>
    /// Composite 0..1 confidence based on sample size, time coverage, and block-bootstrap
    /// stability of the median price and duration quantiles.
    /// </summary>
    public required decimal ReliabilityScore { get; init; }

    public decimal PriceQuantile(decimal q) => QuantileCalculator.Quantile(SortedMovePct, q);
    public decimal TimeQuantile(decimal q) => QuantileCalculator.Quantile(SortedDurationBars, q);

    /// <summary>
    /// Section 37's stop, derived from this direction's own adverse-excursion history rather than
    /// from fixed reward/risk geometry.
    /// <para>
    /// A quantile is used in preference to a fitted model on purpose. The adverse-excursion
    /// distribution is tight and well estimated (375 XAU bull trends give P90 1.80% with a stable
    /// bootstrap interval), whereas the tradeable sample at the P10 entry threshold is ~107 trades -
    /// far too few to fit a per-trade predictor that could beat simply reading this distribution.
    /// </para>
    /// </summary>
    public decimal AdverseExcursionQuantile(decimal q) =>
        QuantileCalculator.Quantile(SortedAdverseExcursionPct, q);

    /// <summary>
    /// Section 22: a profile built from too few trends produces quantiles that move wildly with one
    /// more sample. Callers should refuse to trade a profile that reports false here.
    /// </summary>
    public bool IsReliable(int minimumSamples) =>
        SampleCount >= minimumSamples
        && IndependentBlockCount >= 4
        && ReliabilityScore >= 0.45m;

    public static DirectionTrendProfile Build(
        string symbol,
        TrendDirection direction,
        IReadOnlyList<TrendRecord> trends)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentNullException.ThrowIfNull(trends);

        TrendRecord? wrongSymbol = trends.FirstOrDefault(
            trend => !string.Equals(trend.Symbol, symbol, StringComparison.Ordinal));
        if (wrongSymbol is not null)
        {
            throw new ArgumentException(
                $"Trend symbol '{wrongSymbol.Symbol}' cannot be included in profile '{symbol}'.",
                nameof(trends));
        }

        TrendRecord[] side = [.. trends
            .Where(trend => trend.Direction == direction)
            .OrderBy(trend => trend.EndTime)];
        (int blockCount, decimal reliability) = AssessReliability(side);
        return new DirectionTrendProfile
        {
            Symbol = symbol,
            Direction = direction,
            SampleCount = side.Length,
            // Absolute value: a bear trend's move is stored signed by some producers and unsigned by
            // others, and a profile mixing the two would rank every live trend at percentile 1.
            SortedMovePct = QuantileCalculator.Sorted(side.Select(trend => Math.Abs(trend.TotalMovePct))),
            SortedDurationBars = QuantileCalculator.Sorted(side.Select(trend => (decimal)trend.DurationBars)),
            MoveDurationPairs = [.. side.Select(trend =>
                (Math.Abs(trend.TotalMovePct), (decimal)trend.DurationBars))],
            SortedAdverseExcursionPct = QuantileCalculator.Sorted(
                side.Select(trend => trend.MaximumAdverseExcursionPct)),
            BuiltThrough = side.Length == 0 ? DateTimeOffset.MinValue : side[^1].EndTime,
            IndependentBlockCount = blockCount,
            ReliabilityScore = reliability
        };
    }

    private static (int BlockCount, decimal Score) AssessReliability(IReadOnlyList<TrendRecord> side)
    {
        if (side.Count == 0)
            return (0, 0m);

        TimeSpan blockSpan = TimeSpan.FromDays(90);
        BootstrapEngine engine = new([0.50m], confidenceLevel: 0.90m, seed: 20260830);
        BootstrapResult move = engine.BuildBlockDistribution(
            [.. side.Select(trend => new TimedObservation(trend.EndTime, Math.Abs(trend.TotalMovePct)))],
            blockSpan,
            iterations: 300);
        BootstrapResult duration = engine.BuildBlockDistribution(
            [.. side.Select(trend => new TimedObservation(trend.EndTime, trend.DurationBars))],
            blockSpan,
            iterations: 300);

        decimal sampleScore = Math.Min(1m, side.Count / 100m);
        decimal blockScore = Math.Min(1m, move.ResamplingUnitCount / 8m);
        decimal moveScore = StabilityScore(move);
        decimal durationScore = StabilityScore(duration);
        return (move.ResamplingUnitCount,
            (sampleScore + blockScore + moveScore + durationScore) / 4m);
    }

    private static decimal StabilityScore(BootstrapResult result)
    {
        if (result.ResamplingUnitCount < 2)
            return 0m;
        decimal width = result.For(0.50m).RelativeWidth;
        return width == decimal.MaxValue ? 0m : 1m / (1m + width);
    }
}

/// <summary>Section 21's symbol-level container: one profile per direction, never pooled.</summary>
public sealed record SymbolTrendProfile
{
    public required string Symbol { get; init; }
    public required DirectionTrendProfile Bull { get; init; }
    public required DirectionTrendProfile Bear { get; init; }

    public DirectionTrendProfile For(TrendDirection direction) =>
        direction == TrendDirection.Bullish ? Bull : Bear;

    /// <summary>
    /// Builds both direction profiles from completed trends.
    /// <para>
    /// Section 5 forbids pooling bull and bear trends, and the measured data justifies it: on 3.5
    /// years of XAU/USD the bull median move and duration are materially larger than the bear ones,
    /// so a pooled profile would rank every live trend against a distribution belonging to neither.
    /// </para>
    /// <para>
    /// Section 49's leakage rule is the caller's responsibility: pass only trends that ENDED before
    /// the moment being evaluated. <see cref="BuildAsOf"/> enforces that.
    /// </para>
    /// </summary>
    public static SymbolTrendProfile Build(string symbol, IReadOnlyList<TrendRecord> trends) => new()
    {
        Symbol = symbol,
        Bull = DirectionTrendProfile.Build(symbol, TrendDirection.Bullish, trends),
        Bear = DirectionTrendProfile.Build(symbol, TrendDirection.Bearish, trends)
    };

    /// <summary>
    /// Section 49: builds from trends that had already ENDED at <paramref name="asOf"/>, so a
    /// profile can never be informed by a trend that was still running - or had not started - at
    /// the moment it is used to judge.
    /// </summary>
    public static SymbolTrendProfile BuildAsOf(
        string symbol,
        IReadOnlyList<TrendRecord> trends,
        DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(trends);
        return Build(symbol, [.. trends.Where(trend => trend.EndTime <= asOf)]);
    }
}
