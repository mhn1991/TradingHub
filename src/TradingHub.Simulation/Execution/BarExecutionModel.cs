using TradingHub.Domain.Markets;
using TradingHub.Domain.Trading;

namespace TradingHub.Simulation.Execution;

internal sealed class BarExecutionModel : IExecutionModel
{
    private readonly ExecutionModelOptions _options;

    public BarExecutionModel(ExecutionModelOptions options)
    {
        options.EnsureValid();
        _options = options;
    }

    public FillDecision? TryExecute(
        PendingOrder order,
        PriceBar bar,
        InstrumentDefinition instrument)
    {
        if (order.EligibleAt > bar.OpenTime || order.RemainingQuantity <= 0m)
        {
            return null;
        }

        var rawPrice = FindExecutablePrice(order, bar);
        if (rawPrice is null)
        {
            return null;
        }

        var available = _options.MaximumFillQuantityPerBar ?? order.RemainingQuantity;
        if (order.Request.TimeInForce == TimeInForce.FillOrKill && available < order.RemainingQuantity)
        {
            return new FillDecision
            {
                Status = OrderStatus.Expired,
                Reason = "Fill-or-kill liquidity was unavailable."
            };
        }

        var quantity = Math.Min(order.RemainingQuantity, available);
        var price = ApplyExecutionCosts(rawPrice.Value, order.Request, instrument);
        var fee = quantity * price * instrument.ContractSize * _options.CommissionBps / 10_000m;
        var cumulative = order.FilledQuantity + quantity;

        return new FillDecision
        {
            Status = DetermineStatus(order, cumulative),
            Quantity = quantity,
            Price = price,
            Fee = fee
        };
    }

    private static decimal? FindExecutablePrice(PendingOrder order, PriceBar bar)
    {
        return order.Request.OrderType switch
        {
            OrderType.Market => MarketPrice(order.Request.Side, bar),
            OrderType.Limit => LimitPrice(order.Request, bar),
            OrderType.Stop => StopPrice(order.Request, bar),
            OrderType.StopLimit => StopLimitPrice(order, bar),
            _ => null
        };
    }

    private static decimal MarketPrice(OrderSide side, PriceBar bar)
    {
        return side == OrderSide.Buy ? bar.Ask.Open : bar.Bid.Open;
    }

    private static decimal? LimitPrice(OrderRequest request, PriceBar bar)
    {
        var limit = request.LimitPrice!.Value;
        if (request.Side == OrderSide.Buy)
        {
            return bar.Ask.Open <= limit ? bar.Ask.Open : bar.Ask.Low <= limit ? limit : null;
        }

        return bar.Bid.Open >= limit ? bar.Bid.Open : bar.Bid.High >= limit ? limit : null;
    }

    private static decimal? StopPrice(OrderRequest request, PriceBar bar)
    {
        var stop = request.StopPrice!.Value;
        if (request.Side == OrderSide.Buy)
        {
            return bar.Ask.Open >= stop ? bar.Ask.Open : bar.Ask.High >= stop ? stop : null;
        }

        return bar.Bid.Open <= stop ? bar.Bid.Open : bar.Bid.Low <= stop ? stop : null;
    }

    private static decimal? StopLimitPrice(PendingOrder order, PriceBar bar)
    {
        if (!order.StopTriggered)
        {
            order.StopTriggered = StopPrice(order.Request, bar) is not null;
            return null;
        }

        return LimitPrice(order.Request, bar);
    }

    private decimal ApplyExecutionCosts(
        decimal rawPrice,
        OrderRequest request,
        InstrumentDefinition instrument)
    {
        var marketLike = request.OrderType is OrderType.Market or OrderType.Stop;
        var adjusted = marketLike
            ? request.Side == OrderSide.Buy
                ? rawPrice * (1m + _options.SlippageBps / 10_000m)
                : rawPrice * (1m - _options.SlippageBps / 10_000m)
            : rawPrice;

        var ticks = adjusted / instrument.PriceIncrement;
        var roundedTicks = request.Side == OrderSide.Buy
            ? decimal.Ceiling(ticks)
            : decimal.Floor(ticks);
        return roundedTicks * instrument.PriceIncrement;
    }

    private static OrderStatus DetermineStatus(PendingOrder order, decimal cumulativeQuantity)
    {
        if (cumulativeQuantity >= order.Request.Quantity)
        {
            return OrderStatus.Filled;
        }

        return order.Request.TimeInForce == TimeInForce.ImmediateOrCancel
            ? OrderStatus.Cancelled
            : OrderStatus.PartiallyFilled;
    }
}
