using TradingHub.Domain.Trading;

namespace TradingHub.Application.Risk;

public sealed class RiskEngine : IRiskEngine
{
    private readonly RiskLimits _limits;

    public RiskEngine(RiskLimits limits)
    {
        limits.EnsureValid();
        _limits = limits;
    }

    public RiskDecision Evaluate(TradeIntent intent, RiskContext context)
    {
        var basicDecision = CheckBasicLimits(intent, context);
        if (basicDecision is not null)
        {
            return basicDecision;
        }

        var orderDecision = CheckOrderShape(intent, context);
        if (orderDecision is not null)
        {
            return orderDecision;
        }

        var signedQuantity = intent.Side == OrderSide.Buy ? intent.Quantity : -intent.Quantity;
        var proposedPosition = context.CurrentNetQuantity + signedQuantity;

        if (!_limits.AllowShortSelling && proposedPosition < 0m)
        {
            return Reject("SHORT_SELLING_DISABLED", "The order would create a short position.");
        }

        if (Math.Abs(proposedPosition) > _limits.MaximumAbsolutePosition)
        {
            return Reject("POSITION_LIMIT", "The resulting position exceeds the configured limit.");
        }

        return Approve();
    }

    private RiskDecision? CheckBasicLimits(TradeIntent intent, RiskContext context)
    {
        if (!_limits.TradingEnabled)
        {
            return Reject("TRADING_DISABLED", "Trading is disabled by the global risk switch.");
        }

        if (!context.Instrument.IsEnabled)
        {
            return Reject("INSTRUMENT_DISABLED", "The instrument is not on the enabled whitelist.");
        }

        if (intent.ExpiresAt <= context.Now)
        {
            return Reject("INTENT_EXPIRED", "The trade intent has expired.");
        }

        if (intent.Quantity > _limits.MaximumOrderQuantity || !context.Instrument.IsQuantityValid(intent.Quantity))
        {
            return Reject("ORDER_QUANTITY", "The order quantity violates instrument or risk limits.");
        }

        if (context.LatestBar.SpreadBps > _limits.MaximumSpreadBps)
        {
            return Reject("SPREAD_LIMIT", "The current spread exceeds the configured limit.");
        }

        if (context.OpenOrderCount >= _limits.MaximumOpenOrders)
        {
            return Reject("OPEN_ORDER_LIMIT", "The account has reached its open-order limit.");
        }

        return null;
    }

    private static RiskDecision? CheckOrderShape(TradeIntent intent, RiskContext context)
    {
        if (intent.OrderType is OrderType.Limit or OrderType.StopLimit
            && (intent.LimitPrice is null || !context.Instrument.IsPriceValid(intent.LimitPrice.Value)))
        {
            return Reject("LIMIT_PRICE", "A valid limit price is required.");
        }

        if (intent.OrderType is OrderType.Stop or OrderType.StopLimit
            && (intent.StopPrice is null || !context.Instrument.IsPriceValid(intent.StopPrice.Value)))
        {
            return Reject("STOP_PRICE", "A valid stop price is required.");
        }

        return null;
    }

    private static RiskDecision Approve()
    {
        return new RiskDecision
        {
            IsApproved = true,
            Code = "APPROVED",
            Reason = "All configured pre-trade checks passed."
        };
    }

    private static RiskDecision Reject(string code, string reason)
    {
        return new RiskDecision { IsApproved = false, Code = code, Reason = reason };
    }
}
