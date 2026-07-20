using Simulator.Calibration;

namespace Simulator.Experiments.IndicatorCalibration;

/// <summary>
/// One point in the search space: a full set of numeric and ablation parameter values, keyed by
/// <c>ParameterId</c>. Deliberately flat and generic (not tied to any <c>TOptions</c>) so the
/// search algorithm itself (this file and its siblings) can be written and tested once, against
/// synthetic objectives, independent of any concrete strategy's options type (blueprint §19
/// Phase 4: "Use synthetic objective functions first").
/// </summary>
public sealed record CalibrationCandidate
{
    public required IReadOnlyDictionary<string, decimal> NumericValues { get; init; }
    public required IReadOnlyDictionary<string, bool> AblationValues { get; init; }

    public static CalibrationCandidate FromDefaults<TOptions>(IIndicatorCalibrationManifest<TOptions> manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return new CalibrationCandidate
        {
            NumericValues = manifest.Parameters.ToDictionary(
                parameter => parameter.ParameterId, parameter => parameter.DefaultValue, StringComparer.Ordinal),
            AblationValues = manifest.Ablations.ToDictionary(
                ablation => ablation.ParameterId, ablation => ablation.DefaultValue, StringComparer.Ordinal)
        };
    }

    /// <summary>
    /// Reads the actual effective baseline configuration off <paramref name="baselineOptions"/>
    /// via the manifest's compiled descriptors - distinct from <see cref="FromDefaults"/>, which
    /// uses each descriptor's *declared* default and can differ from what a specific instrument's
    /// request is actually configured with (blueprint §9.1: "retain all current explicit
    /// overrides; never substitute manifest defaults for the actual configured baseline").
    /// </summary>
    public static CalibrationCandidate FromEffectiveOptions<TOptions>(
        TOptions baselineOptions, IIndicatorCalibrationManifest<TOptions> manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return new CalibrationCandidate
        {
            NumericValues = manifest.Parameters.ToDictionary(
                parameter => parameter.ParameterId, parameter => parameter.Read(baselineOptions), StringComparer.Ordinal),
            AblationValues = manifest.Ablations.ToDictionary(
                ablation => ablation.ParameterId, ablation => ablation.Read(baselineOptions), StringComparer.Ordinal)
        };
    }

    public static CalibrationCandidate FromStartingValues<TOptions>(
        IIndicatorCalibrationManifest<TOptions> manifest,
        Func<ICalibrationParameterDescriptor<TOptions>, decimal> selectStartingValue)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(selectStartingValue);
        return new CalibrationCandidate
        {
            NumericValues = manifest.Parameters.ToDictionary(
                parameter => parameter.ParameterId, selectStartingValue, StringComparer.Ordinal),
            AblationValues = manifest.Ablations.ToDictionary(
                ablation => ablation.ParameterId, ablation => ablation.DefaultValue, StringComparer.Ordinal)
        };
    }

    public CalibrationCandidate WithNumericValue(string parameterId, decimal value) => this with
    {
        NumericValues = Merge(NumericValues, parameterId, value)
    };

    public CalibrationCandidate WithAblationValue(string parameterId, bool value) => this with
    {
        AblationValues = Merge(AblationValues, parameterId, value)
    };

    public decimal RequireNumericValue(string parameterId) =>
        NumericValues.TryGetValue(parameterId, out decimal value)
            ? value
            : throw new KeyNotFoundException($"Candidate does not declare a value for parameter '{parameterId}'.");

    /// <summary>Applies every declared value onto <paramref name="baseline"/> via the manifest's compiled descriptors - never reflection.</summary>
    public TOptions ApplyTo<TOptions>(TOptions baseline, IIndicatorCalibrationManifest<TOptions> manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        TOptions result = baseline;
        foreach (ICalibrationParameterDescriptor<TOptions> parameter in manifest.Parameters)
        {
            if (NumericValues.TryGetValue(parameter.ParameterId, out decimal value))
                result = parameter.Apply(result, value);
        }
        foreach (ICalibrationAblationDescriptor<TOptions> ablation in manifest.Ablations)
        {
            if (AblationValues.TryGetValue(ablation.ParameterId, out bool value))
                result = ablation.Apply(result, value);
        }
        return result;
    }

    private static IReadOnlyDictionary<string, T> Merge<T>(IReadOnlyDictionary<string, T> source, string key, T value)
    {
        var merged = new Dictionary<string, T>(source, StringComparer.Ordinal) { [key] = value };
        return merged;
    }
}

/// <summary>
/// Bounds/constraint validation that never throws - used to reject an out-of-range or
/// constraint-violating candidate *before* scheduling a backtest (blueprint §6.1/§9.4/§9.5),
/// as opposed to <see cref="ICalibrationParameterDescriptor{TOptions}.Apply"/>, which throws
/// (appropriate for authoring-time manifest mistakes, not routine search-time rejection).
/// </summary>
public static class CalibrationCandidateValidator
{
    public static bool TryBuildValidOptions<TOptions>(
        CalibrationCandidate candidate,
        TOptions baseline,
        IIndicatorCalibrationManifest<TOptions> manifest,
        out TOptions? effectiveOptions,
        out string? rejectionReason)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(manifest);

        foreach (ICalibrationParameterDescriptor<TOptions> parameter in manifest.Parameters)
        {
            if (candidate.NumericValues.TryGetValue(parameter.ParameterId, out decimal value) &&
                (value < parameter.HardMinimum || value > parameter.HardMaximum))
            {
                effectiveOptions = default;
                rejectionReason = $"Parameter '{parameter.ParameterId}' value {value} is outside its hard bounds " +
                    $"[{parameter.HardMinimum}, {parameter.HardMaximum}].";
                return false;
            }
        }

        TOptions applied = candidate.ApplyTo(baseline, manifest);
        foreach (ICalibrationConstraint<TOptions> constraint in manifest.Constraints)
        {
            CalibrationConstraintResult result = constraint.Validate(applied);
            if (!result.IsValid)
            {
                effectiveOptions = default;
                rejectionReason = result.RejectionReason ?? $"Constraint '{constraint.ConstraintId}' failed.";
                return false;
            }
        }

        effectiveOptions = applied;
        rejectionReason = null;
        return true;
    }
}
