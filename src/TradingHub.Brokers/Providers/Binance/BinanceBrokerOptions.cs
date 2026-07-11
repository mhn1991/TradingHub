using TradingHub.Domain.Trading;

namespace TradingHub.Brokers.Binance;

public sealed record BinanceBrokerOptions
{
    public required string BrokerId { get; init; }

    public required BinanceEnvironment Environment { get; init; }

    public required string AccountId { get; init; }

    public required BinanceCredentials Credentials { get; init; }

    public string? LiveAccountConfirmation { get; init; }

    public int ReceiveWindowMilliseconds { get; init; } = 5_000;

    public TradingEnvironment TradingEnvironment => Environment == BinanceEnvironment.Live
        ? TradingEnvironment.Live
        : TradingEnvironment.Demo;

    public Uri RestEndpoint => Environment == BinanceEnvironment.Live
        ? new Uri("https://api.binance.com/")
        : new Uri("https://testnet.binance.vision/");

    public void EnsureValid()
    {
        if (string.IsNullOrWhiteSpace(BrokerId)
            || string.IsNullOrWhiteSpace(AccountId)
            || string.IsNullOrWhiteSpace(Credentials.ApiKey)
            || string.IsNullOrWhiteSpace(Credentials.SecretKey))
        {
            throw new InvalidOperationException("Binance account alias and API credentials are required.");
        }

        if (ReceiveWindowMilliseconds is < 1 or > 60_000)
        {
            throw new InvalidOperationException("Binance receive window must be between 1 and 60000 milliseconds.");
        }
    }
}
