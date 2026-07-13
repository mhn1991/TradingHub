using System.Runtime.CompilerServices;
using System.Text.Json;
using Brokers.Abstractions;
using Brokers.Infrastructure;
using Brokers.Models;
using Networking.Abstractions;

namespace Brokers.Oanda;

internal sealed class OandaMarketDataClient(
    INetworkGateway gateway,
    TransportId transportId,
    IReadOnlyDictionary<string, string> instrumentMappings) : IMarketDataClient
{
    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(
        CandleQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate(5000, "OANDA");

        var command = new OandaGetCandlesCommand(
            transportId,
            InstrumentMappers.ToOanda(query.Instrument, instrumentMappings),
            OandaMappings.ToGranularity(query.Interval),
            query.Limit,
            query.From,
            query.To);

        OandaCandlesResponse response = await gateway
            .SendAsync(command, cancellationToken)
            .ConfigureAwait(false);

        var candles = new List<Candle>(response.Candles.Count);
        foreach (OandaCandle candle in response.Candles)
        {
            candles.Add(OandaMappings.ToCandle(candle, query));
        }

        candles.Sort(static (left, right) => left.OpenTime.CompareTo(right.OpenTime));
        return candles;
    }

}

internal sealed class OandaAccountClient(
    INetworkGateway gateway,
    TransportId transportId,
    string accountId) : IAccountClient
{
    public async Task<IReadOnlyList<AccountSnapshot>> GetAccountsAsync(
        CancellationToken cancellationToken = default)
    {
        OandaAccountSummaryResponse response = await gateway.SendAsync(
            new OandaGetAccountSummaryCommand(transportId, accountId),
            cancellationToken).ConfigureAwait(false);

        OandaAccountSummary account = response.Account;
        return
        [
            new AccountSnapshot
            {
                AccountId = account.Id ?? accountId,
                AccountType = "Margin",
                Currency = account.Currency,
                Balance = BrokerJson.ParseNullableDecimal(account.Balance),
                Available = BrokerJson.ParseNullableDecimal(account.MarginAvailable),
                MarginUsed = BrokerJson.ParseNullableDecimal(account.MarginUsed),
                UnrealizedProfitLoss = BrokerJson.ParseNullableDecimal(account.UnrealizedPl),
                CanTrade = account.TradingDisabled is null ? null : !account.TradingDisabled
            }
        ];
    }
}

internal sealed class OandaOrderClient(
    INetworkGateway gateway,
    TransportId transportId,
    string accountId,
    IReadOnlyDictionary<string, string> instrumentMappings,
    HttpClient streamingClient) : ITradingOrderClient
{
    public async Task<IReadOnlyList<BrokerOrder>> GetOpenOrdersAsync(
        InstrumentKey? instrument = null,
        CancellationToken cancellationToken = default)
    {
        OandaPendingOrdersResponse response = await gateway.SendAsync(
            new OandaGetPendingOrdersCommand(transportId, accountId),
            cancellationToken).ConfigureAwait(false);

        string? nativeFilter = instrument is null
            ? null
            : InstrumentMappers.ToOanda(instrument.Value, instrumentMappings);

        return response.Orders
            .Where(order => nativeFilter is null ||
                string.Equals(order.Instrument, nativeFilter, StringComparison.OrdinalIgnoreCase))
            .Select(order => OandaMappings.ToOrder(order, instrumentMappings))
            .ToArray();
    }

    public async Task<OrderSubmission> PlaceOrderAsync(
        PlaceOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string clientOrderId = string.IsNullOrWhiteSpace(request.ClientOrderId)
            ? $"th-{Guid.NewGuid():N}"
            : request.ClientOrderId;
        if (clientOrderId.Length > 128)
        {
            throw new ArgumentException("The OANDA client order ID cannot exceed 128 characters.", nameof(request));
        }

        string nativeInstrument = InstrumentMappers.ToOanda(request.Instrument, instrumentMappings);
        OandaCreateOrderEnvelope payload = OandaMappings.ToOrderRequest(
            request,
            nativeInstrument,
            clientOrderId);
        OandaOrderMutationResponse response = await gateway.SendAsync(
            new OandaPlaceOrderCommand(transportId, accountId, payload),
            cancellationToken).ConfigureAwait(false);
        return OandaMappings.ToSubmission(response, clientOrderId);
    }

    public async Task CancelOrderAsync(
        string brokerOrderId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerOrderId);
        OandaOrderMutationResponse response = await gateway.SendAsync(
            new OandaCancelOrderCommand(transportId, accountId, brokerOrderId),
            cancellationToken).ConfigureAwait(false);
        if (response.OrderCancelTransaction is null)
        {
            throw new InvalidOperationException(
                response.ErrorMessage ?? "OANDA did not confirm the order cancellation.");
        }
    }

    public async IAsyncEnumerable<OrderEvent> StreamOrderEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"v3/accounts/{Uri.EscapeDataString(accountId)}/transactions/stream");
        using HttpResponseMessage response = await streamingClient.SendAsync(
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

            OandaTransaction? transaction = JsonSerializer.Deserialize(
                line,
                BrokerJsonSerializerContext.Default.OandaTransaction);
            OrderEvent? orderEvent = transaction is null
                ? null
                : OandaMappings.ToOrderEvent(transaction, instrumentMappings);
            if (orderEvent is not null)
            {
                yield return orderEvent;
            }
        }
    }
}

internal sealed class OandaPositionClient(
    INetworkGateway gateway,
    TransportId transportId,
    string accountId,
    IReadOnlyDictionary<string, string> instrumentMappings) : IPositionClient
{
    public async Task<IReadOnlyList<BrokerPosition>> GetOpenPositionsAsync(
        CancellationToken cancellationToken = default)
    {
        OandaOpenPositionsResponse response = await gateway.SendAsync(
            new OandaGetOpenPositionsCommand(transportId, accountId),
            cancellationToken).ConfigureAwait(false);

        var result = new List<BrokerPosition>();
        foreach (OandaPosition position in response.Positions)
        {
            OandaMappings.AddPositionSide(
                result, position, position.Long, OrderSide.Buy, "long", instrumentMappings);
            OandaMappings.AddPositionSide(
                result, position, position.Short, OrderSide.Sell, "short", instrumentMappings);
        }

        return result;
    }
}
