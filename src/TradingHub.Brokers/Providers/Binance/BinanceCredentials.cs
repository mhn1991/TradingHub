namespace TradingHub.Brokers.Binance;

public sealed record BinanceCredentials
{
    public required string ApiKey { get; init; }

    public required string SecretKey { get; init; }
}
