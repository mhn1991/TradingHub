namespace Simulator.Calibration;

public enum CalibrationParameterCategory
{
    Entry,
    Confirmation,
    Volatility,
    Stop,
    Target,
    Management
}

public enum CalibrationValueKind
{
    IndicatorLevel,
    AtrMultiple,
    Ratio,
    PercentageZeroToOne,
    PercentageZeroToHundred,
    AbsolutePrice,
    Count
}

public enum CalibrationSpacing
{
    Linear,
    Logarithmic,
    Discrete
}

/// <summary>
/// A single calibratable numeric field, applied via compiled read/apply delegates - never
/// reflection (blueprint §5: "Do not use strings plus reflection as the main mutation
/// mechanism"). <see cref="DiagnosticPath"/> exists only for ledger/artifact readability.
/// </summary>
public interface ICalibrationParameterDescriptor<TOptions>
{
    string ParameterId { get; }
    string DiagnosticPath { get; }
    CalibrationParameterCategory Category { get; }
    CalibrationValueKind ValueKind { get; }
    CalibrationSpacing Spacing { get; }

    decimal DefaultValue { get; }
    decimal HardMinimum { get; }
    decimal HardMaximum { get; }
    IReadOnlyList<decimal> CoarseGrid { get; }
    decimal RefinementStep { get; }

    decimal ConservativeStartingValue { get; }
    decimal PermissiveStartingValue { get; }
    int DeclaredSearchOrder { get; }

    decimal Read(TOptions options);
    TOptions Apply(TOptions options, decimal value);
    void ValidateValue(decimal value);
}

/// <summary>Concrete, delegate-backed implementation - the only way manifests should construct a descriptor.</summary>
public sealed class CalibrationParameterDescriptor<TOptions> : ICalibrationParameterDescriptor<TOptions>
{
    private readonly Func<TOptions, decimal> _read;
    private readonly Func<TOptions, decimal, TOptions> _apply;

    public CalibrationParameterDescriptor(
        string parameterId,
        string diagnosticPath,
        CalibrationParameterCategory category,
        CalibrationValueKind valueKind,
        CalibrationSpacing spacing,
        decimal defaultValue,
        decimal hardMinimum,
        decimal hardMaximum,
        IReadOnlyList<decimal> coarseGrid,
        decimal refinementStep,
        decimal conservativeStartingValue,
        decimal permissiveStartingValue,
        int declaredSearchOrder,
        Func<TOptions, decimal> read,
        Func<TOptions, decimal, TOptions> apply)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticPath);
        ArgumentNullException.ThrowIfNull(coarseGrid);
        if (coarseGrid.Count == 0)
            throw new ArgumentException("A coarse grid is required.", nameof(coarseGrid));
        if (hardMinimum >= hardMaximum)
            throw new ArgumentException("HardMinimum must be strictly less than HardMaximum.", nameof(hardMinimum));
        if (defaultValue < hardMinimum || defaultValue > hardMaximum)
            throw new ArgumentException("DefaultValue must lie within the hard bounds.", nameof(defaultValue));
        if (coarseGrid.Any(value => value < hardMinimum || value > hardMaximum))
            throw new ArgumentException("Every coarse-grid value must lie within the hard bounds.", nameof(coarseGrid));
        if (refinementStep <= 0m)
            throw new ArgumentOutOfRangeException(nameof(refinementStep));
        if (conservativeStartingValue < hardMinimum || conservativeStartingValue > hardMaximum ||
            permissiveStartingValue < hardMinimum || permissiveStartingValue > hardMaximum)
        {
            throw new ArgumentException("Starting values must lie within the hard bounds.");
        }

        ParameterId = parameterId;
        DiagnosticPath = diagnosticPath;
        Category = category;
        ValueKind = valueKind;
        Spacing = spacing;
        DefaultValue = defaultValue;
        HardMinimum = hardMinimum;
        HardMaximum = hardMaximum;
        CoarseGrid = coarseGrid;
        RefinementStep = refinementStep;
        ConservativeStartingValue = conservativeStartingValue;
        PermissiveStartingValue = permissiveStartingValue;
        DeclaredSearchOrder = declaredSearchOrder;
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
    }

    public string ParameterId { get; }
    public string DiagnosticPath { get; }
    public CalibrationParameterCategory Category { get; }
    public CalibrationValueKind ValueKind { get; }
    public CalibrationSpacing Spacing { get; }
    public decimal DefaultValue { get; }
    public decimal HardMinimum { get; }
    public decimal HardMaximum { get; }
    public IReadOnlyList<decimal> CoarseGrid { get; }
    public decimal RefinementStep { get; }
    public decimal ConservativeStartingValue { get; }
    public decimal PermissiveStartingValue { get; }
    public int DeclaredSearchOrder { get; }

    public decimal Read(TOptions options) => _read(options);
    public TOptions Apply(TOptions options, decimal value)
    {
        ValidateValue(value);
        return _apply(options, value);
    }

    public void ValidateValue(decimal value)
    {
        if (value < HardMinimum || value > HardMaximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"Parameter '{ParameterId}' value {value} is outside its hard bounds [{HardMinimum}, {HardMaximum}].");
        }
    }
}

/// <summary>Boolean feature-switch, tested through ablation (on/off comparison) - never mixed into numeric coordinate descent.</summary>
public interface ICalibrationAblationDescriptor<TOptions>
{
    string ParameterId { get; }
    string DiagnosticPath { get; }
    bool DefaultValue { get; }

    bool Read(TOptions options);
    TOptions Apply(TOptions options, bool value);
}

public sealed class CalibrationAblationDescriptor<TOptions> : ICalibrationAblationDescriptor<TOptions>
{
    private readonly Func<TOptions, bool> _read;
    private readonly Func<TOptions, bool, TOptions> _apply;

    public CalibrationAblationDescriptor(
        string parameterId,
        string diagnosticPath,
        bool defaultValue,
        Func<TOptions, bool> read,
        Func<TOptions, bool, TOptions> apply)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticPath);
        ParameterId = parameterId;
        DiagnosticPath = diagnosticPath;
        DefaultValue = defaultValue;
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
    }

    public string ParameterId { get; }
    public string DiagnosticPath { get; }
    public bool DefaultValue { get; }
    public bool Read(TOptions options) => _read(options);
    public TOptions Apply(TOptions options, bool value) => _apply(options, value);
}

/// <summary>Compiled cross-parameter constraint - never an arbitrary expression string (blueprint §5.2).</summary>
public interface ICalibrationConstraint<TOptions>
{
    string ConstraintId { get; }
    string Description { get; }
    CalibrationConstraintResult Validate(TOptions options);
}

public sealed record CalibrationConstraintResult(bool IsValid, string? RejectionReason)
{
    public static CalibrationConstraintResult Valid { get; } = new(true, null);
    public static CalibrationConstraintResult Invalid(string reason) => new(false, reason);
}

public sealed class CalibrationConstraint<TOptions> : ICalibrationConstraint<TOptions>
{
    private readonly Func<TOptions, CalibrationConstraintResult> _validate;

    public CalibrationConstraint(
        string constraintId,
        string description,
        Func<TOptions, CalibrationConstraintResult> validate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(constraintId);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ConstraintId = constraintId;
        Description = description;
        _validate = validate ?? throw new ArgumentNullException(nameof(validate));
    }

    public string ConstraintId { get; }
    public string Description { get; }
    public CalibrationConstraintResult Validate(TOptions options) => _validate(options);
}

/// <summary>
/// A declared, small joint-search group (blueprint §9.4: 2 parameters by default, 3 requires
/// <see cref="ExplicitOptIn"/>). Members may reference either numeric parameter ids or ablation
/// parameter ids - a boolean ablation flag may form one dimension of an interaction group.
/// </summary>
public sealed record CalibrationInteractionGroup
{
    public required string GroupId { get; init; }
    public required IReadOnlyList<string> ParameterIds { get; init; }
    public bool ExplicitOptIn { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(GroupId))
            throw new ArgumentException("GroupId is required.");
        if (ParameterIds is null || ParameterIds.Count < 2)
            throw new ArgumentException($"Interaction group '{GroupId}' must declare at least 2 parameter ids.");
        if (ParameterIds.Distinct(StringComparer.Ordinal).Count() != ParameterIds.Count)
            throw new ArgumentException($"Interaction group '{GroupId}' has duplicate parameter ids.");
        if (ParameterIds.Count > 2 && !ExplicitOptIn)
        {
            throw new ArgumentException(
                $"Interaction group '{GroupId}' declares {ParameterIds.Count} parameters; " +
                "groups larger than 2 require ExplicitOptIn = true.");
        }
        if (ParameterIds.Count > 3)
        {
            throw new ArgumentException(
                $"Interaction group '{GroupId}' declares {ParameterIds.Count} parameters; " +
                "the maximum supported interaction-group size is 3.");
        }
    }
}

/// <summary>
/// A versioned, explicit, hand-authored calibration manifest for one strategy's options type.
/// Manifests are checked-in C# factories - reflection must never be used to discover all numeric
/// options (blueprint §5.3).
/// </summary>
public interface IIndicatorCalibrationManifest<TOptions>
{
    int SchemaVersion { get; }
    string ManifestVersion { get; }
    string StrategyId { get; }
    string OptionsSchemaVersion { get; }

    IReadOnlyList<ICalibrationParameterDescriptor<TOptions>> Parameters { get; }
    IReadOnlyList<ICalibrationAblationDescriptor<TOptions>> Ablations { get; }
    IReadOnlyList<CalibrationInteractionGroup> InteractionGroups { get; }
    IReadOnlyList<ICalibrationConstraint<TOptions>> Constraints { get; }

    CalibrationScoringPolicy ScoringPolicy { get; }
    CalibrationAcceptancePolicy AcceptancePolicy { get; }

    void Validate();
}

/// <summary>
/// Shared validation logic every concrete <see cref="IIndicatorCalibrationManifest{TOptions}"/>
/// should delegate to from its own <c>Validate()</c>, so every manifest enforces the same
/// structural invariants (unique ids, interaction groups reference declared parameters, etc.)
/// without duplicating the checks per strategy.
/// </summary>
public static class IndicatorCalibrationManifestValidation
{
    public static void Validate<TOptions>(IIndicatorCalibrationManifest<TOptions> manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SchemaVersion < 1)
            throw new ArgumentException("ManifestVersion schema must be >= 1.");
        if (string.IsNullOrWhiteSpace(manifest.ManifestVersion) ||
            string.IsNullOrWhiteSpace(manifest.StrategyId) ||
            string.IsNullOrWhiteSpace(manifest.OptionsSchemaVersion))
        {
            throw new ArgumentException("ManifestVersion, StrategyId, and OptionsSchemaVersion are required.");
        }
        if (manifest.Parameters is null || manifest.Parameters.Count == 0)
            throw new ArgumentException("A manifest must declare at least one numeric parameter.");
        if (manifest.Parameters.Select(item => item.ParameterId).Distinct(StringComparer.Ordinal).Count() !=
            manifest.Parameters.Count)
        {
            throw new ArgumentException("Duplicate ParameterId values in the numeric parameter list.");
        }

        ArgumentNullException.ThrowIfNull(manifest.Ablations);
        if (manifest.Ablations.Select(item => item.ParameterId).Distinct(StringComparer.Ordinal).Count() !=
            manifest.Ablations.Count)
        {
            throw new ArgumentException("Duplicate ParameterId values in the ablation list.");
        }

        var allIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (ICalibrationParameterDescriptor<TOptions> parameter in manifest.Parameters)
            allIds.Add(parameter.ParameterId);
        foreach (ICalibrationAblationDescriptor<TOptions> ablation in manifest.Ablations)
        {
            if (!allIds.Add(ablation.ParameterId))
            {
                throw new ArgumentException(
                    $"Parameter id '{ablation.ParameterId}' is declared as both a numeric parameter and an ablation flag.");
            }
        }

        ArgumentNullException.ThrowIfNull(manifest.InteractionGroups);
        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (CalibrationInteractionGroup group in manifest.InteractionGroups)
        {
            group.Validate();
            if (!groupIds.Add(group.GroupId))
                throw new ArgumentException($"Duplicate interaction group id '{group.GroupId}'.");
            foreach (string parameterId in group.ParameterIds)
            {
                if (!allIds.Contains(parameterId))
                {
                    throw new ArgumentException(
                        $"Interaction group '{group.GroupId}' references undeclared parameter id '{parameterId}'.");
                }
            }
        }

        ArgumentNullException.ThrowIfNull(manifest.Constraints);
        if (manifest.Constraints.Select(item => item.ConstraintId).Distinct(StringComparer.Ordinal).Count() !=
            manifest.Constraints.Count)
        {
            throw new ArgumentException("Duplicate ConstraintId values.");
        }

        ArgumentNullException.ThrowIfNull(manifest.ScoringPolicy);
        ArgumentNullException.ThrowIfNull(manifest.AcceptancePolicy);
        manifest.ScoringPolicy.Validate();
        manifest.AcceptancePolicy.Validate();
    }
}
