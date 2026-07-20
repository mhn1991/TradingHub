namespace DBManager.Abstractions.Config;

public enum RuntimeProfileKind : short
{
    Dashboard,
    Simulator,
    LiveHost,
    Research,
    BrokerClient
}

public enum RuntimeProfileRevisionStatus : short
{
    Draft,
    Approved,
    Retired
}

public sealed record CreateRuntimeProfile
{
    public required Guid RuntimeProfileId { get; init; }
    public required RuntimeProfileKind Kind { get; init; }
    public required string Name { get; init; }
    public required string CreatedBy { get; init; }
}

public sealed record CreateRuntimeProfileRevision
{
    public required Guid RuntimeProfileRevisionId { get; init; }
    public required Guid RuntimeProfileId { get; init; }
    public required int Revision { get; init; }
    public required string SettingsJson { get; init; }
    public required string SettingsHash { get; init; }
    public required string CreatedBy { get; init; }
}

public sealed record ApproveRuntimeProfileRevision
{
    public required Guid RuntimeProfileRevisionId { get; init; }
    public required string ApprovedBy { get; init; }
}

public sealed record RuntimeProfileRevisionDetail
{
    public required Guid RuntimeProfileRevisionId { get; init; }
    public required Guid RuntimeProfileId { get; init; }
    public required RuntimeProfileKind Kind { get; init; }
    public required string Name { get; init; }
    public required int Revision { get; init; }
    public required RuntimeProfileRevisionStatus Status { get; init; }
    public required string SettingsJson { get; init; }
    public required string SettingsHash { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string CreatedBy { get; init; }
    public DateTimeOffset? ApprovedAt { get; init; }
    public string? ApprovedBy { get; init; }
}

public interface IRuntimeProfileStore
{
    Task<DurableResult> CreateProfileAsync(CreateRuntimeProfile command, CancellationToken cancellationToken);
    Task<DurableResult> CreateRevisionAsync(CreateRuntimeProfileRevision command, CancellationToken cancellationToken);
    Task<DurableResult> ApproveRevisionAsync(ApproveRuntimeProfileRevision command, CancellationToken cancellationToken);
    Task<RuntimeProfileRevisionDetail?> GetRevisionAsync(Guid revisionId, CancellationToken cancellationToken);
    Task<RuntimeProfileRevisionDetail?> GetApprovedRevisionAsync(
        RuntimeProfileKind kind,
        string name,
        CancellationToken cancellationToken);
}

public sealed record RuntimeConfigurationRequest
{
    public required Guid RuntimeProfileRevisionId { get; init; }
    public Guid? PolicyRevisionId { get; init; }
    public Guid? BrokerEnvironmentId { get; init; }
}

public sealed record ResolvedRuntimeConfiguration
{
    public required RuntimeProfileRevisionDetail RuntimeProfile { get; init; }
    public Guid? PolicyRevisionId { get; init; }
    public Guid? BrokerEnvironmentId { get; init; }
    public Guid? BrokerEndpointRevisionId { get; init; }
    public required string ConfigurationHash { get; init; }
    public required string SnapshotJson { get; init; }
}

public interface IRuntimeConfigurationResolver
{
    Task<ResolvedRuntimeConfiguration> ResolveAsync(
        RuntimeConfigurationRequest request,
        CancellationToken cancellationToken);
}
