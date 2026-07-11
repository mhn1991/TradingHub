namespace Brokers;

public interface IBrokerCredentialStore
{
    ValueTask<BrokerCredentials?> GetAsync(
        string credentialReference,
        CancellationToken cancellationToken = default);
}
