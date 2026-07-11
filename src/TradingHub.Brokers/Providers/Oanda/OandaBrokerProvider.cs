using TradingHub.Abstractions.Brokers;
using TradingHub.Abstractions.Time;
using TradingHub.Brokers.Configuration;
using TradingHub.Brokers.Factory;
using TradingHub.Domain.Trading;

namespace TradingHub.Brokers.Oanda;

public sealed class OandaBrokerProvider : IBrokerProvider
{
    public const string Name = "Oanda";
    private const string HttpClientName = "TradingHub.Brokers.Oanda";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IClock _clock;

    public OandaBrokerProvider(IHttpClientFactory httpClientFactory, IClock clock)
    {
        _httpClientFactory = httpClientFactory;
        _clock = clock;
    }

    public string ProviderName => Name;

    public IBrokerGateway Create(
        BrokerDefinition definition,
        BrokerCredentialSet credentials)
    {
        var accessToken = credentials.AccessToken;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException(
                $"Credential '{definition.Account.CredentialKey}' has no OANDA access token.");
        }

        var options = new OandaBrokerOptions
        {
            BrokerId = definition.Id,
            Environment = definition.Environment == TradingEnvironment.Live
                ? OandaEnvironment.Live
                : OandaEnvironment.Practice,
            AccountId = definition.Account.AccountId,
            AccessToken = accessToken,
            LiveAccountConfirmation = definition.Account.LiveAccountConfirmation
        };
        return new OandaBrokerGateway(
            options,
            _httpClientFactory.CreateClient(HttpClientName),
            new BrokerInstrumentMap(definition.CreateInstrumentMap()),
            _clock);
    }
}
