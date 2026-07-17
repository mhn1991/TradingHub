namespace QuantResearch.Training.Pipeline;

/// <summary>
/// Final outcome of a <see cref="CalibrationTrainingPipeline.RunAsync"/> call. On success all
/// three artifact ids are set; on partial/total failure only the stages that actually completed
/// have an id, and <see cref="FailureReason"/> explains why the run stopped. A partial result is
/// never a promotable bundle candidate - only <see cref="Success"/> runs are (Phase 2 wraps
/// those into a <c>CalibrationBundleCandidate</c>).
/// </summary>
public sealed record CalibrationTrainingResult
{
    public required Guid RunId { get; init; }
    public required bool Success { get; init; }
    public string? FailureReason { get; init; }

    public Guid? SetupArtifactId { get; init; }
    public Guid? MetaModelArtifactId { get; init; }
    public Guid? ManagementArtifactId { get; init; }

    public required CalibrationTrainingSnapshot FinalSnapshot { get; init; }
}
