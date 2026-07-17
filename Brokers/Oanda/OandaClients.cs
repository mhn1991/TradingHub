using System.Globalization;
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
                Equity = BrokerJson.ParseNullableDecimal(account.Nav),
                LastTransactionId = response.LastTransactionId,
                CanTrade = account.TradingDisabled is null ? null : !account.TradingDisabled
            }
        ];
    }
}

internal sealed class OandaInstrumentMetadataClient(
    INetworkGateway gateway,
    TransportId transportId,
    string accountId,
    System.Collections.Concurrent.ConcurrentDictionary<string, string> instrumentMappings)
    : IInstrumentMetadataClient
{
    public async Task<IReadOnlyList<InstrumentTradingMetadata>> GetInstrumentMetadataAsync(
        CancellationToken cancellationToken = default)
    {
        OandaInstrumentsResponse response = await gateway.SendAsync(
            new OandaGetInstrumentsCommand(transportId, accountId),
            cancellationToken).ConfigureAwait(false);
        foreach (OandaInstrument instrument in response.Instruments)
        {
            instrumentMappings.TryAdd(
                OandaMappings.CanonicalInstrumentName(instrument),
                instrument.Name);
        }
        return response.Instruments
            .Where(static instrument => !string.IsNullOrWhiteSpace(instrument.Name))
            .Select(instrument => OandaMappings.ToInstrumentMetadata(instrument, instrumentMappings))
            .OrderBy(static metadata => metadata.Instrument.Value, StringComparer.Ordinal)
            .ToArray();
    }
}

internal sealed class OandaTransactionHistoryClient(
    INetworkGateway gateway,
    TransportId transportId,
    string accountId,
    IReadOnlyDictionary<string, string> instrumentMappings) : ITransactionHistoryClient
{
    public async Task<BrokerEventHistoryBatch> GetOrderEventsSinceAsync(
        string lastTransactionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lastTransactionId);
        OandaTransactionsResponse response = await gateway.SendAsync(
            new OandaGetTransactionsSinceCommand(transportId, accountId, lastTransactionId),
            cancellationToken).ConfigureAwait(false);
        OrderEvent[] events = response.Transactions
            .Select(transaction => OandaMappings.ToOrderEvent(transaction, instrumentMappings))
            .Where(static item => item is not null)
            .Cast<OrderEvent>()
            .OrderBy(static item => ParseSortableTransactionId(item.BrokerTransactionId))
            .ThenBy(static item => item.Timestamp)
            .ToArray();
        return new BrokerEventHistoryBatch
        {
            Events = events,
            LastTransactionId = response.LastTransactionId
        };
    }

    private static long ParseSortableTransactionId(string? value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed)
            ? parsed
            : long.MaxValue;
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
            // Trade-dependent stops/targets are reconciled from openTrades, where OANDA provides
            // the owning trade ID and full protection details. Treating them as standalone entry
            // orders would create false "unowned order" alarms.
            .Where(order => !OandaMappings.IsTradeDependentOrder(order.Type))
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
        // Live ownership and stop reconciliation require trade-level state. OANDA's openTrades
        // representation includes the real trade ID and full dependent stop/target orders.
        OandaOpenTradesResponse response = await gateway.SendAsync(
            new OandaGetOpenTradesCommand(transportId, accountId),
            cancellationToken).ConfigureAwait(false);
        return response.Trades
            .Select(trade => OandaMappings.ToPosition(trade, instrumentMappings))
            .OrderBy(position => position.PositionId, StringComparer.Ordinal)
            .ToArray();
    }
}

/// <summary>OANDA explicit trade-close/reduction adapter.</summary>
internal sealed class OandaPositionReductionClient(
    INetworkGateway gateway,
    TransportId transportId,
    string accountId) : IPositionReductionClient
{
    public async Task<PositionReductionResult> ReducePositionAsync(
        ReduceBrokerPositionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        string units = request.IsFullClose
            ? "ALL"
            : request.Quantity.ToString("0.############################", CultureInfo.InvariantCulture);
        try
        {
            OandaOrderMutationResponse response = await gateway.SendAsync(
                new OandaCloseTradeCommand(
                    transportId,
                    accountId,
                    request.PositionId,
                    new OandaTradeCloseRequest { Units = units }),
                cancellationToken).ConfigureAwait(false);

            if (response.OrderRejectTransaction is { } rejected)
            {
                return new PositionReductionResult
                {
                    Status = PositionReductionStatus.Rejected,
                    ClientRequestId = request.ClientRequestId,
                    PositionId = request.PositionId,
                    BrokerOrderId = rejected.OrderId,
                    BrokerTransactionId = rejected.Id,
                    RequestedQuantity = request.Quantity,
                    Certainty = ExecutionCertainty.Rejected,
                    Reason = response.ErrorMessage ?? rejected.Reason ?? "OANDA rejected the trade reduction."
                };
            }

            OandaTransaction? fill = response.OrderFillTransaction;
            if (fill is null)
            {
                return new PositionReductionResult
                {
                    Status = PositionReductionStatus.Unknown,
                    ClientRequestId = request.ClientRequestId,
                    PositionId = request.PositionId,
                    BrokerOrderId = response.OrderCreateTransaction?.OrderId,
                    BrokerTransactionId = response.LastTransactionId,
                    RequestedQuantity = request.Quantity,
                    Certainty = ExecutionCertainty.Unknown,
                    Reason = response.ErrorMessage ?? "OANDA did not return a conclusive trade-close fill."
                };
            }

            decimal filled = Math.Abs(BrokerJson.ParseNullableDecimal(fill.Units) ?? request.Quantity);
            return new PositionReductionResult
            {
                Status = filled + 0.00000001m >= request.Quantity
                    ? PositionReductionStatus.Filled
                    : PositionReductionStatus.Partial,
                ClientRequestId = request.ClientRequestId,
                PositionId = request.PositionId,
                BrokerOrderId = fill.OrderId,
                BrokerTransactionId = fill.Id ?? response.LastTransactionId,
                RequestedQuantity = request.Quantity,
                FilledQuantity = Math.Min(filled, request.OwnedQuantity),
                FillPrice = BrokerJson.ParseNullableDecimal(fill.Price),
                Certainty = ExecutionCertainty.Accepted
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PositionReductionResult
            {
                Status = PositionReductionStatus.Unknown,
                ClientRequestId = request.ClientRequestId,
                PositionId = request.PositionId,
                RequestedQuantity = request.Quantity,
                Certainty = ExecutionCertainty.Unknown,
                Reason = ex.Message
            };
        }
    }
}

/// <summary>
/// Uses OANDA's single dependent-orders replacement endpoint. The existing stop is not cancelled
/// by the client first; a rejected or uncertain request therefore leaves broker protection intact.
/// </summary>
internal sealed class OandaProtectiveOrderClient(
    INetworkGateway gateway,
    TransportId transportId,
    string accountId) : IProtectiveOrderClient
{
    public async Task<ProtectiveStopAmendmentResult> AmendProtectiveStopAsync(
        AmendProtectiveStopRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.PositionId) || request.NewStopPrice <= 0m)
            throw new ArgumentException("A broker trade id and positive stop price are required.", nameof(request));

        try
        {
            OandaTradeDependentOrdersResponse response = await gateway.SendAsync(
                new OandaReplaceTradeDependentOrdersCommand(
                    transportId,
                    accountId,
                    request.PositionId,
                    new OandaTradeDependentOrdersRequest
                    {
                        StopLoss = new OandaDependentOrderRequest
                        {
                            Price = request.NewStopPrice.ToString(
                                "0.############################",
                                CultureInfo.InvariantCulture)
                        }
                    }),
                cancellationToken).ConfigureAwait(false);

            if (response.StopLossOrderTransaction is not { } accepted)
            {
                return new ProtectiveStopAmendmentResult
                {
                    Status = string.IsNullOrWhiteSpace(response.ErrorMessage)
                        ? ProtectiveStopAmendmentStatus.Unknown
                        : ProtectiveStopAmendmentStatus.Rejected,
                    ClientAmendmentId = request.ClientAmendmentId,
                    PreviousStopOrderId = request.ExistingStopOrderId,
                    CurrentStopOrderId = request.ExistingStopOrderId,
                    RequestedStopPrice = request.NewStopPrice,
                    RejectionReason = response.ErrorMessage ??
                        "OANDA did not return a conclusive stop replacement transaction.",
                    Certainty = string.IsNullOrWhiteSpace(response.ErrorMessage)
                        ? ExecutionCertainty.Unknown
                        : ExecutionCertainty.Rejected
                };
            }

            return new ProtectiveStopAmendmentResult
            {
                Status = ProtectiveStopAmendmentStatus.Replaced,
                ClientAmendmentId = request.ClientAmendmentId,
                PreviousStopOrderId = response.StopLossOrderCancelTransaction?.OrderId ??
                    request.ExistingStopOrderId,
                CurrentStopOrderId = accepted.OrderId ?? accepted.Id,
                RequestedStopPrice = request.NewStopPrice,
                AcceptedStopPrice = BrokerJson.ParseNullableDecimal(accepted.Price) ?? request.NewStopPrice,
                AcceptedAt = string.IsNullOrWhiteSpace(accepted.Time)
                    ? null
                    : DateTimeOffset.Parse(accepted.Time, CultureInfo.InvariantCulture),
                EffectiveFromExecutionSequence = request.EffectiveFromExecutionSequence,
                Certainty = ExecutionCertainty.Accepted
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ProtectiveStopAmendmentResult
            {
                Status = ProtectiveStopAmendmentStatus.Unknown,
                ClientAmendmentId = request.ClientAmendmentId,
                PreviousStopOrderId = request.ExistingStopOrderId,
                CurrentStopOrderId = request.ExistingStopOrderId,
                RequestedStopPrice = request.NewStopPrice,
                RejectionReason = ex.Message,
                Certainty = ExecutionCertainty.Unknown
            };
        }
    }
}
