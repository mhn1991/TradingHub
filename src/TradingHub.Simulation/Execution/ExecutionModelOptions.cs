namespace TradingHub.Simulation.Execution;

public sealed record ExecutionModelOptions
{
    public decimal SlippageBps { get; init; }

    public decimal CommissionBps { get; init; }

    public decimal? MaximumFillQuantityPerBar { get; init; }

    public void EnsureValid()
    {
        if (SlippageBps < 0m || CommissionBps < 0m || MaximumFillQuantityPerBar <= 0m)
        {
            throw new InvalidOperationException("Execution model values cannot be negative or zero.");
        }
    }
}
