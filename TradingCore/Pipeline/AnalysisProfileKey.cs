using ChartAnnotator.Engine;
using Brokers.Models;
using System.Collections.Frozen;

namespace TradingCore.Pipeline;

/// <summary>
/// Identifies one distinct computed-analysis configuration, separate from Agent interpretation
/// configuration (thresholds, confirmation rules, feature switches - those can differ freely
/// between Agents sharing one profile). Two Agents whose effective <see cref="ChartAnnotationOptions"/>
/// and required intervals are identical can share one <c>ChartAnnotationEngine</c> instance and
/// one computed <c>MarketAnalysisSnapshot</c> per candle; any difference that changes computed
/// analysis must produce a different key.
/// </summary>
/// <remarks>
/// <see cref="ProfileHash"/> is a content hash of the *entire* <see cref="ChartAnnotationOptions"/>
/// (via <see cref="ChartAnnotationOptionsHasher"/>) rather than a curated field subset - a curated
/// list risks silently omitting a field that affects computed analysis, exactly the failure mode
/// this key exists to prevent. <see cref="FeatureSchemaHash"/> is carried for provenance/parity
/// only and is deliberately excluded from equality/grouping: two profiles with identical
/// <see cref="ChartAnnotationOptions"/> should still share one engine even if computed under a
/// differing feature-schema version, since feature extraction happens downstream of analysis.
/// </remarks>
public sealed record AnalysisProfileKey
{
    public required string ProfileHash { get; init; }
    public required IReadOnlySet<BarInterval> RequiredIntervals { get; init; }
    public required string FeatureSchemaHash { get; init; }

    public static AnalysisProfileKey Create(
        ChartAnnotationOptions options,
        IReadOnlySet<BarInterval> requiredIntervals,
        string featureSchemaHash)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(requiredIntervals);
        ArgumentException.ThrowIfNullOrWhiteSpace(featureSchemaHash);
        return new AnalysisProfileKey
        {
            ProfileHash = ChartAnnotationOptionsHasher.ComputeHash(options),
            // Never retain a caller-owned set. Profile keys are dictionary keys for the lifetime
            // of a run, so mutating the source set after registration must not change equality or
            // its hash code underneath those dictionaries.
            RequiredIntervals = requiredIntervals.ToFrozenSet(),
            FeatureSchemaHash = featureSchemaHash
        };
    }

    /// <summary>
    /// Grouping/equality is <see cref="ProfileHash"/> + <see cref="RequiredIntervals"/> set-content
    /// equality - never the default record equality for <see cref="RequiredIntervals"/>, since
    /// <c>IReadOnlySet&lt;T&gt;</c>'s synthesized equality is reference-based, not content-based
    /// (the same type-name-vs-content pitfall <see cref="ChartAnnotationOptionsHasher"/> documents
    /// for collection-typed record members).
    /// </summary>
    public bool Equals(AnalysisProfileKey? other) =>
        other is not null &&
        string.Equals(ProfileHash, other.ProfileHash, StringComparison.Ordinal) &&
        RequiredIntervals.SetEquals(other.RequiredIntervals);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ProfileHash, StringComparer.Ordinal);
        foreach (BarInterval interval in RequiredIntervals.OrderBy(i => i.ToString(), StringComparer.Ordinal))
            hash.Add(interval);
        return hash.ToHashCode();
    }
}
