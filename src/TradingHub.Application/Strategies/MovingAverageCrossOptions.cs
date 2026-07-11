namespace TradingHub.Application.Strategies;

public sealed record MovingAverageCrossOptions
{
    public required string StrategyId { get; init; }

    public required int ShortPeriod { get; init; }

    public required int LongPeriod { get; init; }

    public required decimal OrderQuantity { get; init; }

    public void EnsureValid()
    {
        if (ShortPeriod < 1 || LongPeriod <= ShortPeriod || OrderQuantity <= 0m)
        {
            throw new InvalidOperationException("Moving-average strategy settings are invalid.");
        }
    }
}
