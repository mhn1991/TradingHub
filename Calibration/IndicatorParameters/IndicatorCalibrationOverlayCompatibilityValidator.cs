namespace Simulator.Calibration;

/// <summary>
/// Everything a consumer must know about "the strategy instance about to receive an overlay"
/// before applying one (blueprint §15.2). <see cref="CurrentBaselineConfigurationHash"/> is the
/// hash of the *fully resolved effective* options object (compiled defaults plus any explicit
/// request/configuration overrides already in force) - distinct from
/// <see cref="CalibrationCompatibilityIdentity.DefaultConfigurationHash"/>, which only covers the
/// compiled-in defaults (blueprint §9.1: "retain all current explicit overrides; never substitute
/// manifest defaults for the actual configured baseline").
/// </summary>
public sealed record CalibrationOverlayConsumptionContext
{
    public required CalibrationCompatibilityIdentity CurrentIdentity { get; init; }
    public required string CurrentInstrument { get; init; }
    public required string CurrentBaselineConfigurationHash { get; init; }
    public required IReadOnlySet<string> SupportedManifestVersions { get; init; }
}

public sealed record CalibrationOverlayCompatibilityResult
{
    public required bool IsCompatible { get; init; }
    public required IReadOnlyList<string> RejectionReasons { get; init; }

    public static CalibrationOverlayCompatibilityResult Compatible { get; } =
        new() { IsCompatible = true, RejectionReasons = [] };

    public static CalibrationOverlayCompatibilityResult Incompatible(IReadOnlyList<string> reasons) =>
        new() { IsCompatible = false, RejectionReasons = reasons };
}

/// <summary>
/// The consumption-time gate from blueprint §15.2. Every check runs regardless of earlier
/// failures so a rejected overlay reports every mismatch at once, not just the first one -
/// "reject the complete overlay ... never partially apply" is enforced by the caller treating
/// any non-empty <see cref="CalibrationOverlayCompatibilityResult.RejectionReasons"/> as total
/// rejection, not by this validator trying to apply a partial subset.
///
/// "Artifact checksum is valid" (the tenth §15.2 check) is deliberately not performed here - it
/// is a storage-integrity concern, verified once when the artifact is read from the repository
/// (mirroring how <c>FileCalibrationArtifactRepository</c> already verifies content hash on every
/// read for Setup/Management/MetaModel artifacts). This validator only checks semantic
/// compatibility of an artifact that has already passed that integrity check.
/// </summary>
public static class IndicatorCalibrationOverlayCompatibilityValidator
{
    public static CalibrationOverlayCompatibilityResult Validate(
        IndicatorCalibrationArtifact artifact,
        CalibrationOverlayConsumptionContext context)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(context);
        var reasons = new List<string>();

        if (artifact.PromotionStatus != CalibrationPromotionStatus.Approved)
            reasons.Add($"Artifact promotion status is '{artifact.PromotionStatus}', not Approved.");
        if (artifact.Outcome != CalibrationOutcome.Improved)
            reasons.Add($"Artifact outcome is '{artifact.Outcome}', not Improved.");
        if (!string.Equals(artifact.StrategyId, context.CurrentIdentity.StrategyId, StringComparison.Ordinal))
        {
            reasons.Add(
                $"StrategyId mismatch: artifact='{artifact.StrategyId}' current='{context.CurrentIdentity.StrategyId}'.");
        }
        if (!string.Equals(
                artifact.StrategyImplementationVersion,
                context.CurrentIdentity.StrategyImplementationVersion,
                StringComparison.Ordinal))
        {
            reasons.Add(
                $"StrategyImplementationVersion mismatch: artifact='{artifact.StrategyImplementationVersion}' " +
                $"current='{context.CurrentIdentity.StrategyImplementationVersion}'.");
        }
        if (!string.Equals(artifact.OptionsSchemaVersion, context.CurrentIdentity.OptionsSchemaVersion, StringComparison.Ordinal))
        {
            reasons.Add(
                $"OptionsSchemaVersion mismatch: artifact='{artifact.OptionsSchemaVersion}' " +
                $"current='{context.CurrentIdentity.OptionsSchemaVersion}'.");
        }
        if (!context.SupportedManifestVersions.Contains(artifact.ManifestVersion))
            reasons.Add($"ManifestVersion '{artifact.ManifestVersion}' is not supported by this consumer.");
        if (!string.Equals(artifact.Instrument, context.CurrentInstrument, StringComparison.Ordinal))
            reasons.Add($"Instrument mismatch: artifact='{artifact.Instrument}' current='{context.CurrentInstrument}'.");
        if (!string.Equals(artifact.TimeframeTopologyHash, context.CurrentIdentity.TimeframeTopologyHash, StringComparison.Ordinal))
            reasons.Add("TimeframeTopologyHash mismatch.");
        if (!string.Equals(artifact.BaselineConfigurationHash, context.CurrentBaselineConfigurationHash, StringComparison.Ordinal))
        {
            reasons.Add(
                "BaselineConfigurationHash mismatch - the current effective configuration differs from what this " +
                "artifact was calibrated against.");
        }

        return reasons.Count == 0
            ? CalibrationOverlayCompatibilityResult.Compatible
            : CalibrationOverlayCompatibilityResult.Incompatible(reasons);
    }
}
