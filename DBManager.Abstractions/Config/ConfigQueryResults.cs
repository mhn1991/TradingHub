namespace DBManager.Abstractions.Config;

/// <summary>
/// The exact, immutable content of one promoted revision. There is deliberately no "latest
/// revision" query anywhere in <see cref="IPolicyConfigurationStore"/> — callers must always name
/// an exact <see cref="PolicyRevisionId"/> (section 30 Phase 1 acceptance: no automatic
/// "latest artifact" selection).
/// </summary>
public sealed record PolicyRevisionDetail
{
    public required Guid PolicyRevisionId { get; init; }
    public required Guid PolicyId { get; init; }
    public required int Revision { get; init; }
    public required string ConfigurationHash { get; init; }
    public required PolicyRevisionStatus Status { get; init; }
    public required string PolicyDocumentJson { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string CreatedBy { get; init; }
    public DateTimeOffset? ApprovedAt { get; init; }
    public string? ApprovedBy { get; init; }
    public DateTimeOffset? RetiredAt { get; init; }
    public required IReadOnlyList<PolicyArtifactLink> Artifacts { get; init; }
}

public sealed record PolicyArtifactLink
{
    public required Guid ArtifactId { get; init; }
    public required ArtifactRole Role { get; init; }
    public required string ContentHash { get; init; }
    public required string StorageUri { get; init; }
    public required CalibrationArtifactStatus Status { get; init; }
}

public sealed record ActiveDeploymentDetail
{
    public required Guid DeploymentId { get; init; }
    public required Guid BrokerAccountId { get; init; }
    public required Guid PolicyRevisionId { get; init; }
    public required string HostInstanceId { get; init; }
    public required ExecutionMode ExecutionMode { get; init; }
    public required DeploymentStatus Status { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
}
