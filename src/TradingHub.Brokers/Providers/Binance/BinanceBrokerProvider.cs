using System.Globalization;
using TradingHub.Abstractions.Brokers;
using TradingHub.Abstractions.Time;
using TradingHub.Brokers.Configuration;
using TradingHub.Brokers.Factory;
using TradingHub.Domain.Trading;

namespace TradingHub.Brokers.Binance;

public sealed class BinanceBrokerProvider : IBrokerProvider
{
    public const string Name = "Binance";
    private const string HttpClientName = "TradingHub.Brokers.Binance";
    private const int DefaultReceiveWindow = 5_000;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IClock _clock;

    public BinanceBrokerProvider(IHttpClientFactory httpClientFactory, IClock clock)
    {
        _httpClientFactory = httpClientFactory;
        _clock = clock;
    }

    public string ProviderName => Name;

    public IBrokerGateway Create(
        BrokerDefinition definition,
        BrokerCredentialSet credentials)
    {
        EnsureCredentials(definition, credentials);
        var options = new BinanceBrokerOptions
        {
            BrokerId = definition.Id,
            Environment = definition.Environment == TradingEnvironment.Live
                ? BinanceEnvironment.Live
                : BinanceEnvironment.SpotTestnet,
            AccountId = definition.Account.AccountId,
            Credentials = new BinanceCredentials
            {
                ApiKey = credentials.ApiKey!,
                SecretKey = credentials.SecretKey!
            },
            LiveAccountConfirmation = definition.Account.LiveAccountConfirmation,
            ReceiveWindowMilliseconds = ReadReceiveWindow(definition.Account.Settings)
        };
        return new BinanceBrokerGateway(
            options,
            _httpClientFactory.CreateClient(HttpClientName),
            new BrokerInstrumentMap(definition.CreateInstrumentMap()),
            _clock);
    }

    private static void EnsureCredentials(
        BrokerDefinition definition,
        BrokerCredentialSet credentials)
    {
        if (string.IsNullOrWhiteSpace(credentials.ApiKey)
            || string.IsNullOrWhiteSpace(credentials.SecretKey))
        {
            throw new InvalidOperationException(
                $"Credential '{definition.Account.CredentialKey}' needs a Binance API and secret key.");
        }
    }

    private static int ReadReceiveWindow(IReadOnlyDictionary<string, string> settings)
    {
        if (!settings.TryGetValue("ReceiveWindowMilliseconds", out var configured))
        {
            return DefaultReceiveWindow;
        }

        return int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : throw new InvalidOperationException("Binance receive window must be an integer.");
    }
}
