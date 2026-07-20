namespace DBManager.Abstractions.Credentials;

/// <summary>
/// Decrypted broker credentials. Instances must never be logged or persisted outside the
/// encrypted credential vault.
/// </summary>
public sealed record BrokerCredential
{
    public required string BrokerCode { get; init; }
    public required string Environment { get; init; }
    public bool Enabled { get; init; } = true;
    public string? AccountId { get; init; }
    public string? AccessToken { get; init; }
    public string? ApiKey { get; init; }
    public string? SecretKey { get; init; }
    public string? Identifier { get; init; }
    public string? Password { get; init; }
    public string? BaseAddress { get; init; }
}

public interface IBrokerCredentialStore
{
    Task<BrokerCredential?> GetAsync(
        string brokerCode,
        string environment,
        bool includeDisabled = false,
        CancellationToken cancellationToken = default);

    Task UpsertAsync(BrokerCredential credential, CancellationToken cancellationToken = default);
}
