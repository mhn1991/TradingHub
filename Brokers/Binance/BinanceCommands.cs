using System.Globalization;
using System.Text.Json;
using Brokers.Abstractions;
using Brokers.Infrastructure;
using Networking.Abstractions;
using Networking.Http;

namespace Brokers.Binance;

internal abstract class BinanceHttpCommand<TResponse>(TransportId transportId)
    : IHttpCommand<TResponse>
{
    public TransportId TransportId { get; } = transportId;
    public TimeSpan? Timeout => null;
    public bool EnsureSuccessStatusCode => false;
    public bool IsIdempotent => true;
    public int MaxTransientRetries => 2;
    public abstract HttpRequestMessage CreateRequest();

    public virtual ValueTask<TResponse> ReadResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken) =>
        BrokerJson.ReadAsync<TResponse>(BrokerKind.Binance, response, cancellationToken);
}

internal sealed class BinanceGetKlinesCommand(
    TransportId transportId,
    string symbol,
    string interval,
    int limit,
    DateTimeOffset? from,
    DateTimeOffset? to) : BinanceHttpCommand<BinanceKlineRow[]>(transportId)
{
    public override HttpRequestMessage CreateRequest()
    {
        var query = new List<string>
        {
            $"symbol={Uri.EscapeDataString(symbol)}",
            $"interval={Uri.EscapeDataString(interval)}",
            $"limit={limit.ToString(CultureInfo.InvariantCulture)}"
        };

        if (from is not null)
        {
            query.Add($"startTime={from.Value.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}");
        }

        if (to is not null)
        {
            query.Add($"endTime={to.Value.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}");
        }

        return new HttpRequestMessage(HttpMethod.Get, $"api/v3/klines?{string.Join('&', query)}");
    }

    public override async ValueTask<BinanceKlineRow[]> ReadResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        JsonElement[][] rows = await BrokerJson.ReadAsync<JsonElement[][]>(
            BrokerKind.Binance,
            response,
            cancellationToken).ConfigureAwait(false);

        return rows.Select(BinanceKlineRow.FromJson).ToArray();
    }
}

internal abstract class BinanceSignedCommand<TResponse>(
    TransportId transportId,
    BinanceRequestSigner signer) : BinanceHttpCommand<TResponse>(transportId)
{
    protected HttpRequestMessage CreateSignedGet(
        string path,
        IEnumerable<KeyValuePair<string, string?>> parameters)
    {
        string query = signer.CreateSignedQuery(parameters);
        var request = new HttpRequestMessage(HttpMethod.Get, $"{path}?{query}");
        request.Headers.TryAddWithoutValidation("X-MBX-APIKEY", signer.ApiKey);
        return request;
    }
}

internal sealed class BinanceGetAccountCommand(
    TransportId transportId,
    BinanceRequestSigner signer) : BinanceSignedCommand<BinanceAccountResponse>(transportId, signer)
{
    public override HttpRequestMessage CreateRequest() => CreateSignedGet(
        "api/v3/account",
        [new KeyValuePair<string, string?>("omitZeroBalances", "false")]);
}

internal sealed class BinanceGetOpenOrdersCommand(
    TransportId transportId,
    BinanceRequestSigner signer,
    string? symbol) : BinanceSignedCommand<BinanceOrderDto[]>(transportId, signer)
{
    public override HttpRequestMessage CreateRequest() => CreateSignedGet(
        "api/v3/openOrders",
        [new KeyValuePair<string, string?>("symbol", symbol)]);
}

internal sealed class BinanceGetCommissionCommand(
    TransportId transportId,
    BinanceRequestSigner signer,
    string symbol) : BinanceSignedCommand<BinanceCommissionResponse>(transportId, signer)
{
    public override HttpRequestMessage CreateRequest() => CreateSignedGet(
        "api/v3/account/commission",
        [new KeyValuePair<string, string?>("symbol", symbol)]);
}
