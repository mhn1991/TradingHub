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
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SecretKey);
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
        var signer = new BinanceRequestSigner(
            options.ApiKey,
            options.SecretKey,
            options.ReceiveWindow,
            options.TimeProvider);

        Descriptor = new BrokerDescriptor(BrokerKind.Binance, options.Environment, "spot");
        MarketData = new BinanceMarketDataClient(
            _runtime.Gateway,
            RestTransportId,
            options.TimeProvider,
            instrumentMappings);
        Accounts = new BinanceAccountClient(_runtime.Gateway, RestTransportId, signer);
        Orders = new BinanceOrderClient(
            _runtime.Gateway,
            RestTransportId,
            signer,
            instrumentMappings);
        Positions = new UnsupportedPositionClient(BrokerKind.Binance);
        Costs = new BinanceCostClient(
            _runtime.Gateway,
            RestTransportId,
            signer,
            instrumentMappings);
    }

    public BrokerDescriptor Descriptor { get; }
    public BrokerCapabilities Capabilities { get; } = new(true, true, true, false, true);
    public IMarketDataClient MarketData { get; }
    public IAccountClient Accounts { get; }
    public IOrderClient Orders { get; }
    public IPositionClient Positions { get; }
    public ICostClient Costs { get; }

    public ValueTask DisposeAsync() => _runtime.DisposeAsync();
}
