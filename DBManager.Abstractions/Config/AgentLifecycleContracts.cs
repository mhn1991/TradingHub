using DBManager.Abstractions;

namespace DBManager.Abstractions.Config;

public sealed record Page<T>(IReadOnlyList<T> Items, int Offset, int Limit, long Total);

public sealed record PolicySummary(
    Guid PolicyId, string StrategyId, string StrategyVersion, DateTimeOffset CreatedAt, string? Description);

public sealed record PolicyRevisionSummary(
    Guid PolicyRevisionId, Guid PolicyId, int Revision, PolicyRevisionStatus Status,
    string ConfigurationHash, DateTimeOffset CreatedAt);

public sealed record PolicyPermissionDetail(
    Guid PolicyRevisionId, string BrokerEnvironment, Guid? BrokerAccountId,
    PolicyExecutionPermission EffectivePermissions, IReadOnlyList<PolicyPermissionEvent> Events);

public sealed record PolicyPermissionEvent(
    Guid PermissionEventId, PolicyExecutionPermission Permissions, bool Granted,
    DateTimeOffset OccurredAt, string ActorIdentity, string Reason, string ConfigurationHash);

public sealed record ParityCertificationDetail
{
    public required Guid CertificationId { get; init; }
    public required Guid PolicyRevisionId { get; init; }
    public required string ConfigurationHash { get; init; }
    public required string SourceCommit { get; init; }
    public required string RecordingHash { get; init; }
    public required string SimulatorBuildHash { get; init; }
    public required string LiveBuildHash { get; init; }
    public required int ComparedEpochCount { get; init; }
    public required int MismatchCount { get; init; }
    public required string MismatchDetailsJson { get; init; }
    public required ParityCertificationStatus Status { get; init; }
    public required DateTimeOffset CertifiedAt { get; init; }
    public required string CertifiedBy { get; init; }
}

public sealed record DeploymentSummary(
    Guid DeploymentId, Guid BrokerAccountId, string Environment, string HostInstanceId,
    DeploymentStatus Status, long Version, DateTimeOffset RequestedAt, string RequestedBy);

public sealed record DeploymentAgentDetail
{
    public required Guid DeploymentAgentId { get; init; }
    public required Guid DeploymentId { get; init; }
    public required Guid BrokerAccountId { get; init; }
    public required Guid PolicyRevisionId { get; init; }
    public required long InstrumentId { get; init; }
    public required string StrategyId { get; init; }
    public required AgentMode AgentMode { get; init; }
    public required DeploymentAgentStatus Status { get; init; }
    public required bool Enabled { get; init; }
    public required string PackageHash { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? StoppedAt { get; init; }
    public string? FaultCode { get; init; }
    public string? FaultMessage { get; init; }
}

public sealed record DeploymentDetail(
    DeploymentSummary Deployment,
    IReadOnlyList<DeploymentAgentDetail> Agents,
    IReadOnlyList<DeploymentEventDetail> RecentEvents);

public sealed record DeploymentEventDetail(
    long EventId, Guid DeploymentId, Guid? DeploymentAgentId, string EventType,
    DateTimeOffset OccurredAt, string ActorIdentity, string CorrelationId,
    string? ReasonCode, string? DetailJson);

public sealed record CreateDeploymentRequest
{
    public required Guid DeploymentId { get; init; }
    public required Guid BrokerAccountId { get; init; }
    public required string Environment { get; init; }
    public required string HostInstanceId { get; init; }
    public required string RequestedBy { get; init; }
    public required string DeploymentHash { get; init; }
    public required string IdempotencyKey { get; init; }
}

public sealed record AddDeploymentAgentRequest
{
    public required Guid DeploymentAgentId { get; init; }
    public required Guid DeploymentId { get; init; }
    public required Guid PolicyRevisionId { get; init; }
    public required long InstrumentId { get; init; }
    public required string StrategyId { get; init; }
    public required AgentMode AgentMode { get; init; }
    public required string PackageHash { get; init; }
    public required string RequestedBy { get; init; }
    public bool Enabled { get; init; } = true;
    public long? ExpectedVersion { get; init; }
}

public sealed record EnqueueDeploymentCommandRequest
{
    public required Guid CommandId { get; init; }
    public required Guid DeploymentId { get; init; }
    public Guid? DeploymentAgentId { get; init; }
    public required DeploymentCommandType CommandType { get; init; }
    public required string RequestedBy { get; init; }
    public required long ExpectedVersion { get; init; }
    public string PayloadJson { get; init; } = "{}";
    public required string IdempotencyKey { get; init; }
}

public sealed record ClaimedDeploymentCommand(
    Guid CommandId, Guid DeploymentId, Guid? DeploymentAgentId, DeploymentCommandType CommandType,
    string RequestedBy, long ExpectedVersion, string PayloadJson, string IdempotencyKey);

public interface IAgentLifecycleStore
{
    Task<Page<PolicySummary>> ListPoliciesAsync(int offset, int limit, CancellationToken cancellationToken = default);
    Task<Page<PolicyRevisionSummary>> ListPolicyRevisionsAsync(Guid? policyId, int offset, int limit,
        CancellationToken cancellationToken = default);
    Task<PolicyPermissionDetail> GetPermissionsAsync(Guid policyRevisionId, string brokerEnvironment,
        Guid? brokerAccountId, CancellationToken cancellationToken = default);
    Task<DurableResult> AppendPermissionAsync(Guid permissionEventId, Guid policyRevisionId,
        string brokerEnvironment, Guid? brokerAccountId, PolicyExecutionPermission permissions, bool granted,
        string configurationHash, string actorIdentity, string reason, CancellationToken cancellationToken = default);
    Task<ParityCertificationDetail?> GetLatestParityCertificationAsync(Guid policyRevisionId,
        CancellationToken cancellationToken = default);
    Task<DurableResult> StoreParityCertificationAsync(ParityCertificationDetail certification,
        CancellationToken cancellationToken = default);
    Task<DurableResult> CreateDeploymentAsync(CreateDeploymentRequest request,
        CancellationToken cancellationToken = default);
    Task<DurableResult> AddDeploymentAgentAsync(AddDeploymentAgentRequest request,
        CancellationToken cancellationToken = default);
    Task<DurableResult> EnqueueCommandAsync(EnqueueDeploymentCommandRequest request,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ClaimedDeploymentCommand>> ClaimPendingCommandsAsync(string hostInstanceId, int take,
        CancellationToken cancellationToken = default);
    Task<DurableResult> CompleteCommandAsync(Guid commandId, string hostInstanceId, bool succeeded,
        string? errorCode, string? errorMessage, CancellationToken cancellationToken = default);
    Task<DurableResult> TransitionDeploymentAsync(Guid deploymentId, long expectedVersion,
        DeploymentStatus status, string actorIdentity, string correlationId, string? reasonCode,
        CancellationToken cancellationToken = default);
    Task<DurableResult> TransitionDeploymentAgentAsync(Guid deploymentAgentId,
        DeploymentAgentStatus status, bool enabled, string actorIdentity, string correlationId,
        string? faultCode = null, string? faultMessage = null,
        CancellationToken cancellationToken = default);
    Task<DurableResult> AppendDeploymentEventAsync(Guid deploymentId, Guid? deploymentAgentId,
        string eventType, string actorIdentity, string correlationId, string? reasonCode, string? detailJson,
        CancellationToken cancellationToken = default);
    Task<DeploymentDetail?> GetDeploymentAsync(Guid deploymentId, CancellationToken cancellationToken = default);
    Task<Page<DeploymentSummary>> ListDeploymentsAsync(int offset, int limit,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeploymentAgentDetail>> ListDeploymentAgentsAsync(Guid deploymentId,
        CancellationToken cancellationToken = default);
    Task<DeploymentAgentDetail?> GetDeploymentAgentAsync(Guid deploymentAgentId,
        CancellationToken cancellationToken = default);
}
