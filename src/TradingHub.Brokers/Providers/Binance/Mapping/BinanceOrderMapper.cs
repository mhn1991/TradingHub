using System.Globalization;
using TradingHub.Brokers.Binance.Contracts;
using TradingHub.Brokers.Internal;
using TradingHub.Domain.Trading;

namespace TradingHub.Brokers.Binance.Mapping;

internal static class BinanceOrderMapper
{
    public static IReadOnlyList<KeyValuePair<string, string>> ToParameters(
        OrderRequest request,
        string brokerSymbol,
        long timestamp,
        int receiveWindowMilliseconds)
    {
        var type = request.OrderType switch
        {
            OrderType.Market => "MARKET",
            OrderType.Limit => "LIMIT",
            _ => throw new NotSupportedException("The Binance adapter currently supports market and limit orders.")
        };
        var parameters = new List<KeyValuePair<string, string>>
        {
            Pair("symbol", brokerSymbol),
            Pair("side", request.Side == OrderSide.Buy ? "BUY" : "SELL"),
            Pair("type", type),
            Pair("quantity", Format(request.Quantity)),
            Pair("newClientOrderId", request.ClientOrderId),
            Pair("newOrderRespType", "FULL")
        };

        if (request.OrderType == OrderType.Limit)
        {
            parameters.Add(Pair("timeInForce", MapTimeInForce(request.TimeInForce)));
            parameters.Add(Pair("price", Format(request.LimitPrice!.Value)));
        }

        parameters.Add(Pair("recvWindow", receiveWindowMilliseconds.ToString(CultureInfo.InvariantCulture)));
        parameters.Add(Pair("timestamp", timestamp.ToString(CultureInfo.InvariantCulture)));
        return parameters;
    }

    public static OrderSubmissionResult ToCanonical(
        OrderRequest request,
        BinanceOrderResponseDto response,
        DateTimeOffset fallbackTime)
    {
        var occurredAt = response.TransactTime > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(response.TransactTime)
            : fallbackTime;
        var status = MapStatus(response.Status);
        var brokerOrderId = response.OrderId.ToString(CultureInfo.InvariantCulture);
        var executions = CreateExecutions(request, response, brokerOrderId, occurredAt);
        return new OrderSubmissionResult
        {
            Outcome = status == OrderStatus.Rejected
                ? SubmissionOutcome.Rejected
                : SubmissionOutcome.Accepted,
            Status = status,
            BrokerOrderId = brokerOrderId,
            OccurredAt = occurredAt,
            ImmediateExecutions = executions
        };
    }

    public static OrderSubmissionResult Rejected(string reason, DateTimeOffset occurredAt) =>
        BrokerResultFactory.RejectedOrder(reason, occurredAt);

    private static IReadOnlyList<ExecutionReport> CreateExecutions(
        OrderRequest request,
        BinanceOrderResponseDto response,
        string brokerOrderId,
        DateTimeOffset occurredAt)
    {
        if (response.Fills.Count == 0)
        {
            return CreateAggregateExecution(request, response, brokerOrderId, occurredAt);
        }

        var reports = new List<ExecutionReport>();
        var cumulative = 0m;
        foreach (var fill in response.Fills)
        {
            var quantity = Parse(fill.Qty);
            cumulative += quantity;
            reports.Add(new ExecutionReport
            {
                OrderId = request.OrderId,
                BrokerOrderId = brokerOrderId,
                InstrumentId = request.InstrumentId,
                Side = request.Side,
                Status = cumulative >= request.Quantity ? OrderStatus.Filled : OrderStatus.PartiallyFilled,
                LastFillQuantity = quantity,
                LastFillPrice = Parse(fill.Price),
                CumulativeFilledQuantity = cumulative,
                Fee = Parse(fill.Commission),
                FeeAsset = fill.CommissionAsset,
                OccurredAt = occurredAt
            });
        }

        return reports;
    }

    private static IReadOnlyList<ExecutionReport> CreateAggregateExecution(
        OrderRequest request,
        BinanceOrderResponseDto response,
        string brokerOrderId,
        DateTimeOffset occurredAt)
    {
        var quantity = Parse(response.ExecutedQty);
        if (quantity <= 0m)
        {
            return [];
        }

        var quoteQuantity = Parse(response.CummulativeQuoteQty);
        var price = quoteQuantity > 0m ? quoteQuantity / quantity : Parse(response.Price);
        return
        [
            new ExecutionReport
            {
                OrderId = request.OrderId,
                BrokerOrderId = brokerOrderId,
                InstrumentId = request.InstrumentId,
                Side = request.Side,
                Status = MapStatus(response.Status),
                LastFillQuantity = quantity,
                LastFillPrice = price,
                CumulativeFilledQuantity = quantity,
                OccurredAt = occurredAt
            }
        ];
    }

    private static OrderStatus MapStatus(string? status)
    {
        return status?.ToUpperInvariant() switch
        {
            "NEW" or "PENDING_NEW" => OrderStatus.Acknowledged,
            "PARTIALLY_FILLED" => OrderStatus.PartiallyFilled,
            "FILLED" => OrderStatus.Filled,
            "CANCELED" => OrderStatus.Cancelled,
            "REJECTED" => OrderStatus.Rejected,
            "EXPIRED" or "EXPIRED_IN_MATCH" => OrderStatus.Expired,
            _ => OrderStatus.Unknown
        };
    }

    private static string MapTimeInForce(TimeInForce timeInForce)
    {
        return timeInForce switch
        {
            TimeInForce.FillOrKill => "FOK",
            TimeInForce.ImmediateOrCancel => "IOC",
            TimeInForce.GoodTilCancelled => "GTC",
            TimeInForce.GoodTilDate => throw new NotSupportedException("Binance Spot GTD mapping is not implemented."),
            _ => throw new ArgumentOutOfRangeException(nameof(timeInForce))
        };
    }

    private static KeyValuePair<string, string> Pair(string key, string value) => new(key, value);

    private static string Format(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static decimal Parse(string? value)
    {
        return decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0m;
    }
}
