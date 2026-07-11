namespace TradingHub.Application.Strategies;

public sealed record StrategyContext
{
    public required string AccountId { get; init; }

    public required decimal NetPositionQuantity { get; init; }
}
