using DBManager.Abstractions.Config;

namespace DBManager.Abstractions.Research;

public sealed record StartResearchRun
{
    public required Guid ResearchRunId { get; init; }
    public required ResearchRunType RunType { get; init; }
    public required string StrategyId { get; init; }
    public required string StrategyVersion { get; init; }
    public required string ConfigurationHash { get; init; }
    public required string DatasetHash { get; init; }
    public long? RandomSeed { get; init; }
    public required string ParametersJson { get; init; }
}

public sealed record CompleteResearchRun
{
    public required Guid ResearchRunId { get; init; }
    public required ResearchRunStatus Status { get; init; }
    public required string SummaryMetricsJson { get; init; }
    public string? FailureReason { get; init; }
}

/// <summary>
/// <see cref="PurgeDuration"/>/<see cref="EmbargoDuration"/> are the LEAK-01 leakage-prevention
/// controls (section 30 Phase 6 acceptance: "LEAK-01 controls ... are queryable").
/// </summary>
public sealed record RecordTimeSeriesFold
{
    public required Guid FoldId { get; init; }
    public required Guid ResearchRunId { get; init; }
    public required int FoldNumber { get; init; }
    public required DateTimeOffset TrainingFrom { get; init; }
    public required DateTimeOffset TrainingTo { get; init; }
    public required DateTimeOffset ValidationFrom { get; init; }
    public required DateTimeOffset ValidationTo { get; init; }
    public DateTimeOffset? TestFrom { get; init; }
    public DateTimeOffset? TestTo { get; init; }
    public required TimeSpan PurgeDuration { get; init; }
    public required TimeSpan EmbargoDuration { get; init; }
    public required string SampleCountsJson { get; init; }
    public required string MetricsJson { get; init; }
}

public sealed record RecordCalibrationRun
{
    public required Guid CalibrationRunId { get; init; }
    public required Guid ResearchRunId { get; init; }
    public required ArtifactRole Stage { get; init; }
    public required string InputArtifactIdsJson { get; init; }
    public Guid? OutputArtifactId { get; init; }
    public required ResearchRunStatus Status { get; init; }
    public required string MetricsJson { get; init; }
}
