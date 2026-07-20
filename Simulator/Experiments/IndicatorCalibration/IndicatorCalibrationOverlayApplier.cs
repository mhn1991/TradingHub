using Simulator.Calibration;

namespace Simulator.Experiments.IndicatorCalibration;

/// <summary>
/// Strongly typed overlay application (blueprint §15.3): applies an approved artifact's
/// <see cref="IndicatorCalibrationArtifact.Overrides"/>/<see cref="IndicatorCalibrationArtifact.AblationOverrides"/>
/// onto a baseline options object via the manifest's compiled descriptors - never reflection, and
/// never a partial application (any parameter/ablation id the manifest doesn't recognize is a hard
/// failure, not a silently-skipped override).
/// </summary>
public static class IndicatorCalibrationOverlayApplier
{
    public static TOptions Apply<TOptions>(
        TOptions baseline, IndicatorCalibrationArtifact artifact, IIndicatorCalibrationManifest<TOptions> manifest)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(manifest);

        TOptions result = baseline;
        foreach (CalibratedParameterOverride overrideItem in artifact.Overrides)
        {
            ICalibrationParameterDescriptor<TOptions>? descriptor = manifest.Parameters
                .FirstOrDefault(parameter => string.Equals(parameter.ParameterId, overrideItem.ParameterId, StringComparison.Ordinal));
            if (descriptor is null)
            {
                throw new InvalidOperationException(
                    $"Artifact '{artifact.CalibrationId}' references parameter '{overrideItem.ParameterId}', " +
                    $"which manifest '{manifest.ManifestVersion}' does not declare.");
            }
            result = descriptor.Apply(result, overrideItem.CalibratedValue);
        }

        foreach ((string parameterId, bool value) in artifact.AblationOverrides)
        {
            ICalibrationAblationDescriptor<TOptions>? ablation = manifest.Ablations
                .FirstOrDefault(item => string.Equals(item.ParameterId, parameterId, StringComparison.Ordinal));
            if (ablation is null)
            {
                throw new InvalidOperationException(
                    $"Artifact '{artifact.CalibrationId}' references ablation '{parameterId}', " +
                    $"which manifest '{manifest.ManifestVersion}' does not declare.");
            }
            result = ablation.Apply(result, value);
        }

        return result;
    }
}
