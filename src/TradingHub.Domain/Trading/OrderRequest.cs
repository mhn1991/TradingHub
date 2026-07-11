namespace TradingHub.Domain.Trading;

public sealed record OrderRequest
{
    public required string OrderId { get; init; }

    public required string ClientOrderId { get; init; }

    public required string IntentId { get; init; }

    public required string StrategyId { get; init; }

    public required string AccountId { get; init; }

    public required string InstrumentId { get; init; }

    public required OrderSide Side { get; init; }

    public required decimal Quantity { get; init; }

    public required OrderType OrderType { get; init; }

    public decimal? LimitPrice { get; init; }

    public decimal? StopPrice { get; init; }

    public required TimeInForce TimeInForce { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record OrderCancellationRequest
{
    public required string AccountId { get; init; }

    public required string BrokerOrderId { get; init; }

    public required string InstrumentId { get; init; }

    public string? ClientOrderId { get; init; }
}
