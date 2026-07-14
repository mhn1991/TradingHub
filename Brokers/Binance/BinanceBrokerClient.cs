using Brokers.Abstractions;
using Brokers.Infrastructure;
using Networking.Abstractions;
using Networking.Http;

namespace Brokers.Binance;

public sealed class BinanceBrokerClient : IBrokerClient
{
    private static readonly TransportId RestTransportId = new("binance.spot.rest");
    private readonly BrokerHttpRuntime _runtime;

    public BinanceBrokerClient(BinanceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.InstrumentMappings);
        ArgumentNullException.ThrowIfNull(options.TimeProvider);

        Uri baseAddress = options.BaseAddress ?? options.Environment switch
        {
            BrokerEnvironment.Demo => new Uri("https://demo-api.binance.com/"),
            BrokerEnvironment.Live => new Uri("https://api.binance.com/"),
            BrokerEnvironment.Testnet => new Uri("https://testnet.binance.vision/"),
            _ => throw new ArgumentOutOfRangeException(nameof(options.Environment))
        };

        HttpClient httpClient = PooledHttpClient.Create(
            new PooledHttpClientOptions
            {
                BaseAddress = baseAddress,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                MaxConnectionsPerServer = 16,
                DefaultHeaders = new Dictionary<string, string>
                {
                    ["Accept"] = "application/json"
                }
            });

        _runtime = new BrokerHttpRuntime(RestTransportId, httpClient, options.RequestTimeout);
        var instrumentMappings = new Dictionary<string, string>(
            options.InstrumentMappings,
            StringComparer.OrdinalIgnoreCase);
        bool hasApiKey = !string.IsNullOrWhiteSpace(options.ApiKey);
        bool hasSecretKey = !string.IsNullOrWhiteSpace(options.SecretKey);
        if (hasApiKey != hasSecretKey)
        {
            throw new ArgumentException(
                "Binance API key and secret must either both be supplied or both be omitted.");
        }

        Descriptor = new BrokerDescriptor(BrokerKind.Binance, options.Environment, "spot");
        MarketData = new BinanceMarketDataClient(
            _runtime.Gateway,
            RestTransportId,
            options.TimeProvider,
            instrumentMappings);
        Positions = new UnsupportedPositionClient(BrokerKind.Binance);

        if (hasApiKey)
        {
            var signer = new BinanceRequestSigner(
                options.ApiKey!,
                options.SecretKey!,
                options.ReceiveWindow,
                options.TimeProvider);
            Accounts = new BinanceAccountClient(_runtime.Gateway, RestTransportId, signer);
            Orders = new BinanceOrderClient(
                _runtime.Gateway,
                RestTransportId,
                signer,
                instrumentMappings);
            Costs = new BinanceCostClient(
                _runtime.Gateway,
                RestTransportId,
                signer,
                instrumentMappings);
            Capabilities = new BrokerCapabilities(true, true, true, false, true);
        }
        else
        {
            Accounts = new UnsupportedAccountClient(BrokerKind.Binance);
            Orders = new UnsupportedOrderClient(BrokerKind.Binance);
            Costs = new UnsupportedCostClient(BrokerKind.Binance);
            Capabilities = new BrokerCapabilities(true, false, false, false, false);
        }
    }

    public BrokerDescriptor Descriptor { get; }
    public BrokerCapabilities Capabilities { get; }
    public IMarketDataClient MarketData { get; }
    public IAccountClient Accounts { get; }
    public IOrderClient Orders { get; }
    public IPositionClient Positions { get; }
    public ICostClient Costs { get; }

    public ValueTask DisposeAsync() => _runtime.DisposeAsync();
}
