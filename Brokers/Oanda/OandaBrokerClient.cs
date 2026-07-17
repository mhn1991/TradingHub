using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Brokers.Abstractions;
using Brokers.Infrastructure;
using Brokers.Models;
using Networking.Abstractions;
using Networking.Http;

namespace Brokers.Oanda;

public sealed class OandaBrokerClient : IProtectiveOrderBrokerClient, IPositionReductionBrokerClient,
    ITransactionHistoryBrokerClient, IInstrumentMetadataBrokerClient
{
    private static readonly TransportId RestTransportId = new("oanda.rest");
    private readonly BrokerHttpRuntime _runtime;
    private readonly HttpClient _streamingClient;
    private readonly ConcurrentDictionary<string, string> _instrumentMappings;
    private readonly string _accountId;

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
        Uri streamBaseAddress = options.StreamBaseAddress ?? options.Environment switch
        {
            BrokerEnvironment.Demo => new Uri("https://stream-fxpractice.oanda.com/"),
            BrokerEnvironment.Live => new Uri("https://stream-fxtrade.oanda.com/"),
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
        var instrumentMappings = new ConcurrentDictionary<string, string>(
            options.InstrumentMappings,
            StringComparer.OrdinalIgnoreCase);
        _instrumentMappings = instrumentMappings;
        _accountId = options.AccountId;
        _streamingClient = CreateClient(streamBaseAddress, options.AccessToken);

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
            instrumentMappings,
            _streamingClient);
        TransactionHistory = new OandaTransactionHistoryClient(
            _runtime.Gateway,
            RestTransportId,
            options.AccountId,
            instrumentMappings);
        InstrumentMetadata = new OandaInstrumentMetadataClient(
            _runtime.Gateway,
            RestTransportId,
            options.AccountId,
            instrumentMappings);
        ProtectiveOrders = new OandaProtectiveOrderClient(
            _runtime.Gateway,
            RestTransportId,
            options.AccountId);
        PositionReductions = new OandaPositionReductionClient(
            _runtime.Gateway,
            RestTransportId,
            options.AccountId);
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
    public ITradingOrderClient Orders { get; }
    IOrderClient IBrokerClient.Orders => Orders;
    public ITransactionHistoryClient TransactionHistory { get; }
    public IInstrumentMetadataClient InstrumentMetadata { get; }
    public TradingBrokerCapabilities TradingCapabilities { get; } = new()
    {
        SupportsNativeStopAmendment = true,
        SupportsAtomicOrderReplacement = true,
        SupportsDependentOcoAmendment = true
    };
    public IProtectiveOrderClient ProtectiveOrders { get; }
    public IPositionReductionClient PositionReductions { get; }
    public IPositionClient Positions { get; }
    public ICostClient Costs { get; }

    public async Task<IReadOnlyList<OandaInstrumentInfo>> GetInstrumentsAsync(
        CancellationToken cancellationToken = default)
    {
        OandaInstrumentsResponse response = await _runtime.Gateway.SendAsync(
            new OandaGetInstrumentsCommand(RestTransportId, _accountId),
            cancellationToken).ConfigureAwait(false);
        foreach (OandaInstrument instrument in response.Instruments)
        {
            _instrumentMappings.TryAdd(
                OandaMappings.CanonicalInstrumentName(instrument),
                instrument.Name);
        }

        return response.Instruments
            .Where(instrument => !string.IsNullOrWhiteSpace(instrument.Name))
            .Select(instrument => new OandaInstrumentInfo(
                instrument.Name,
                instrument.DisplayName ?? instrument.Name.Replace('_', '/'),
                instrument.Type ?? "UNKNOWN"))
            .OrderBy(instrument => instrument.DisplayName, StringComparer.Ordinal)
            .ToArray();
    }

    public async IAsyncEnumerable<OandaPriceTick> StreamPricesAsync(
        IReadOnlyCollection<string> instruments,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instruments);
        string[] nativeInstruments = instruments
            .Where(instrument => !string.IsNullOrWhiteSpace(instrument))
            .Select(instrument => InstrumentMappers.ToOanda(new InstrumentKey(instrument), _instrumentMappings))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (nativeInstruments.Length is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(
                nameof(instruments),
                "OANDA pricing streams require between one and fifty instruments.");
        }

        string list = string.Join(',', nativeInstruments.Select(Uri.EscapeDataString));
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"v3/accounts/{Uri.EscapeDataString(_accountId)}/pricing/stream" +
            $"?snapshot=true&instruments={list}");
        using HttpResponseMessage response = await _streamingClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                yield break;
            }
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            OandaPricingStreamMessage? message = JsonSerializer.Deserialize(
                line,
                BrokerJsonSerializerContext.Default.OandaPricingStreamMessage);
            OandaPriceTick? tick = message is null ? null : OandaMappings.ToPriceTick(message);
            if (tick is not null)
            {
                yield return tick;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _streamingClient.Dispose();
        await _runtime.DisposeAsync().ConfigureAwait(false);
    }

    private static HttpClient CreateClient(Uri baseAddress, string accessToken) =>
        PooledHttpClient.Create(
            new PooledHttpClientOptions
            {
                BaseAddress = baseAddress,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                MaxConnectionsPerServer = 16,
                DefaultHeaders = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {accessToken}",
                    ["Accept"] = "application/json"
                }
            });
}
