using TrendStatistics.Detection;
using TrendStatistics.Profiles;
using TrendStatistics.Runtime;

namespace TrendStatistics.Trading;

/// <summary>Blueprint section 30's three entry experiments, run independently.</summary>
public enum SwingEntryMode
{
    /// <summary>Experiment A: price confirmation only.</summary>
    PriceOnly,
    /// <summary>Experiment B: time confirmation only.</summary>
    TimeOnly,
    /// <summary>Experiment C: both must clear.</summary>
    PriceAndTime,
    /// <summary>The "possibly later" variant section 30 mentions after A/B/C.</summary>
    PriceOrTime
}

public sealed record SwingEntryOptions
{
    /// <summary>Section 29 sweeps this over P05..P25; it must not be assumed optimal.</summary>
    public decimal EntryPercentile { get; init; } = 0.05m;

    public SwingEntryMode Mode { get; init; } = SwingEntryMode.PriceOnly;

    /// <summary>Section 22: refuse to trade a profile built from too few trends.</summary>
    public int MinimumTrendSamples { get; init; } = 20;

    /// <summary>
    /// Section 32's exhaustion zone. An entry is refused above this even when the entry threshold
    /// is cleared - a trend already past P90 is not a fresh opportunity, and without this the
    /// PriceOnly mode would happily buy the top of a P99 move.
    /// </summary>
    public decimal MaximumEntryPercentile { get; init; } = 0.75m;

    public void Validate()
    {
        if (EntryPercentile is < 0m or > 1m)
            throw new ArgumentOutOfRangeException(nameof(EntryPercentile));
        if (MaximumEntryPercentile is < 0m or > 1m)
            throw new ArgumentOutOfRangeException(nameof(MaximumEntryPercentile));
        if (MaximumEntryPercentile <= EntryPercentile)
            throw new ArgumentException("MaximumEntryPercentile must exceed EntryPercentile.");
        if (MinimumTrendSamples <= 0)
            throw new ArgumentOutOfRangeException(nameof(MinimumTrendSamples));
    }
}

public enum SwingSignalAction { None, Buy, Sell }

public readonly record struct SwingSignal
{
    public required SwingSignalAction Action { get; init; }
    public required TrendProgress Progress { get; init; }
    public required string Reason { get; init; }

    public bool IsActionable => Action != SwingSignalAction.None;
}

public interface ISwingSignalGenerator
{
    SwingSignal Evaluate(TrendState state, DirectionTrendProfile profile);
}

/// <summary>
/// Blueprint section 28: enter only once the trend is Confirmed and its progress percentile clears
/// the configured threshold.
/// <para>
/// The percentile gate is the whole point. Measured on 3.5 years of XAU/USD, entering at
/// confirmation surrenders 40% of a median bull move and 67% of a median bear move, and a naive
/// confirm-to-end hold has a 37.7% win rate with a losing median trade. Section 28 exists because
/// confirmation alone is a bad entry; this generator refuses to fire until the trend has actually
/// shown progress relative to its own history.
/// </para>
/// <para>
/// Deliberately stateless and side-effect free: it maps (state, profile) to a decision and nothing
/// else, so section 30's A/B/C experiments differ only by configuration.
/// </para>
/// </summary>
public sealed class SwingSignalGenerator : ISwingSignalGenerator
{
    private readonly SwingEntryOptions _options;
    private readonly ITrendProgressEstimator _estimator;

    public SwingSignalGenerator(SwingEntryOptions? options = null, ITrendProgressEstimator? estimator = null)
    {
        _options = options ?? new SwingEntryOptions();
        _options.Validate();
        _estimator = estimator ?? new TrendProgressEstimator(_options.MinimumTrendSamples);
    }

    public SwingSignal Evaluate(TrendState state, DirectionTrendProfile profile)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(profile);

        // Mature is still an active confirmed trend. Exhaustion is deliberately excluded, while
        // Candidate and Neutral have not established the trade thesis.
        if (state.Phase is not (TrendPhase.Confirmed or TrendPhase.Mature)
            || state.Direction is not TrendDirection direction)
            return None(default, $"Phase is {state.Phase}; entry requires Confirmed or Mature.");

        TrendProgress progress = _estimator.Estimate(state, profile);
        if (!progress.IsReliable)
        {
            return None(progress,
                $"Profile reliability is insufficient (n={profile.SampleCount}, " +
                $"blocks={profile.IndependentBlockCount}, score={profile.ReliabilityScore:F2}); " +
                "section 22 says the quantiles are not yet trustworthy.");
        }

        bool priceClears = progress.PricePercentile >= _options.EntryPercentile;
        bool timeClears = progress.TimePercentile >= _options.EntryPercentile;

        bool clears = _options.Mode switch
        {
            SwingEntryMode.PriceOnly => priceClears,
            SwingEntryMode.TimeOnly => timeClears,
            SwingEntryMode.PriceAndTime => priceClears && timeClears,
            SwingEntryMode.PriceOrTime => priceClears || timeClears,
            _ => throw new ArgumentOutOfRangeException(nameof(state))
        };

        if (!clears)
        {
            return None(progress,
                $"{_options.Mode} below entry {_options.EntryPercentile:F2} " +
                $"(price {progress.PricePercentile:F2}, time {progress.TimePercentile:F2}).");
        }

        // Section 32: past the exhaustion band this is a trend to protect profit in, not to open.
        decimal entryAxis = _options.Mode switch
        {
            SwingEntryMode.PriceOnly => progress.PricePercentile,
            SwingEntryMode.TimeOnly => progress.TimePercentile,
            _ => Math.Max(progress.PricePercentile, progress.TimePercentile)
        };
        if (entryAxis >= _options.MaximumEntryPercentile)
        {
            return None(progress,
                $"{_options.Mode} entry axis {entryAxis:F2} is at or beyond the " +
                $"{_options.MaximumEntryPercentile:F2} entry ceiling; already an exhaustion-zone trend.");
        }

        return new SwingSignal
        {
            Action = direction == TrendDirection.Bullish ? SwingSignalAction.Buy : SwingSignalAction.Sell,
            Progress = progress,
            Reason = $"Confirmed {direction}, price percentile {progress.PricePercentile:F2}, " +
                $"time percentile {progress.TimePercentile:F2}, mode {_options.Mode}, " +
                $"entry {_options.EntryPercentile:F2}."
        };
    }

    private static SwingSignal None(TrendProgress progress, string reason) => new()
    {
        Action = SwingSignalAction.None,
        Progress = progress,
        Reason = reason
    };
}
