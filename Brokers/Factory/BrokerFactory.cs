using Networking;

namespace Brokers;

/// <summary>
/// Creates a fully configured broker instance from persistence records and resolved credentials.
/// </summary>
public sealed class BrokerFactory : IBrokerFactory
{
    private readonly INetworkDispatcher _networkDispatcher;
    private readonly IBrokerCredentialStore? _credentialStore;
    private readonly TimeProvider _timeProvider;

    public BrokerFactory(
        INetworkDispatcher networkDispatcher,
        IBrokerCredentialStore? credentialStore = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(networkDispatcher);
        _networkDispatcher = networkDispatcher;
        _credentialStore = credentialStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public IBroker Create(
        BrokerConfiguration configuration,
        IEnumerable<BrokerInstrumentConfiguration> instruments,
        BrokerCredentials? credentials = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(instruments);

        BrokerInstrumentConfiguration[] instrumentArray = instruments.ToArray();
        ValidateConfiguration(configuration, instrumentArray, credentials ?? BrokerCredentials.None);

        Uri restEndpoint = BrokerEndpointCatalog.GetRestEndpoint(configuration);
        Uri? streamingEndpoint = BrokerEndpointCatalog.GetStreamingEndpoint(configuration);
        CandlePriceBasis priceBasis = ResolvePriceBasis(configuration);
        var runtime = new BrokerRuntimeConfiguration(
            configuration,
            instrumentArray,
            credentials ?? BrokerCredentials.None,
            restEndpoint,
            streamingEndpoint,
            priceBasis);

        IBrokerProviderAdapter adapter = configuration.Provider switch
        {
            BrokerProvider.BinanceSpot => new BinanceBrokerAdapter(runtime, _timeProvider),
            BrokerProvider.OandaV20 => new OandaBrokerAdapter(runtime),
            _ => throw new BrokerConfigurationException(
                $"Broker provider '{configuration.Provider}' is not supported.")
        };

        return new BrokerClient(runtime, adapter, _networkDispatcher);
    }

    public async ValueTask<IBroker> CreateAsync(
        BrokerConfiguration configuration,
        IEnumerable<BrokerInstrumentConfiguration> instruments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        BrokerCredentials credentials = BrokerCredentials.None;
        if (!string.IsNullOrWhiteSpace(configuration.CredentialReference))
        {
            if (_credentialStore is null)
            {
                throw new BrokerConfigurationException(
                    $"Broker '{configuration.Id}' references credentials, but no credential store was configured.");
            }

            credentials = await _credentialStore
                .GetAsync(configuration.CredentialReference, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new BrokerConfigurationException(
                    $"Credentials '{configuration.CredentialReference}' were not found for broker '{configuration.Id}'.");
        }

        return Create(configuration, instruments, credentials);
    }

    private static void ValidateConfiguration(
        BrokerConfiguration configuration,
        IReadOnlyCollection<BrokerInstrumentConfiguration> instruments,
        BrokerCredentials credentials)
    {
        if (string.IsNullOrWhiteSpace(configuration.Id) || string.IsNullOrWhiteSpace(configuration.Name))
        {
            throw new BrokerConfigurationException("Every broker requires a non-empty Id and Name.");
        }

        if (!Enum.IsDefined(configuration.Provider))
        {
            throw new BrokerConfigurationException($"Unknown broker provider '{configuration.Provider}'.");
        }

        if (!Enum.IsDefined(configuration.Environment))
        {
            throw new BrokerConfigurationException($"Unknown broker environment '{configuration.Environment}'.");
        }

        if (configuration.DefaultCandleLimit is <= 0 or > CandleQuery.MaximumLimit)
        {
            throw new BrokerConfigurationException(
                $"DefaultCandleLimit must be between 1 and {CandleQuery.MaximumLimit}.");
        }

        if (configuration.RequestTimeoutMilliseconds <= 0)
        {
            throw new BrokerConfigurationException("RequestTimeoutMilliseconds must be positive.");
        }

        if (instruments.Count == 0)
        {
            throw new BrokerConfigurationException(
                $"Broker '{configuration.Id}' must contain at least one instrument mapping.");
        }

        var instrumentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var providerSymbols = new HashSet<string>(StringComparer.Ordinal);
        foreach (BrokerInstrumentConfiguration instrument in instruments)
        {
            if (string.IsNullOrWhiteSpace(instrument.InstrumentId) ||
                string.IsNullOrWhiteSpace(instrument.ProviderSymbol))
            {
                throw new BrokerConfigurationException(
                    "InstrumentId and ProviderSymbol are required for every instrument mapping.");
            }

            if (!instrumentIds.Add(instrument.InstrumentId))
            {
                throw new BrokerConfigurationException(
                    $"Instrument '{instrument.InstrumentId}' is mapped more than once for broker '{configuration.Id}'.");
            }

            if (!providerSymbols.Add(instrument.ProviderSymbol))
            {
                throw new BrokerConfigurationException(
                    $"Provider symbol '{instrument.ProviderSymbol}' is mapped more than once for broker '{configuration.Id}'.");
            }
        }

        if (configuration.Provider == BrokerProvider.OandaV20)
        {
            if (string.IsNullOrWhiteSpace(configuration.AccountId))
            {
                throw new BrokerConfigurationException("An OANDA broker requires AccountId.");
            }

            if (string.IsNullOrWhiteSpace(credentials.AccessToken))
            {
                throw new BrokerConfigurationException("An OANDA broker requires an access token.");
            }
        }
    }

    private static CandlePriceBasis ResolvePriceBasis(BrokerConfiguration configuration) =>
        configuration.Provider switch
        {
            BrokerProvider.BinanceSpot when configuration.CandlePriceBasis is
                CandlePriceBasis.ProviderDefault or CandlePriceBasis.Trades => CandlePriceBasis.Trades,
            BrokerProvider.OandaV20 when configuration.CandlePriceBasis == CandlePriceBasis.ProviderDefault =>
                CandlePriceBasis.Midpoint,
            BrokerProvider.OandaV20 when configuration.CandlePriceBasis is
                CandlePriceBasis.Midpoint or CandlePriceBasis.Bid or CandlePriceBasis.Ask =>
                configuration.CandlePriceBasis,
            _ => throw new BrokerConfigurationException(
                $"Price basis '{configuration.CandlePriceBasis}' is not supported by {configuration.Provider}.")
        };
}
