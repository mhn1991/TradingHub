using TradingHub.Domain.Trading;

namespace TradingHub.Brokers.Oanda;

public sealed record OandaBrokerOptions
{
    public required string BrokerId { get; init; }

    public required OandaEnvironment Environment { get; init; }

    public required string AccountId { get; init; }

    public required string AccessToken { get; init; }

    public string? LiveAccountConfirmation { get; init; }

    public TradingEnvironment TradingEnvironment => Environment == OandaEnvironment.Live
        ? TradingEnvironment.Live
        : TradingEnvironment.Demo;

    public Uri RestEndpoint => Environment == OandaEnvironment.Live
        ? new Uri("https://api-fxtrade.oanda.com/")
        : new Uri("https://api-fxpractice.oanda.com/");

    public Uri StreamingEndpoint => Environment == OandaEnvironment.Live
        ? new Uri("https://stream-fxtrade.oanda.com/")
        : new Uri("https://stream-fxpractice.oanda.com/");

    public void EnsureValid()
    {
        if (string.IsNullOrWhiteSpace(BrokerId)
            || string.IsNullOrWhiteSpace(AccountId)
            || string.IsNullOrWhiteSpace(AccessToken))
        {
            throw new InvalidOperationException("OANDA account ID and access token are required.");
        }
    }
}
