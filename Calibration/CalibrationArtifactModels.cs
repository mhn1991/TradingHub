namespace Simulator.Calibration;

public enum CalibrationArtifactType
{
    Setup,
    Management,
    MetaModel,
    /// <summary>Blueprint §4.3: per-instrument calibrated indicator-parameter/threshold overlay artifacts.</summary>
    IndicatorParameters
}

/// <summary>
/// Review status of an artifact produced by <c>QuantResearch.Training</c>'s automated pipeline.
/// A freshly-trained artifact always starts <see cref="PendingReview"/> - nothing in this
/// repository ever auto-approves an artifact for live use; that only happens through the
/// bundle-candidate approval workflow.
/// </summary>
public enum CalibrationPromotionStatus
{
    PendingReview,
    Approved,
    Rejected,
    Superseded
}

/// <summary>
/// Chronological train/test boundary for one purged cross-validation fold, recorded for audit
/// so a reviewer can see exactly what data windows contributed to a bucket's out-of-fold stats.
/// </summary>
public sealed record CalibrationFoldSummary
{
    public required int Fold { get; init; }
    public required DateTimeOffset TrainingFrom { get; init; }
    public required DateTimeOffset TrainingTo { get; init; }
    public required DateTimeOffset TestFrom { get; init; }
    public required DateTimeOffset TestTo { get; init; }
    public required int TrainingSamples { get; init; }
    public required int TestSamples { get; init; }
}

/// <summary>
/// Aggregate out-of-fold (<see cref="CalibrationArtifactMetadata.ValidationMetrics"/>) or
/// untouched-test-window (<see cref="CalibrationArtifactMetadata.TestMetrics"/>) statistics for
/// a training run. <see cref="Extra"/> carries stage-specific figures (e.g. risk-multiplier
/// bounds checks) without needing a bespoke type per calibration kind.
/// </summary>
public sealed record CalibrationValidationReport
{
    public required int SampleCount { get; init; }
    public decimal? BrierScore { get; init; }
    public decimal? MeanExpectedR { get; init; }
    public decimal? WinRate { get; init; }
    public IReadOnlyDictionary<string, decimal> Extra { get; init; } = new Dictionary<string, decimal>();
}

public sealed record CalibrationArtifactMetadata
{
    public required Guid Id { get; init; }
    public required CalibrationArtifactType Type { get; init; }
    public required int SchemaVersion { get; init; }
    public required string CalibrationId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string ContentHash { get; init; }
    public string? Description { get; init; }

    /// <summary>Purged cross-validation fold boundaries used to fit this artifact's out-of-fold statistics.</summary>
    public IReadOnlyList<CalibrationFoldSummary> Folds { get; init; } = [];
    public TimeSpan Embargo { get; init; }
    /// <summary>Final holdout window, evaluated exactly once, never fed back into bucket selection.</summary>
    public DateTimeOffset? TestWindowFrom { get; init; }
    public DateTimeOffset? TestWindowTo { get; init; }
    /// <summary>Aggregate statistics across all out-of-fold evaluation folds.</summary>
    public CalibrationValidationReport? ValidationMetrics { get; init; }
    /// <summary>Statistics from the single, untouched final test window.</summary>
    public CalibrationValidationReport? TestMetrics { get; init; }
    /// <summary>The artifact (if any) this one was trained to replace, for retraining lineage.</summary>
    public Guid? SupersedesArtifactId { get; init; }
    public CalibrationPromotionStatus PromotionStatus { get; init; } = CalibrationPromotionStatus.PendingReview;
}

/// <summary>
/// Optional provenance bundle accepted by <see cref="ICalibrationArtifactRepository"/>'s
/// <c>Store*Async</c> methods. Left null/default by callers that don't do leakage-safe
/// cross-validation (e.g. artifacts imported from elsewhere) - such artifacts simply carry no
/// fold/validation metadata, which is honest since none was computed.
/// </summary>
public sealed record CalibrationArtifactProvenance
{
    public IReadOnlyList<CalibrationFoldSummary> Folds { get; init; } = [];
    public TimeSpan Embargo { get; init; }
    public DateTimeOffset? TestWindowFrom { get; init; }
    public DateTimeOffset? TestWindowTo { get; init; }
    public CalibrationValidationReport? ValidationMetrics { get; init; }
    public CalibrationValidationReport? TestMetrics { get; init; }
    public Guid? SupersedesArtifactId { get; init; }
}
