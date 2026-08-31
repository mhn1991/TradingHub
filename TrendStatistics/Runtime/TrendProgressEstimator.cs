using TrendStatistics.Detection;
using TrendStatistics.Profiles;
using TrendStatistics.Statistics;

namespace TrendStatistics.Runtime;

/// <summary>
/// Where a live trend sits inside its own historical distribution - blueprint sections 23 and 24.
/// <para>
/// Price and time are reported separately and never blended. Section 25 is explicit that V1 must
/// measure how useful each dimension is independently before any joint score is attempted.
/// </para>
/// </summary>
public readonly record struct TrendProgress
{
    /// <summary>Fraction of comparable historical trends this one has already out-moved (section 23).</summary>
    public required decimal PricePercentile { get; init; }

    /// <summary>Fraction it has already out-lasted (section 24).</summary>
    public required decimal TimePercentile { get; init; }

    public required decimal MovePct { get; init; }
    public required int DurationBars { get; init; }

    /// <summary>False when the profile has too few samples to rank against (section 22).</summary>
    public required bool IsReliable { get; init; }
}

public interface ITrendProgressEstimator
{
    TrendProgress Estimate(TrendState state, DirectionTrendProfile profile);
}

public sealed class TrendProgressEstimator(int minimumSamples = 20) : ITrendProgressEstimator
{
    private readonly int _minimumSamples = minimumSamples > 0
        ? minimumSamples
        : throw new ArgumentOutOfRangeException(nameof(minimumSamples));

    public TrendProgress Estimate(TrendState state, DirectionTrendProfile profile)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(profile);
        if (state.Direction is null)
            throw new ArgumentException("A neutral trend has no progress to estimate.", nameof(state));
        if (state.Direction != profile.Direction)
        {
            throw new ArgumentException(
                $"Trend is {state.Direction} but the profile describes {profile.Direction}. " +
                "Section 5 forbids ranking a trend against the opposite direction's distribution.",
                nameof(profile));
        }
        if (state.Symbol is not null
            && !string.Equals(state.Symbol, profile.Symbol, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Trend symbol '{state.Symbol}' cannot be ranked against profile '{profile.Symbol}'.",
                nameof(profile));
        }

        // The live move is unsigned, matching how the profile stores historical moves.
        decimal move = Math.Abs(state.CurrentMovePct);

        bool reliable = profile.IsReliable(_minimumSamples);
        return new TrendProgress
        {
            PricePercentile = reliable ? QuantileCalculator.PercentileOf(profile.SortedMovePct, move) : 0m,
            TimePercentile = reliable
                ? QuantileCalculator.PercentileOf(profile.SortedDurationBars, state.DurationBars)
                : 0m,
            MovePct = move,
            DurationBars = state.DurationBars,
            IsReliable = reliable
        };
    }
}
