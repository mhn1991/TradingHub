using System.Globalization;
using TradingHub.Brokers.Oanda.Contracts;
using TradingHub.Brokers.Internal;
using TradingHub.Domain.Trading;

namespace TradingHub.Brokers.Oanda.Mapping;

internal static class OandaOrderMapper
{
    public static OandaCreateOrderEnvelopeDto ToExternal(OrderRequest request, string brokerSymbol)
    {
        var type = MapOrderType(request.OrderType);
        var price = request.OrderType switch
        {
            OrderType.Limit => Format(request.LimitPrice),
            OrderType.Stop => Format(request.StopPrice),
            _ => null
        };
        var signedQuantity = request.Side == OrderSide.Buy ? request.Quantity : -request.Quantity;

        return new OandaCreateOrderEnvelopeDto
        {
            Order = new OandaCreateOrderDto
            {
                Units = Format(signedQuantity)!,
                Instrument = brokerSymbol,
                TimeInForce = MapTimeInForce(request),
                Type = type,
                Price = price,
                ClientExtensions = new OandaClientExtensionsDto
                {
                    Id = request.ClientOrderId,
                    Tag = request.StrategyId
                }
            }
        };
    }

    public static OrderSubmissionResult ToCanonical(
        OrderRequest request,
        OandaOrderResponseDto response,
        DateTimeOffset fallbackTime)
    {
        if (response.OrderRejectTransaction is not null)
        {
            return BrokerResultFactory.RejectedOrder(
                response.ErrorMessage ?? response.OrderRejectTransaction.Reason ?? "OANDA rejected the order.",
                fallbackTime);
        }

        if (response.OrderCancelTransaction is not null && response.OrderFillTransaction is null)
        {
            return new OrderSubmissionResult
            {
                Outcome = SubmissionOutcome.Rejected,
                Status = OrderStatus.Cancelled,
                BrokerOrderId = response.OrderCreateTransaction?.Id,
                OccurredAt = ParseTime(response.OrderCancelTransaction.Time, fallbackTime),
                Reason = response.OrderCancelTransaction.Reason
            };
        }

        var brokerOrderId = response.OrderCreateTransaction?.Id
            ?? response.OrderFillTransaction?.OrderId
            ?? response.OrderFillTransaction?.Id;
        if (response.OrderFillTransaction is null)
        {
            return new OrderSubmissionResult
            {
                Outcome = SubmissionOutcome.Accepted,
                Status = OrderStatus.Acknowledged,
                BrokerOrderId = brokerOrderId,
                OccurredAt = ParseTime(response.OrderCreateTransaction?.Time, fallbackTime)
            };
        }

        var fill = response.OrderFillTransaction;
        var execution = new ExecutionReport
        {
            OrderId = request.OrderId,
            BrokerOrderId = brokerOrderId ?? throw new InvalidOperationException("OANDA omitted the order ID."),
            InstrumentId = request.InstrumentId,
            Side = request.Side,
            Status = OrderStatus.Filled,
            LastFillQuantity = Math.Abs(ParseDecimal(fill.Units)),
            LastFillPrice = ParseDecimal(fill.Price),
            CumulativeFilledQuantity = Math.Abs(ParseDecimal(fill.Units)),
            Fee = Math.Abs(ParseDecimal(fill.Commission)),
            OccurredAt = ParseTime(fill.Time, fallbackTime),
            Reason = fill.Reason
        };
        return new OrderSubmissionResult
        {
            Outcome = SubmissionOutcome.Accepted,
            Status = OrderStatus.Filled,
            BrokerOrderId = brokerOrderId,
            OccurredAt = execution.OccurredAt,
            ImmediateExecutions = [execution]
        };
    }

    private static string MapOrderType(OrderType orderType)
    {
        return orderType switch
        {
            OrderType.Market => "MARKET",
            OrderType.Limit => "LIMIT",
            OrderType.Stop => "STOP",
            OrderType.StopLimit => throw new NotSupportedException("OANDA stop-limit mapping is not implemented."),
            _ => throw new ArgumentOutOfRangeException(nameof(orderType))
        };
    }

    private static string MapTimeInForce(OrderRequest request)
    {
        return request.TimeInForce switch
        {
            TimeInForce.FillOrKill => "FOK",
            TimeInForce.ImmediateOrCancel => "IOC",
            TimeInForce.GoodTilCancelled => "GTC",
            TimeInForce.GoodTilDate => throw new NotSupportedException("A GTD expiry timestamp is required."),
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        };
    }

    private static string? Format(decimal? value)
    {
        return value?.ToString(CultureInfo.InvariantCulture);
    }

    private static decimal ParseDecimal(string? value)
    {
        return decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0m;
    }

    private static DateTimeOffset ParseTime(string? value, DateTimeOffset fallback)
    {
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var result)
            ? result
            : fallback;
    }
}
