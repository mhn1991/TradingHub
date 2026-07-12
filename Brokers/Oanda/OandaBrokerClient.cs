using Brokers.Abstractions;
using Brokers.Infrastructure;
using Networking.Abstractions;
using Networking.Http;

namespace Brokers.Oanda;

public sealed class OandaBrokerClient : IBrokerClient
{
    private static readonly TransportId RestTransportId = new("oanda.rest");
    private readonly BrokerHttpRuntime _runtime;

    public OandaBrokerClient(OandaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.AccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.AccessToken);
        ArgumentNullException.ThrowIfNull(options.InstrumentMappings);

        Uri baseAddress = options.BaseAddress ?? options.Environment switch
        {
            BrokerEnvironment.Demo => new Uri("https://api-fxpractice.oanda.com/"),
            BrokerEnvironment.Live => new Uri("https://api-fxtrade.oanda.com/"),
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
                    ["Authorization"] = $"Bearer {options.AccessToken}",
                    ["Accept"] = "application/json"
                }
            });

        _runtime = new BrokerHttpRuntime(RestTransportId, httpClient, options.RequestTimeout);
        var instrumentMappings = new Dictionary<string, string>(
            options.InstrumentMappings,
            StringComparer.OrdinalIgnoreCase);

        Descriptor = new BrokerDescriptor(
            BrokerKind.Oanda,
            options.Environment,
            options.AccountId);

        MarketData = new OandaMarketDataClient(
            _runtime.Gateway,
            RestTransportId,
            instrumentMappings);
        Accounts = new OandaAccountClient(_runtime.Gateway, RestTransportId, options.AccountId);
        Orders = new OandaOrderClient(
            _runtime.Gateway,
            RestTransportId,
            options.AccountId,
            instrumentMappings);
        Positions = new OandaPositionClient(
            _runtime.Gateway,
            RestTransportId,
            options.AccountId,
            instrumentMappings);
        Costs = new UnsupportedCostClient(BrokerKind.Oanda);
    }

    public BrokerDescriptor Descriptor { get; }
    public BrokerCapabilities Capabilities { get; } = new(true, true, true, true, false);
    public IMarketDataClient MarketData { get; }
    public IAccountClient Accounts { get; }
    public IOrderClient Orders { get; }
    public IPositionClient Positions { get; }
    public ICostClient Costs { get; }

    public ValueTask DisposeAsync() => _runtime.DisposeAsync();
}
