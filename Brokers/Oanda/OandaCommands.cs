using System.Globalization;
using System.Net.Http.Json;
using Brokers.Abstractions;
using Brokers.Infrastructure;
using Networking.Abstractions;
using Networking.Http;

namespace Brokers.Oanda;

internal abstract class OandaHttpCommand<TResponse>(TransportId transportId)
    : IHttpCommand<TResponse>
{
    public TransportId TransportId { get; } = transportId;
    public TimeSpan? Timeout => null;
    public bool EnsureSuccessStatusCode => false;
    public virtual bool IsIdempotent => true;
    public virtual int MaxTransientRetries => 2;
    public abstract HttpRequestMessage CreateRequest();

    public ValueTask<TResponse> ReadResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken) =>
        BrokerJson.ReadAsync<TResponse>(BrokerKind.Oanda, response, cancellationToken);
}

internal sealed class OandaGetCandlesCommand(
    TransportId transportId,
    string instrument,
    string granularity,
    int limit,
    DateTimeOffset? from,
    DateTimeOffset? to) : OandaHttpCommand<OandaCandlesResponse>(transportId)
{
    public override HttpRequestMessage CreateRequest()
    {
        var query = new List<string>
        {
            $"granularity={Uri.EscapeDataString(granularity)}",
            $"count={limit.ToString(CultureInfo.InvariantCulture)}",
            "price=M",
            "dailyAlignment=0",
            "alignmentTimezone=UTC",
            "weeklyAlignment=Monday"
        };

        if (from is not null)
        {
            query.Add($"from={Uri.EscapeDataString(from.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))}");
        }

        if (to is not null)
        {
            query.Add($"to={Uri.EscapeDataString(to.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))}");
        }

        string path = $"v3/instruments/{Uri.EscapeDataString(instrument)}/candles?{string.Join('&', query)}";
        return new HttpRequestMessage(HttpMethod.Get, path);
    }
}

internal sealed class OandaGetAccountSummaryCommand(
    TransportId transportId,
    string accountId) : OandaHttpCommand<OandaAccountSummaryResponse>(transportId)
{
    public override HttpRequestMessage CreateRequest() => new(
        HttpMethod.Get,
        $"v3/accounts/{Uri.EscapeDataString(accountId)}/summary");
}

internal sealed class OandaGetPendingOrdersCommand(
    TransportId transportId,
    string accountId) : OandaHttpCommand<OandaPendingOrdersResponse>(transportId)
{
    public override HttpRequestMessage CreateRequest() => new(
        HttpMethod.Get,
        $"v3/accounts/{Uri.EscapeDataString(accountId)}/pendingOrders");
}

internal sealed class OandaGetOpenPositionsCommand(
    TransportId transportId,
    string accountId) : OandaHttpCommand<OandaOpenPositionsResponse>(transportId)
{
    public override HttpRequestMessage CreateRequest() => new(
        HttpMethod.Get,
        $"v3/accounts/{Uri.EscapeDataString(accountId)}/openPositions");
}

internal sealed class OandaGetInstrumentsCommand(
    TransportId transportId,
    string accountId) : OandaHttpCommand<OandaInstrumentsResponse>(transportId)
{
    public override HttpRequestMessage CreateRequest() => new(
        HttpMethod.Get,
        $"v3/accounts/{Uri.EscapeDataString(accountId)}/instruments");
}

internal sealed class OandaPlaceOrderCommand(
    TransportId transportId,
    string accountId,
    OandaCreateOrderEnvelope payload) : OandaHttpCommand<OandaOrderMutationResponse>(transportId)
{
    public override bool IsIdempotent => false;
    public override int MaxTransientRetries => 0;

    public override HttpRequestMessage CreateRequest() => new(
        HttpMethod.Post,
        $"v3/accounts/{Uri.EscapeDataString(accountId)}/orders")
    {
        Content = JsonContent.Create(
            payload,
            BrokerJsonSerializerContext.Default.OandaCreateOrderEnvelope)
    };
}

internal sealed class OandaCancelOrderCommand(
    TransportId transportId,
    string accountId,
    string orderId) : OandaHttpCommand<OandaOrderMutationResponse>(transportId)
{
    public override bool IsIdempotent => false;
    public override int MaxTransientRetries => 0;

    public override HttpRequestMessage CreateRequest() => new(
        HttpMethod.Put,
        $"v3/accounts/{Uri.EscapeDataString(accountId)}/orders/" +
        $"{Uri.EscapeDataString(orderId)}/cancel");
}
