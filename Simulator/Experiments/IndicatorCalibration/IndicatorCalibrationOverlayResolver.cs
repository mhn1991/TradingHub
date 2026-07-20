using Agent.Configuration;
using Brokers.Models;
using Simulator.Calibration;
using Simulator.Models;

namespace Simulator.Experiments.IndicatorCalibration;

/// <summary>
/// Consumption-time overlay resolution (blueprint §15, §19 Phase 7). Explicit pinning only - there
/// is no "latest approved artifact" lookup anywhere in this class; the only input is the artifact
/// id a caller already decided to pin. Any incompatibility (missing artifact, not approved, not
/// Improved, or any <see cref="IndicatorCalibrationOverlayCompatibilityValidator"/> mismatch) fails
/// the whole resolution loudly - it never silently falls back to baseline, so a stale/wrong pin is
/// caught immediately rather than quietly running with unintended behaviour. Removing the pin
/// (leaving <see cref="StrategyInstrumentAssignment.IndicatorCalibrationArtifactId"/> null) is the
/// only way back to baseline behaviour, and that path never calls this class at all.
/// </summary>
public static class IndicatorCalibrationOverlayResolver
{
    /// <summary>
    /// Resolves one pinned artifact into a ready-to-run <see cref="TradingAgentDefinition"/>.
    /// Throws <see cref="InvalidOperationException"/> on any incompatibility - callers must not
    /// catch this and fall back silently (that would defeat "explicit pinning" - a caller that
    /// wants graceful degradation must decide that explicitly, not have it happen implicitly here).
    /// </summary>
    public static async Task<TradingAgentDefinition> ResolveAsync<TOptions>(
        Guid artifactId,
        TOptions baselineOptions,
        Func<TOptions, TradingAgentDefinition> buildAgentDefinition,
        IIndicatorCalibrationManifest<TOptions> manifest,
        ICalibrationCompatibilityProvider<TOptions> compatibilityProvider,
        TimeframeTopology topology,
        InstrumentKey instrument,
        ICalibrationArtifactRepository artifacts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baselineOptions);
        ArgumentNullException.ThrowIfNull(buildAgentDefinition);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(compatibilityProvider);
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(artifacts);

        IndicatorCalibrationArtifact? artifact = await artifacts
            .GetIndicatorParametersAsync(artifactId, cancellationToken).ConfigureAwait(false);
        if (artifact is null)
            throw new InvalidOperationException($"Indicator-calibration artifact '{artifactId}' does not exist.");

        CalibrationCompatibilityIdentity identity = compatibilityProvider.Describe(topology);
        string baselineHash = IndicatorCalibrationHash.ComputeOfObject(baselineOptions);
        CalibrationOverlayCompatibilityResult compatibility = IndicatorCalibrationOverlayCompatibilityValidator.Validate(
            artifact,
            new CalibrationOverlayConsumptionContext
            {
                CurrentIdentity = identity,
                CurrentInstrument = instrument.Value,
                CurrentBaselineConfigurationHash = baselineHash,
                SupportedManifestVersions = new HashSet<string>(StringComparer.Ordinal) { manifest.ManifestVersion }
            });
        if (!compatibility.IsCompatible)
        {
            throw new InvalidOperationException(
                $"Indicator-calibration artifact '{artifactId}' was rejected in full (no partial application): " +
                string.Join("; ", compatibility.RejectionReasons));
        }

        TOptions overlaid = IndicatorCalibrationOverlayApplier.Apply(baselineOptions, artifact, manifest);
        return buildAgentDefinition(overlaid);
    }

    /// <summary>
    /// Applies <paramref name="assignment"/>'s pinned artifact, if any. A null
    /// <see cref="StrategyInstrumentAssignment.IndicatorCalibrationArtifactId"/> returns the
    /// assignment completely unchanged and never touches <paramref name="artifacts"/> - the
    /// no-overlay case is a true no-op, not "resolve and get a no-change result."
    /// </summary>
    public static async Task<StrategyInstrumentAssignment> ApplyIfPinnedAsync<TOptions>(
        StrategyInstrumentAssignment assignment,
        TOptions baselineOptions,
        Func<TOptions, TradingAgentDefinition> buildAgentDefinition,
        IIndicatorCalibrationManifest<TOptions> manifest,
        ICalibrationCompatibilityProvider<TOptions> compatibilityProvider,
        TimeframeTopology topology,
        ICalibrationArtifactRepository artifacts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        if (assignment.IndicatorCalibrationArtifactId is not { } artifactId)
            return assignment;

        TradingAgentDefinition overlaidDefinition = await ResolveAsync(
            artifactId, baselineOptions, buildAgentDefinition, manifest, compatibilityProvider, topology,
            assignment.Instrument, artifacts, cancellationToken).ConfigureAwait(false);
        return assignment with { AgentDefinitionOverride = overlaidDefinition };
    }
}
