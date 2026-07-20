using Brokers.Models;

namespace Simulator.Calibration;

/// <summary>
/// Tooling-layer helper for the "auto-apply the best approved calibration" consumption-side flow
/// (blueprint task: nightly calibration + auto-apply for tests). Deliberately kept out of the
/// engine/overlay-resolver path, which must only ever consume an explicit, already-pinned artifact
/// GUID - there is no "latest approved" lookup baked into the engine itself. This type performs
/// that resolution once, for one CLI invocation, and hands back a single GUID the caller then pins
/// exactly as if a human had typed it; the overlay resolver's own compatibility check downstream is
/// still the final authority and will reject a mismatched pick loudly rather than silently
/// mis-applying it.
/// </summary>
public static class BestApprovedCalibrationArtifactResolver
{
    /// <summary>
    /// Finds the most recently created <see cref="CalibrationPromotionStatus.Approved"/> +
    /// <see cref="CalibrationOutcome.Improved"/> artifact matching <paramref name="strategyId"/>,
    /// <paramref name="instrument"/> and the standard topology for <paramref name="executionInterval"/>
    /// (see <see cref="StandardTimeframeTopologyFactory"/>). Returns null if none match - the caller
    /// (not this resolver) decides whether that is an error or simply "nothing to pin yet".
    /// </summary>
    public static async Task<Guid?> ResolveAsync(
        ICalibrationArtifactRepository artifacts,
        string strategyId,
        InstrumentKey instrument,
        BarInterval executionInterval,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);

        string expectedTopologyHash = StandardTimeframeTopologyFactory.Build(executionInterval).ComputeHash();

        IReadOnlyList<CalibrationArtifactMetadata> metadata = await artifacts
            .ListAsync(CalibrationArtifactType.IndicatorParameters, take: 500, cancellationToken).ConfigureAwait(false);

        CalibrationArtifactMetadata? best = null;
        foreach (CalibrationArtifactMetadata item in metadata)
        {
            if (item.PromotionStatus != CalibrationPromotionStatus.Approved)
                continue;
            if (best is not null && item.CreatedAt <= best.CreatedAt)
                continue;

            IndicatorCalibrationArtifact? artifact = await artifacts
                .GetIndicatorParametersAsync(item.Id, cancellationToken).ConfigureAwait(false);
            if (artifact is null || artifact.Outcome != CalibrationOutcome.Improved)
                continue;
            if (!string.Equals(artifact.StrategyId, strategyId, StringComparison.Ordinal))
                continue;
            if (!string.Equals(artifact.Instrument, instrument.Value, StringComparison.Ordinal))
                continue;
            if (!string.Equals(artifact.TimeframeTopologyHash, expectedTopologyHash, StringComparison.Ordinal))
                continue;

            best = item;
        }

        return best?.Id;
    }
}
