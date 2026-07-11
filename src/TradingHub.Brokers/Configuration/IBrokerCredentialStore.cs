namespace TradingHub.Brokers.Configuration;

public interface IBrokerCredentialStore
{
    ValueTask<BrokerCredentialSet> GetAsync(
        string credentialKey,
        CancellationToken cancellationToken = default);
}
