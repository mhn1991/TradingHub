using Simulator.Calibration;

namespace Simulator.Experiments.IndicatorCalibration;

/// <summary>Lifecycle state of one calibration run as tracked by <see cref="IIndicatorCalibrationApplicationService"/> (blueprint §17.1).</summary>
public enum IndicatorCalibrationRunState
{
    Queued,
    Running,
    Paused,
    Completed,
    Failed,
    Cancelled
}

/// <summary>Lightweight, list-friendly view of one calibration run.</summary>
public sealed record IndicatorCalibrationRunSummary
{
    public required string CalibrationId { get; init; }
    public required string StrategyId { get; init; }
    public required string Instrument { get; init; }
    public required IndicatorCalibrationRunState State { get; init; }
    public CalibrationOutcome? Outcome { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? FailureReason { get; init; }
    public Guid? ArtifactId { get; init; }
}

/// <summary>Full detail view of one calibration run, including the request that started it and (once complete) its result/artifact.</summary>
public sealed record IndicatorCalibrationRunDetails
{
    public required IndicatorCalibrationRunSummary Summary { get; init; }
    public required IndicatorCalibrationRequest Request { get; init; }
    public IndicatorCalibrationOrchestrationResult? Result { get; init; }
    public IndicatorCalibrationArtifact? Artifact { get; init; }
}

/// <summary>
/// One artifact awaiting review, paired with its storage id - <see cref="IndicatorCalibrationArtifact"/>
/// itself never carries the repository-assigned <see cref="Guid"/> it was stored under (that id is
/// a property of the storage envelope, not the payload), so a caller reviewing a list of these has
/// no way to actually reference one for approval without this pairing.
/// </summary>
public sealed record IndicatorCalibrationPendingApproval
{
    public required Guid ArtifactId { get; init; }
    public required IndicatorCalibrationArtifact Artifact { get; init; }
}

/// <summary>
/// Explicit human approval input (blueprint §16). Only an artifact whose
/// <see cref="IndicatorCalibrationArtifact.Outcome"/> is <see cref="CalibrationOutcome.Improved"/>
/// may be approved - <see cref="IIndicatorCalibrationApplicationService.ApproveAsync"/> enforces this.
/// Exactly one of <see cref="CalibrationId"/> (resolved through this process's in-memory run
/// tracking) or <see cref="ArtifactId"/> (resolved directly against the artifact repository,
/// working regardless of whether the run that produced it is still tracked anywhere - e.g. a
/// prior night's unattended calibration) must be supplied.
/// </summary>
public sealed record IndicatorCalibrationApprovalRequest
{
    public string? CalibrationId { get; init; }
    public Guid? ArtifactId { get; init; }
    public required string ApprovedBy { get; init; }
    public required string TargetEnvironment { get; init; }
    public required string ReviewNotes { get; init; }
    public string? ReplacesArtifactId { get; init; }
}

/// <summary>Same "exactly one of CalibrationId or ArtifactId" contract as <see cref="IndicatorCalibrationApprovalRequest"/>.</summary>
public sealed record IndicatorCalibrationRejectionRequest
{
    public string? CalibrationId { get; init; }
    public Guid? ArtifactId { get; init; }
    public required string RejectedBy { get; init; }
    public required string Reason { get; init; }
}
