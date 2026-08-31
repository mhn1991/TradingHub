namespace TrendStatistics.Statistics;

/// <summary>A value with its observation time, used for dependence-aware block resampling.</summary>
public readonly record struct TimedObservation(DateTimeOffset Time, decimal Value);

/// <summary>One bootstrapped quantile: the point estimate and its confidence interval.</summary>
public readonly record struct QuantileEstimate(
    decimal Quantile,
    decimal PointEstimate,
    decimal LowerBound,
    decimal UpperBound)
{
    public decimal IntervalWidth => UpperBound - LowerBound;

    /// <summary>
    /// Interval width relative to the estimate. Section 17's worked example contrasts a P95 of
    /// 11.7% with CI 10.9-12.8 (width 0.16 of the estimate, stable) against 7.1-18.5 (width 0.97,
    /// poorly estimated). This is that ratio.
    /// </summary>
    public decimal RelativeWidth => PointEstimate == 0m ? decimal.MaxValue : IntervalWidth / Math.Abs(PointEstimate);

    /// <summary>Section 17: a quantile whose interval spans most of its own value is not usable.</summary>
    public bool IsStable(decimal maximumRelativeWidth = 0.5m) => RelativeWidth <= maximumRelativeWidth;
}

public sealed record BootstrapResult
{
    public required int Iterations { get; init; }
    public required int SampleSize { get; init; }
    public required int Seed { get; init; }
    public required IReadOnlyList<QuantileEstimate> Estimates { get; init; }

    /// <summary>Number of independently resampled units: observations for IID, time blocks otherwise.</summary>
    public int ResamplingUnitCount { get; init; }

    public TimeSpan? BlockSpan { get; init; }

    public QuantileEstimate For(decimal quantile)
    {
        QuantileEstimate estimate = Estimates.FirstOrDefault(candidate => candidate.Quantile == quantile);
        return estimate.Quantile == quantile
            ? estimate
            : throw new KeyNotFoundException($"Quantile {quantile} was not requested from this bootstrap.");
    }

    public string ToText()
    {
        System.Text.StringBuilder builder = new();
        string unit = BlockSpan is null
            ? $"observations={ResamplingUnitCount}"
            : $"blocks={ResamplingUnitCount} span={BlockSpan}";
        builder.AppendLine($"  bootstrap n={SampleSize} {unit} iterations={Iterations} seed={Seed}");
        builder.AppendLine($"    {"q",6}{"estimate",11}{"CI low",11}{"CI high",11}{"rel width",11}  stable");
        foreach (QuantileEstimate estimate in Estimates)
        {
            builder.AppendLine($"    {estimate.Quantile,6:F2}{estimate.PointEstimate,11:F3}" +
                $"{estimate.LowerBound,11:F3}{estimate.UpperBound,11:F3}{estimate.RelativeWidth,11:F3}" +
                $"  {(estimate.IsStable() ? "yes" : "NO")}");
        }
        return builder.ToString();
    }
}

public interface IBootstrapEngine
{
    BootstrapResult BuildDistribution(IReadOnlyList<decimal> sample, int iterations);
    BootstrapResult BuildBlockDistribution(
        IReadOnlyList<TimedObservation> sample,
        TimeSpan blockSpan,
        int iterations);
}

/// <summary>
/// Blueprint sections 16 to 20: resample the historical trends with replacement and report a
/// confidence interval around each estimated quantile.
/// <para>
/// Section 18 is emphatic that these are two different things. The empirical quantile says where
/// historical trends fell; the bootstrap interval says how well that quantile is itself known. A
/// P95 of 11.7% with a 10.9-12.8 interval is a usable exhaustion threshold; the same 11.7% with a
/// 7.1-18.5 interval is noise wearing a number, and the entry/exit logic must not act on it.
/// </para>
/// <para>
/// Section 20 requires reproducibility, so the seed is explicit and stored on the result rather
/// than defaulted from the clock.
/// </para>
/// </summary>
public sealed class BootstrapEngine(
    IReadOnlyList<decimal>? quantiles = null,
    decimal confidenceLevel = 0.90m,
    int seed = 20260830) : IBootstrapEngine
{
    private readonly IReadOnlyList<decimal> _quantiles = quantiles ?? QuantileCalculator.DefaultQuantiles;
    private readonly decimal _confidence = confidenceLevel is > 0m and < 1m
        ? confidenceLevel
        : throw new ArgumentOutOfRangeException(nameof(confidenceLevel));
    private readonly int _seed = seed;

    public BootstrapResult BuildDistribution(IReadOnlyList<decimal> sample, int iterations = 10_000)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (sample.Count == 0)
            throw new ArgumentException("Cannot bootstrap an empty sample.", nameof(sample));
        if (iterations <= 0)
            throw new ArgumentOutOfRangeException(nameof(iterations));

        decimal[] original = QuantileCalculator.Sorted(sample);
        // One row per quantile, one column per iteration.
        decimal[][] draws = [.. _quantiles.Select(_ => new decimal[iterations])];

        Random random = new(_seed);
        decimal[] resample = new decimal[sample.Count];

        for (int iteration = 0; iteration < iterations; iteration++)
        {
            // Section 16: sample N trends WITH replacement, where N is the original sample size.
            // Drawing fewer would understate the uncertainty of the real estimate.
            for (int index = 0; index < resample.Length; index++)
                resample[index] = sample[random.Next(sample.Count)];
            Array.Sort(resample);

            for (int q = 0; q < _quantiles.Count; q++)
                draws[q][iteration] = QuantileCalculator.Quantile(resample, _quantiles[q]);
        }

        decimal tail = (1m - _confidence) / 2m;
        List<QuantileEstimate> estimates = [];
        for (int q = 0; q < _quantiles.Count; q++)
        {
            decimal[] sorted = QuantileCalculator.Sorted(draws[q]);
            estimates.Add(new QuantileEstimate(
                _quantiles[q],
                QuantileCalculator.Quantile(original, _quantiles[q]),
                QuantileCalculator.Quantile(sorted, tail),
                QuantileCalculator.Quantile(sorted, 1m - tail)));
        }

        return new BootstrapResult
        {
            Iterations = iterations,
            SampleSize = sample.Count,
            Seed = _seed,
            ResamplingUnitCount = sample.Count,
            Estimates = estimates
        };
    }

    /// <summary>
    /// Resamples fixed calendar-time blocks rather than pretending nearby trends are independent.
    /// All observations in a selected block travel together, preserving clustered market regimes.
    /// </summary>
    public BootstrapResult BuildBlockDistribution(
        IReadOnlyList<TimedObservation> sample,
        TimeSpan blockSpan,
        int iterations = 10_000)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (sample.Count == 0)
            throw new ArgumentException("Cannot bootstrap an empty sample.", nameof(sample));
        if (blockSpan <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(blockSpan));
        if (iterations <= 0)
            throw new ArgumentOutOfRangeException(nameof(iterations));

        decimal[][] blocks = [.. sample
            .OrderBy(observation => observation.Time)
            .GroupBy(observation => observation.Time.UtcTicks / blockSpan.Ticks)
            .Select(group => group.Select(observation => observation.Value).ToArray())];
        decimal[] original = QuantileCalculator.Sorted(sample.Select(observation => observation.Value));
        decimal[][] draws = [.. _quantiles.Select(_ => new decimal[iterations])];
        Random random = new(_seed);

        for (int iteration = 0; iteration < iterations; iteration++)
        {
            List<decimal> resample = new(sample.Count);
            for (int block = 0; block < blocks.Length; block++)
                resample.AddRange(blocks[random.Next(blocks.Length)]);
            decimal[] sortedResample = QuantileCalculator.Sorted(resample);
            for (int q = 0; q < _quantiles.Count; q++)
                draws[q][iteration] = QuantileCalculator.Quantile(sortedResample, _quantiles[q]);
        }

        decimal tail = (1m - _confidence) / 2m;
        List<QuantileEstimate> estimates = [];
        for (int q = 0; q < _quantiles.Count; q++)
        {
            decimal[] sortedDraws = QuantileCalculator.Sorted(draws[q]);
            estimates.Add(new QuantileEstimate(
                _quantiles[q],
                QuantileCalculator.Quantile(original, _quantiles[q]),
                QuantileCalculator.Quantile(sortedDraws, tail),
                QuantileCalculator.Quantile(sortedDraws, 1m - tail)));
        }

        return new BootstrapResult
        {
            Iterations = iterations,
            SampleSize = sample.Count,
            Seed = _seed,
            ResamplingUnitCount = blocks.Length,
            BlockSpan = blockSpan,
            Estimates = estimates
        };
    }

    /// <summary>
    /// Section 19's convergence test: run at several iteration counts and report how much the
    /// estimates still move. Section 19 explicitly says not to assume 10,000 is either necessary
    /// or sufficient.
    /// </summary>
    public IReadOnlyList<(int Iterations, BootstrapResult Result)> ConvergenceTest(
        IReadOnlyList<decimal> sample,
        IReadOnlyList<int>? iterationCounts = null)
    {
        iterationCounts ??= [1_000, 5_000, 10_000, 20_000];
        return [.. iterationCounts.Select(count => (count, BuildDistribution(sample, count)))];
    }
}
