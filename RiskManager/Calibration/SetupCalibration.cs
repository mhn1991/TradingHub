namespace RiskManager.Calibration;

public sealed record SetupCalibrationBucket
{
    public required string StrategyId { get; init; }
    public required string InstrumentGroup { get; init; }
    public required string Regime { get; init; }
    public required decimal ConfidenceFrom { get; init; }
    public required decimal ConfidenceTo { get; init; }
    public required int Samples { get; init; }
    public required decimal WinRate { get; init; }
    public required decimal AverageR { get; init; }
    public required decimal ExpectedR { get; init; }
    public required decimal BrierScore { get; init; }
}

public sealed record SetupCalibrationArtifact
{
    public required int SchemaVersion { get; init; }
    public required string CalibrationId { get; init; }
    public required DateTimeOffset TrainingFrom { get; init; }
    public required DateTimeOffset TrainingTo { get; init; }
    public required IReadOnlyList<string> Instruments { get; init; }
    public required string StrategyVersion { get; init; }
    public required string FeatureSchemaHash { get; init; }
    public required IReadOnlyDictionary<string, string> Parameters { get; init; }
    public required int TotalSamples { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string DataHash { get; init; }
    public required IReadOnlyList<SetupCalibrationBucket> Buckets { get; init; }

    public void Validate(string? requiredFeatureSchemaHash = null)
    {
        if (SchemaVersion != 1 || string.IsNullOrWhiteSpace(CalibrationId) || TrainingFrom >= TrainingTo ||
            Instruments is null || Instruments.Count == 0 || string.IsNullOrWhiteSpace(StrategyVersion) ||
            string.IsNullOrWhiteSpace(FeatureSchemaHash) || Parameters is null || TotalSamples < 0 ||
            string.IsNullOrWhiteSpace(DataHash) || Buckets is null ||
            requiredFeatureSchemaHash is not null && !string.Equals(
                requiredFeatureSchemaHash, FeatureSchemaHash, StringComparison.Ordinal))
            throw new ArgumentException("The setup calibration artifact is missing, incompatible, or invalid.");
        foreach (SetupCalibrationBucket bucket in Buckets)
        {
            if (string.IsNullOrWhiteSpace(bucket.StrategyId) || string.IsNullOrWhiteSpace(bucket.InstrumentGroup) ||
                string.IsNullOrWhiteSpace(bucket.Regime) || bucket.ConfidenceFrom < 0m ||
                bucket.ConfidenceTo <= bucket.ConfidenceFrom || bucket.ConfidenceTo > 100m ||
                bucket.Samples < 0 || bucket.WinRate is < 0m or > 1m || bucket.BrierScore is < 0m or > 1m)
                throw new ArgumentException("A setup calibration bucket is invalid.");
        }
    }
}

public sealed record SetupCalibrationPolicyOptions
{
    public bool Enabled { get; init; }
    public int MinimumSamples { get; init; } = 30;
    public decimal WeakPositiveExpectedRThreshold { get; init; } = 0.20m;
    public decimal WeakPositiveRiskMultiplier { get; init; } = 0.50m;

    public void Validate()
    {
        if (MinimumSamples < 1 || WeakPositiveExpectedRThreshold < 0m ||
            WeakPositiveRiskMultiplier is < 0m or > 1m)
            throw new ArgumentOutOfRangeException(nameof(SetupCalibrationPolicyOptions));
    }
}

public sealed record SetupCalibrationDecision
{
    public required bool Trade { get; init; }
    public required decimal RiskMultiplier { get; init; }
    public required string ReasonCode { get; init; }
    public required string Explanation { get; init; }
    public string? CalibrationId { get; init; }
    public int Samples { get; init; }
    public decimal? ExpectedR { get; init; }
}

public interface ISetupCalibrationPolicy
{
    SetupCalibrationDecision Evaluate(
        string strategyId,
        string instrumentGroup,
        string regime,
        decimal confidence);
}

public sealed class SetupCalibrationPolicy : ISetupCalibrationPolicy
{
    private readonly SetupCalibrationArtifact? _artifact;
    private readonly SetupCalibrationPolicyOptions _options;

    public SetupCalibrationPolicy(
        SetupCalibrationArtifact? artifact = null,
        SetupCalibrationPolicyOptions? options = null,
        string? requiredFeatureSchemaHash = null)
    {
        _options = options ?? new SetupCalibrationPolicyOptions();
        _options.Validate();
        if (_options.Enabled)
        {
            _artifact = artifact ?? throw new ArgumentException("Enabled setup calibration requires an artifact.");
            _artifact.Validate(requiredFeatureSchemaHash);
        }
    }

    public SetupCalibrationDecision Evaluate(
        string strategyId,
        string instrumentGroup,
        string regime,
        decimal confidence)
    {
        if (!_options.Enabled)
            return Decision(true, 1m, "SetupCalibrationDisabled", "Setup calibration is disabled.");
        SetupCalibrationBucket? bucket = _artifact!.Buckets
            .Where(item => string.Equals(item.StrategyId, strategyId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.InstrumentGroup, instrumentGroup, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Regime, regime, StringComparison.OrdinalIgnoreCase) &&
                confidence >= item.ConfidenceFrom && confidence < item.ConfidenceTo)
            .OrderByDescending(item => item.Samples)
            .ThenBy(item => item.ConfidenceFrom)
            .FirstOrDefault();
        if (bucket is null)
            return Decision(true, 1m, "SetupCalibrationBucketUnavailable", "No compatible calibration bucket was available.", _artifact.CalibrationId);
        if (bucket.Samples < _options.MinimumSamples)
            return Decision(true, 1m, "SetupCalibrationSamplesInsufficient", $"Calibration bucket has only {bucket.Samples} samples.", _artifact.CalibrationId, bucket);
        if (bucket.ExpectedR < 0m)
            return Decision(false, 0m, "NegativeCalibratedExpectancy", $"Validated expected R is {bucket.ExpectedR:F3}.", _artifact.CalibrationId, bucket);
        if (bucket.ExpectedR < _options.WeakPositiveExpectedRThreshold)
            return Decision(true, _options.WeakPositiveRiskMultiplier, "WeakCalibratedExpectancy", $"Validated expected R is only {bucket.ExpectedR:F3}; risk was reduced.", _artifact.CalibrationId, bucket);
        return Decision(true, 1m, "StrongCalibratedExpectancy", $"Validated expected R is {bucket.ExpectedR:F3}.", _artifact.CalibrationId, bucket);
    }

    private static SetupCalibrationDecision Decision(
        bool trade,
        decimal multiplier,
        string code,
        string explanation,
        string? calibrationId = null,
        SetupCalibrationBucket? bucket = null) => new()
    {
        Trade = trade,
        RiskMultiplier = Math.Clamp(multiplier, 0m, 1m),
        ReasonCode = code,
        Explanation = explanation,
        CalibrationId = calibrationId,
        Samples = bucket?.Samples ?? 0,
        ExpectedR = bucket?.ExpectedR
    };
}
