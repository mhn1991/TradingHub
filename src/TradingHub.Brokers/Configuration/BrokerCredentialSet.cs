namespace TradingHub.Brokers.Configuration;

public sealed record BrokerCredentialSet
{
    public string? AccessToken { get; init; }

    public string? ApiKey { get; init; }

    public string? SecretKey { get; init; }
}
