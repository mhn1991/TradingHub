namespace Brokers;

/// <summary>
/// Runtime credentials. They are deliberately separate from the SQLite-friendly broker configuration.
/// </summary>
public sealed class BrokerCredentials
{
    public string? ApiKey { get; init; }

    public string? SecretKey { get; init; }

    public string? AccessToken { get; init; }

    public static BrokerCredentials None { get; } = new();
}
