namespace Brokers.Tests.TestDoubles;

internal sealed class StubCredentialStore(BrokerCredentials? credentials) : IBrokerCredentialStore
{
    public string? RequestedReference { get; private set; }

    public ValueTask<BrokerCredentials?> GetAsync(
        string credentialReference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequestedReference = credentialReference;
        return ValueTask.FromResult(credentials);
    }
}
