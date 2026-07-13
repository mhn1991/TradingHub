namespace Agent.Execution;

public sealed record ExecutionOptions
{
    public bool BlockWhenAccountCannotTrade { get; init; } = true;
    public bool BlockDuplicateOpenOrders { get; init; } = true;
    public bool AllowPyramiding { get; init; }
    public decimal MinimumConfidence { get; init; }
    public decimal? MaximumAbsolutePositionQuantity { get; init; }
}
