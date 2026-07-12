using Brokers.Abstractions;
using Brokers.Infrastructure;
using Networking.Abstractions;
using Networking.Http;

namespace Brokers.Ig;

public sealed class IgBrokerClient : IBrokerClient
{
    private static readonly TransportId RestTransportId = new("ig.rest");
    private readonly BrokerHttpRuntime _runtime;
    private readonly IgSessionManager _sessions;

    public IgBrokerClient(IgOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Identifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Password);
        ArgumentNullException.ThrowIfNull(options.InstrumentMappings);
        ArgumentNullException.ThrowIfNull(options.TimeProvider);

        Uri baseAddress = options.BaseAddress ?? options.Environment switch
        {
            BrokerEnvironment.Demo => new Uri("https://demo-api.ig.com/gateway/deal/"),
            BrokerEnvironment.Live => new Uri("https://api.ig.com/gateway/deal/"),
            _ => throw new ArgumentOutOfRangeException(nameof(options.Environment))
        };

        HttpClient httpClient = PooledHttpClient.Create(
            new PooledHttpClientOptions
            {
                BaseAddress = baseAddress,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                MaxConnectionsPerServer = 8,
                DefaultHeaders = new Dictionary<string, string>
                {
                    ["Accept"] = "application/json",
                    ["X-IG-API-KEY"] = options.ApiKey
                }
            });

        _runtime = new BrokerHttpRuntime(RestTransportId, httpClient, options.RequestTimeout);
        _sessions = new IgSessionManager(
            _runtime.Gateway,
            RestTransportId,
            options.Identifier,
            options.Password,
            options.AccountId);

        var instrumentMappings = new Dictionary<string, string>(
            options.InstrumentMappings,
            StringComparer.OrdinalIgnoreCase);

        Descriptor = new BrokerDescriptor(BrokerKind.Ig, options.Environment, options.AccountId);
        MarketData = new IgMarketDataClient(
            RestTransportId,
            _sessions,
            instrumentMappings,
            options.TimeProvider);
        Accounts = new IgAccountClient(RestTransportId, _sessions);
        Orders = new IgOrderClient(
            RestTransportId,
            _sessions,
            instrumentMappings);
        Positions = new IgPositionClient(
            RestTransportId,
            _sessions,
            instrumentMappings);
        Costs = new UnsupportedCostClient(BrokerKind.Ig);
    }

    public BrokerDescriptor Descriptor { get; }
    public BrokerCapabilities Capabilities { get; } = new(true, true, true, true, false);
    public IMarketDataClient MarketData { get; }
    public IAccountClient Accounts { get; }
    public IOrderClient Orders { get; }
    public IPositionClient Positions { get; }
    public ICostClient Costs { get; }

    public async ValueTask DisposeAsync()
    {
        _sessions.Dispose();
        await _runtime.DisposeAsync().ConfigureAwait(false);
    }
}
