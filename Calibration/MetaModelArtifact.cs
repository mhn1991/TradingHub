namespace Simulator.Calibration;

public sealed record MetaModelBucket
{
    public required string StrategyId { get; init; }
    public required string Regime { get; init; }
    public required decimal ConfidenceFrom { get; init; }
    public required decimal ConfidenceTo { get; init; }
    /// <summary>Multi-timeframe alignment bucket bounds, in [0, 1].</summary>
    public required decimal AlignmentFrom { get; init; }
    public required decimal AlignmentTo { get; init; }
    public required int Samples { get; init; }
    public required decimal WinRate { get; init; }
    public required decimal BrierScore { get; init; }
    public required decimal ExpectedR { get; init; }
}

/// <summary>
/// A calibration-cohort-driven meta-model artifact (not a trained ML model - see the
/// 2026-07-16 audit §14-17 pass's decision to keep meta-labeling deterministic and
/// consistent with <see cref="RiskManager.Calibration.SetupCalibrationArtifact"/>'s pattern
/// rather than introduce an ML framework).
/// </summary>
public sealed record MetaModelArtifact
{
    public required int SchemaVersion { get; init; }
    public required string CalibrationId { get; init; }
    public required string ModelVersion { get; init; }
    /// <summary>Must equal <see cref="RiskManager.Calibration.MetaLabelFeatureFactory.SchemaVersion"/>.</summary>
    public required string FeatureSchemaHash { get; init; }
    public required DateTimeOffset TrainingFrom { get; init; }
    public required DateTimeOffset TrainingTo { get; init; }
    public required string DataHash { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required IReadOnlyList<MetaModelBucket> Buckets { get; init; }

    public void Validate(string? requiredFeatureSchemaHash = null)
    {
        if (SchemaVersion != 1 || string.IsNullOrWhiteSpace(CalibrationId) ||
            string.IsNullOrWhiteSpace(ModelVersion) || string.IsNullOrWhiteSpace(FeatureSchemaHash) ||
            TrainingFrom >= TrainingTo || string.IsNullOrWhiteSpace(DataHash) || Buckets is null ||
            requiredFeatureSchemaHash is not null &&
            !string.Equals(requiredFeatureSchemaHash, FeatureSchemaHash, StringComparison.Ordinal))
            throw new ArgumentException("The meta-model artifact is missing, incompatible, or invalid.");

        foreach (MetaModelBucket bucket in Buckets)
        {
            if (string.IsNullOrWhiteSpace(bucket.StrategyId) || string.IsNullOrWhiteSpace(bucket.Regime) ||
                bucket.ConfidenceFrom < 0m || bucket.ConfidenceTo <= bucket.ConfidenceFrom || bucket.ConfidenceTo > 100m ||
                bucket.AlignmentFrom < 0m || bucket.AlignmentTo <= bucket.AlignmentFrom || bucket.AlignmentTo > 1m ||
                bucket.Samples < 0 || bucket.WinRate is < 0m or > 1m || bucket.BrierScore is < 0m or > 1m)
                throw new ArgumentException("A meta-model bucket is invalid.");
        }
    }
}

public sealed record MetaModelPolicyOptions
{
    public bool Enabled { get; init; }
    public int MinimumSamples { get; init; } = 30;
    public decimal WeakPositiveExpectedRThreshold { get; init; } = 0.20m;
    /// <summary>Never exceeds 1.0 by construction - a weak signal only ever shrinks size.</summary>
    public decimal WeakPositiveRiskMultiplier { get; init; } = 0.50m;

    public void Validate()
    {
        if (MinimumSamples < 1)
            throw new ArgumentOutOfRangeException(nameof(MinimumSamples));
        if (WeakPositiveRiskMultiplier is <= 0m or > 1m)
            throw new ArgumentOutOfRangeException(
                nameof(WeakPositiveRiskMultiplier), "Must be in (0, 1] - a meta-model can only reduce risk, never increase it.");
    }
}
