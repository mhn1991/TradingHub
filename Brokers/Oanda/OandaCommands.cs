using System.Globalization;
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
    public bool IsIdempotent => true;
    public int MaxTransientRetries => 2;
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
            "price=M"
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
