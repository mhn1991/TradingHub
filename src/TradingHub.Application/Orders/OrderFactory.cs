using TradingHub.Domain.Trading;

namespace TradingHub.Application.Orders;

public sealed class OrderFactory
{
    private long _sequence;

    public OrderRequest Create(TradeIntent intent)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        return new OrderRequest
        {
            OrderId = $"ORD-{sequence:D12}",
            ClientOrderId = $"th-{sequence:D12}",
            IntentId = intent.IntentId,
            StrategyId = intent.StrategyId,
            AccountId = intent.AccountId,
            InstrumentId = intent.InstrumentId,
            Side = intent.Side,
            Quantity = intent.Quantity,
            OrderType = intent.OrderType,
            LimitPrice = intent.LimitPrice,
            StopPrice = intent.StopPrice,
            TimeInForce = intent.TimeInForce,
            CreatedAt = intent.CreatedAt
        };
    }
}

public sealed record OrderSubmissionRecord
{
    public required OrderRequest Request { get; init; }

    public required OrderSubmissionResult Result { get; init; }
}
