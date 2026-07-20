namespace DBManager.Abstractions.Config;

public sealed record CreatePolicyProfile
{
    public required Guid PolicyId { get; init; }
    public required string StrategyId { get; init; }
    public required string StrategyVersion { get; init; }
    public required string FeatureSchemaHash { get; init; }
    public required string CreatedBy { get; init; }
    public string? Description { get; init; }
}

/// <summary>Creates a new revision in <see cref="PolicyRevisionStatus.Research"/>. Immutable once created.</summary>
public sealed record CreatePolicyRevision
{
    public required Guid PolicyRevisionId { get; init; }
    public required Guid PolicyId { get; init; }
    public required int Revision { get; init; }
    public required string ConfigurationHash { get; init; }
    public required string PolicyDocumentJson { get; init; }
    public required string CreatedBy { get; init; }
}

/// <summary>Appends a promotion event and updates the status projection (section 7.2).</summary>
public sealed record PromotePolicyRevision
{
    public required Guid PolicyRevisionId { get; init; }
    public required PolicyRevisionStatus ToStatus { get; init; }
    public required string ActorIdentity { get; init; }
    public string? Reason { get; init; }
}

public sealed record RegisterCalibrationArtifact
{
    public required Guid ArtifactId { get; init; }
    public required ArtifactRole ArtifactType { get; init; }
    public required string StrategyId { get; init; }
    public required string StrategyVersion { get; init; }
    public required string FeatureSchemaHash { get; init; }
    public required string ContentHash { get; init; }
    public required string StorageUri { get; init; }
    public required DateTimeOffset TrainingFrom { get; init; }
    public required DateTimeOffset TrainingTo { get; init; }
    public required DateTimeOffset ValidationFrom { get; init; }
    public required DateTimeOffset ValidationTo { get; init; }
    public DateTimeOffset? TestFrom { get; init; }
    public DateTimeOffset? TestTo { get; init; }
    public required long SampleCount { get; init; }
    public required string MetricsJson { get; init; }
}

public sealed record PromoteCalibrationArtifact
{
    public required Guid ArtifactId { get; init; }
    public required CalibrationArtifactStatus ToStatus { get; init; }
    public required string ActorIdentity { get; init; }
}

/// <summary>Links an artifact to a policy revision, enforcing feature-schema/strategy compatibility.</summary>
public sealed record LinkPolicyArtifact
{
    public required Guid PolicyRevisionId { get; init; }
    public required Guid ArtifactId { get; init; }
    public required ArtifactRole Role { get; init; }
}

/// <summary>The atomic policy-activation workflow (section 13.4): verify, close previous, activate new.</summary>
public sealed record ActivateDeployment
{
    public required Guid DeploymentId { get; init; }
    public required Guid BrokerAccountId { get; init; }
    public required Guid PolicyRevisionId { get; init; }
    public required string HostInstanceId { get; init; }
    public required ExecutionMode ExecutionMode { get; init; }
    public required string StartedBy { get; init; }
    public required string DeploymentHash { get; init; }
}

public sealed record SetDeploymentAssignment
{
    public required Guid DeploymentId { get; init; }
    public required long InstrumentId { get; init; }
    public required string StrategyId { get; init; }
    public required AgentMode AgentMode { get; init; }
    public required bool Enabled { get; init; }
}

/// <summary>How an Agent instance is allowed to act for a given assignment.</summary>
public enum AgentMode
{
    RecordOnly = 0,
    Shadow = 1,
    Live = 2,
    ManualApproval = Live,
    Automatic = 3
}
