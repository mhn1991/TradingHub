namespace TradingHub.Brokers.Configuration;

public sealed class EnvironmentBrokerCredentialStore : IBrokerCredentialStore
{
    public ValueTask<BrokerCredentialSet> GetAsync(
        string credentialKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(credentialKey))
        {
            throw new ArgumentException("A credential key is required.", nameof(credentialKey));
        }

        return ValueTask.FromResult(new BrokerCredentialSet
        {
            AccessToken = Read(credentialKey, "ACCESS_TOKEN"),
            ApiKey = Read(credentialKey, "API_KEY"),
            SecretKey = Read(credentialKey, "SECRET_KEY")
        });
    }

    private static string? Read(string credentialKey, string field)
    {
        return Environment.GetEnvironmentVariable($"{credentialKey}__{field}");
    }
}
