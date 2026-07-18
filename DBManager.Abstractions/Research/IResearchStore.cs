namespace DBManager.Abstractions.Research;

public sealed record FoldLineageEntry
{
    public required Guid FoldId { get; init; }
    public required int FoldNumber { get; init; }
    public required TimeSpan PurgeDuration { get; init; }
    public required TimeSpan EmbargoDuration { get; init; }
    public required DateTimeOffset TrainingFrom { get; init; }
    public required DateTimeOffset ValidationTo { get; init; }
}

/// <summary>Research run, fold and calibration-lineage persistence (Phase 6, section 7.9).</summary>
public interface IResearchStore
{
    Task<DurableResult> StartResearchRunAsync(StartResearchRun command, CancellationToken cancellationToken);

    Task<DurableResult> CompleteResearchRunAsync(CompleteResearchRun command, CancellationToken cancellationToken);

    Task<DurableResult> RecordFoldAsync(RecordTimeSeriesFold command, CancellationToken cancellationToken);

    Task<DurableResult> RecordCalibrationRunAsync(RecordCalibrationRun command, CancellationToken cancellationToken);

    /// <summary>Section 30 Phase 6 acceptance: "LEAK-01 controls and fold lineage are queryable."</summary>
    Task<IReadOnlyList<FoldLineageEntry>> GetFoldLineageAsync(Guid researchRunId, CancellationToken cancellationToken);
}
