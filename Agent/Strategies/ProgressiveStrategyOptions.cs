using Brokers.Models;

namespace Agent.Strategies;

public sealed record ProgressiveStrategyOptions
{
    public BarInterval TrendInterval { get; init; } = BarInterval.Hours(1);
    public BarInterval ConfirmationInterval { get; init; } = BarInterval.Minutes(15);
    public BarInterval EntryInterval { get; init; } = BarInterval.Minutes(5);
    public decimal Quantity { get; init; } = 1_000m;
    public decimal MinimumTrendConfidence { get; init; } = 55m;
    public decimal MinimumConfirmationConfidence { get; init; } = 55m;
    public decimal MinimumEntryConfidence { get; init; } = 58m;
    public decimal StopBufferAtr { get; init; } = 0.20m;
    public decimal FallbackStopAtr { get; init; } = 1.5m;
    public decimal FallbackTargetAtr { get; init; } = 3m;
    public decimal MinimumRewardRisk { get; init; } = 1.5m;
    public int MaximumEntryCandles { get; init; } = 3;

    public void Validate()
    {
        if (!TrendInterval.IsValid || !ConfirmationInterval.IsValid || !EntryInterval.IsValid)
            throw new ArgumentException("All progressive strategy intervals must be valid.");
        if (Quantity <= 0m) throw new ArgumentOutOfRangeException(nameof(Quantity));
        if (MinimumRewardRisk <= 0m) throw new ArgumentOutOfRangeException(nameof(MinimumRewardRisk));
        if (MaximumEntryCandles < 1) throw new ArgumentOutOfRangeException(nameof(MaximumEntryCandles));
    }
}
