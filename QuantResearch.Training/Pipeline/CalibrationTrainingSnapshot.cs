namespace QuantResearch.Training.Pipeline;

public enum CalibrationStageStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Skipped
}

public enum CalibrationTrainingOverallStatus
{
    Queued,
    Running,
    Completed,
    Failed
}

/// <summary>Status of one of the three pipeline stages, tracked independently so a caller can show real progress.</summary>
public sealed record CalibrationStageSnapshot
{
    public required CalibrationStageStatus Status { get; init; }
    public string? Message { get; init; }
    public Guid? ArtifactId { get; init; }
}

/// <summary>
/// Point-in-time status of one <see cref="CalibrationTrainingPipeline.RunAsync"/> call. Mirrors
/// the shape of <c>Simulator.Models.SimulationJobSnapshot</c> but scoped to the 3-stage
/// calibration pipeline rather than a single backtest.
/// </summary>
public sealed record CalibrationTrainingSnapshot
{
    public required Guid RunId { get; init; }
    public required CalibrationTrainingOverallStatus Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? Message { get; init; }
    public string? FailureReason { get; init; }

    public required CalibrationStageSnapshot SetupStage { get; init; }
    public required CalibrationStageSnapshot MetaModelStage { get; init; }
    public required CalibrationStageSnapshot ManagementStage { get; init; }
}
