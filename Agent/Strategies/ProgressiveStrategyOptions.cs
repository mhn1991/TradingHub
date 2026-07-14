using Brokers.Models;

namespace Agent.Strategies;

public enum PriceActionConfirmationMode
{
    Disabled,
    Soft,
    Required
}

/// <summary>
/// Role-based progressive strategy configuration. The original trend/confirmation/entry
/// fields remain the primary intervals; optional lists add secondary trend, setup and
/// additional confirmation evidence without requiring identical signals on every chart.
/// </summary>
public sealed record ProgressiveStrategyOptions
{
    public BarInterval TrendInterval { get; init; } = BarInterval.Hours(1);
    public IReadOnlyList<BarInterval> SecondaryTrendIntervals { get; init; } = [];
    public IReadOnlyList<BarInterval> SetupIntervals { get; init; } = [];
    public BarInterval ConfirmationInterval { get; init; } = BarInterval.Minutes(15);
    public IReadOnlyList<BarInterval> AdditionalConfirmationIntervals { get; init; } = [];
    public BarInterval EntryInterval { get; init; } = BarInterval.Minutes(5);
    public int MinimumSecondaryTrendAlignments { get; init; }
    public int MinimumSetupAlignments { get; init; }
    public int MinimumConfirmationAlignments { get; init; } = 1;
    public bool StrongOppositionVeto { get; init; } = true;

    public decimal Quantity { get; init; } = 1_000m;
    public decimal MinimumTrendConfidence { get; init; } = 55m;
    public decimal MinimumSecondaryTrendConfidence { get; init; } = 50m;
    public decimal MinimumSetupConfidence { get; init; } = 50m;
    public decimal MinimumConfirmationConfidence { get; init; } = 55m;
    public decimal MinimumEntryConfidence { get; init; } = 58m;
    public decimal StopBufferAtr { get; init; } = 0.20m;
    public decimal FallbackStopAtr { get; init; } = 1.5m;
    public decimal FallbackTargetAtr { get; init; } = 3m;
    public decimal MinimumRewardRisk { get; init; } = 1.5m;
    public int MaximumEntryCandles { get; init; } = 3;
    public PriceActionConfirmationMode PriceActionConfirmation { get; init; } = PriceActionConfirmationMode.Soft;
    public decimal MinimumPriceActionConfidence { get; init; } = 55m;
    public bool RejectStrongOpposingPriceAction { get; init; } = true;

    public IReadOnlyList<BarInterval> ConfirmationIntervals =>
        [ConfirmationInterval, .. AdditionalConfirmationIntervals];

    public IReadOnlyList<BarInterval> AllRequiredIntervals =>
        [
            EntryInterval,
            ConfirmationInterval,
            .. AdditionalConfirmationIntervals,
            .. SetupIntervals,
            .. SecondaryTrendIntervals,
            TrendInterval
        ];

    public void Validate()
    {
        if (!TrendInterval.IsValid || !ConfirmationInterval.IsValid || !EntryInterval.IsValid ||
            SecondaryTrendIntervals is null || SetupIntervals is null ||
            AdditionalConfirmationIntervals is null ||
            AllRequiredIntervals.Any(interval => !interval.IsValid))
        {
            throw new ArgumentException("All progressive strategy intervals must be valid.");
        }

        if (AllRequiredIntervals.Distinct().Count() != AllRequiredIntervals.Count)
            throw new ArgumentException("Each progressive timeframe role must use a unique interval.");
        if (BarIntervalParser.CompareDuration(EntryInterval, ConfirmationInterval) >= 0)
            throw new ArgumentException("Entry interval must be finer than the primary confirmation interval.");
        if (BarIntervalParser.CompareDuration(ConfirmationInterval, TrendInterval) >= 0)
            throw new ArgumentException("Primary confirmation interval must be finer than the primary trend interval.");
        if (SecondaryTrendIntervals.Any(interval =>
                BarIntervalParser.CompareDuration(interval, TrendInterval) >= 0 ||
                BarIntervalParser.CompareDuration(interval, ConfirmationInterval) <= 0))
        {
            throw new ArgumentException(
                "Secondary trend intervals must be finer than primary trend and coarser than confirmation.");
        }
        if (SetupIntervals.Any(interval =>
                BarIntervalParser.CompareDuration(interval, TrendInterval) >= 0 ||
                BarIntervalParser.CompareDuration(interval, ConfirmationInterval) <= 0))
        {
            throw new ArgumentException(
                "Setup intervals must be finer than trend and coarser than primary confirmation.");
        }
        if (AdditionalConfirmationIntervals.Any(interval =>
                BarIntervalParser.CompareDuration(interval, TrendInterval) >= 0 ||
                BarIntervalParser.CompareDuration(interval, EntryInterval) <= 0))
        {
            throw new ArgumentException(
                "Additional confirmation intervals must be finer than trend and coarser than entry.");
        }

        int secondaryCount = SecondaryTrendIntervals.Count;
        int setupCount = SetupIntervals.Count;
        int confirmationCount = 1 + AdditionalConfirmationIntervals.Count;
        if (MinimumSecondaryTrendAlignments < 0 ||
            MinimumSecondaryTrendAlignments > secondaryCount ||
            MinimumSetupAlignments < 0 || MinimumSetupAlignments > setupCount ||
            MinimumConfirmationAlignments < 1 ||
            MinimumConfirmationAlignments > confirmationCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MinimumConfirmationAlignments),
                "Minimum aligned timeframe counts cannot exceed the configured intervals.");
        }

        if (Quantity <= 0m) throw new ArgumentOutOfRangeException(nameof(Quantity));
        if (MinimumRewardRisk <= 0m) throw new ArgumentOutOfRangeException(nameof(MinimumRewardRisk));
        if (MaximumEntryCandles < 1) throw new ArgumentOutOfRangeException(nameof(MaximumEntryCandles));
        if (MinimumPriceActionConfidence is < 0m or > 100m ||
            MinimumTrendConfidence is < 0m or > 100m ||
            MinimumSecondaryTrendConfidence is < 0m or > 100m ||
            MinimumSetupConfidence is < 0m or > 100m ||
            MinimumConfirmationConfidence is < 0m or > 100m ||
            MinimumEntryConfidence is < 0m or > 100m)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumPriceActionConfidence));
        }
    }
}
