using TrendStatistics.Statistics;

namespace TrendStatistics.Runtime;

/// <summary>Blueprint section 35's exhaustion bands.</summary>
public enum ExhaustionLevel { Low, Moderate, High, Extreme }

/// <summary>
/// Section 35's <c>TrendExhaustionState</c>. The raw percentiles are retained alongside the score
/// because section 35 is explicit that the score must not hide the inputs.
/// </summary>
public readonly record struct TrendExhaustionState
{
    public required decimal PricePercentile { get; init; }
    public required decimal TimePercentile { get; init; }
    public required decimal JointRarity { get; init; }
    public required decimal Imbalance { get; init; }
    public required bool StructuralWeakness { get; init; }
    public required decimal ExhaustionScore { get; init; }
    public required ExhaustionLevel Level { get; init; }
}

public interface IExhaustionEstimator
{
    TrendExhaustionState Estimate(TrendProgress progress, JointDistribution joint, bool structuralWeakness);
}

/// <summary>
/// Sections 32 to 35. Scores how far through its own historical distribution a live trend is.
/// <para>
/// Section 33 sets the boundary on what this may be used for: P95 is an exhaustion <i>zone</i>, not
/// an automatic exit, because the largest trends may supply much of the system's profit and
/// force-closing them at a quantile would systematically cut the winners. The score therefore
/// drives the position state machine's protection stages, never an unconditional close.
/// </para>
/// </summary>
public sealed class ExhaustionEstimator : IExhaustionEstimator
{
    public TrendExhaustionState Estimate(TrendProgress progress, JointDistribution joint, bool structuralWeakness)
    {
        ArgumentNullException.ThrowIfNull(joint);

        decimal rarity = joint.Rarity(progress.PricePercentile, progress.TimePercentile);
        decimal imbalance = JointDistribution.Imbalance(progress.PricePercentile, progress.TimePercentile);

        // The maximum of the two axes, not the mean: section 34's two worked examples are both
        // exhaustion signals precisely because ONE axis is extreme while the other is not, and a
        // mean would report them as unremarkable.
        decimal axis = Math.Max(progress.PricePercentile, progress.TimePercentile);

        // Rarity reinforces rather than replaces the axis reading, and structural weakness adds a
        // fixed increment because it is evidence of a different kind - price/time say "this is far
        // through its distribution", structure says "it is breaking".
        decimal score = Math.Min(1m, (axis * 0.6m) + (rarity * 0.4m) + (structuralWeakness ? 0.15m : 0m));

        return new TrendExhaustionState
        {
            PricePercentile = progress.PricePercentile,
            TimePercentile = progress.TimePercentile,
            JointRarity = rarity,
            Imbalance = imbalance,
            StructuralWeakness = structuralWeakness,
            ExhaustionScore = score,
            Level = score switch
            {
                >= 0.95m => ExhaustionLevel.Extreme,
                >= 0.90m => ExhaustionLevel.High,
                >= 0.75m => ExhaustionLevel.Moderate,
                _ => ExhaustionLevel.Low
            }
        };
    }
}
