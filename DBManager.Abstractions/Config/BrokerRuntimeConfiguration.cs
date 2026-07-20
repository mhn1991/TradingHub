using DBManager.Abstractions.Credentials;

namespace DBManager.Abstractions.Config;

public enum BrokerEndpointKind : short
{
    Rest,
    Streaming,
    MarketData
}

public sealed record BrokerEnvironmentDetail
{
    public required Guid BrokerEnvironmentId { get; init; }
    public required long BrokerId { get; init; }
    public required string BrokerCode { get; init; }
    public required string EnvironmentCode { get; init; }
    public required string DisplayName { get; init; }
    public required bool IsLive { get; init; }
    public required bool Enabled { get; init; }
}

public sealed record BrokerEndpointRevisionDetail
{
    public required Guid BrokerEndpointRevisionId { get; init; }
    public required Guid BrokerEnvironmentId { get; init; }
    public required BrokerEndpointKind Kind { get; init; }
    public required int Revision { get; init; }
    public required Uri BaseAddress { get; init; }
    public required bool Active { get; init; }
    public required string ContentHash { get; init; }
}

public sealed record BrokerAccountSettingsDetail
{
    public required Guid BrokerAccountSettingsRevisionId { get; init; }
    public required Guid BrokerEnvironmentId { get; init; }
    public Guid? BrokerAccountId { get; init; }
    public required int Revision { get; init; }
    public required string AccountAlias { get; init; }
    public string? ExternalAccountId { get; init; }
    public required string AccountCurrency { get; init; }
    public required string SettingsJson { get; init; }
    public required bool Active { get; init; }
}

public sealed record ResolvedBrokerRuntimeConfiguration
{
    public required BrokerEnvironmentDetail Environment { get; init; }
    public required IReadOnlyList<BrokerEndpointRevisionDetail> Endpoints { get; init; }
    public BrokerAccountSettingsDetail? Account { get; init; }
    public required IReadOnlyList<SecretReference> CredentialReferences { get; init; }
}

public interface IBrokerRuntimeConfigurationStore
{
    Task<ResolvedBrokerRuntimeConfiguration?> GetActiveAsync(
        string brokerCode,
        string environmentCode,
        CancellationToken cancellationToken);
}
