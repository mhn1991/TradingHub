namespace Brokers;

/// <summary>
/// Persistence-friendly broker row. SQLite can hydrate these scalar properties directly.
/// Secrets are referenced by key and resolved before the broker instance is returned.
/// </summary>
public sealed class BrokerConfiguration
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public BrokerProvider Provider { get; init; }

    public BrokerEnvironment Environment { get; init; }

    /// <summary>
    /// Optional absolute override. When omitted, the official endpoint for the provider/environment is used.
    /// </summary>
    public string? RestBaseUrl { get; init; }

    /// <summary>
    /// Optional absolute streaming endpoint override retained on the broker instance for later streaming clients.
    /// </summary>
    public string? StreamingBaseUrl { get; init; }

    public string? AccountId { get; init; }

    public string? CredentialReference { get; init; }

    public CandlePriceBasis CandlePriceBasis { get; init; } = CandlePriceBasis.ProviderDefault;

    public int DefaultCandleLimit { get; init; } = 500;

    public int RequestTimeoutMilliseconds { get; init; } = 30_000;
}
