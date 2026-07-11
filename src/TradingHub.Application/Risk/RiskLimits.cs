namespace TradingHub.Application.Risk;

public sealed record RiskLimits
{
    public bool TradingEnabled { get; init; } = true;

    public required decimal MaximumOrderQuantity { get; init; }

    public required decimal MaximumAbsolutePosition { get; init; }

    public required decimal MaximumSpreadBps { get; init; }

    public required int MaximumOpenOrders { get; init; }

    public bool AllowShortSelling { get; init; }

    public void EnsureValid()
    {
        if (MaximumOrderQuantity <= 0m || MaximumAbsolutePosition <= 0m)
        {
            throw new InvalidOperationException("Risk quantity limits must be positive.");
        }

        if (MaximumSpreadBps < 0m || MaximumOpenOrders < 0)
        {
            throw new InvalidOperationException("Risk spread and order limits cannot be negative.");
        }
    }
}
