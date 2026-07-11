using TradingHub.Brokers.Configuration;

namespace TradingHub.Simulation.Tests.TestDoubles;

internal sealed class InMemoryBrokerCredentialStore : IBrokerCredentialStore
{
    private readonly IReadOnlyDictionary<string, BrokerCredentialSet> _credentials;

    public InMemoryBrokerCredentialStore(IReadOnlyDictionary<string, BrokerCredentialSet> credentials)
    {
        _credentials = credentials;
    }

    public ValueTask<BrokerCredentialSet> GetAsync(
        string credentialKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _credentials.TryGetValue(credentialKey, out var credentials)
            ? ValueTask.FromResult(credentials)
            : ValueTask.FromException<BrokerCredentialSet>(
                new KeyNotFoundException($"Credential '{credentialKey}' was not found."));
    }
}
