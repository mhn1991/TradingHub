namespace TradingHub.Domain.Trading;

public sealed record TradeIntent
{
    public required string IntentId { get; init; }

    public required string StrategyId { get; init; }

    public required string AccountId { get; init; }

    public required string InstrumentId { get; init; }

    public required OrderSide Side { get; init; }

    public required decimal Quantity { get; init; }

    public required OrderType OrderType { get; init; }

    public decimal? LimitPrice { get; init; }

    public decimal? StopPrice { get; init; }

    public TimeInForce TimeInForce { get; init; } = TimeInForce.GoodTilCancelled;

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }
}
