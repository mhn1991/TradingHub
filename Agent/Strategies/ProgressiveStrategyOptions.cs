using Brokers.Models;
using ChartAnnotator.Models;

namespace Agent.Strategies;

public enum PriceActionConfirmationMode
{
    Disabled,
    Soft,
    Required,
    /// <summary>
    /// Requires an LTF PA setup/trigger and an HTF context arm (see
    /// <see cref="MultiTimeframePriceActionPolicy"/>).
    /// </summary>
    RequiredWithContext
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
    /// <summary>Places targets just before structural barriers to improve fill realism.</summary>
    public decimal TargetBufferAtr { get; init; } = 0.10m;
    /// <summary>Ignores isolated micro-swing targets closer than this many entry-timeframe ATRs.</summary>
    public decimal MinimumSwingTargetDistanceAtr { get; init; } = 0.25m;
    public decimal MinimumZoneStrength { get; init; } = 40m;
    public decimal MinimumChannelConfidence { get; init; } = 40m;
    public decimal FallbackStopAtr { get; init; } = 1.5m;
    public decimal FallbackTargetAtr { get; init; } = 3m;
    public decimal MinimumRewardRisk { get; init; } = 1.5m;
    /// <summary>
    /// How many entry-interval bars are allowed after confirmation before the scoped
    /// setup expires. Default 12 (one hour on a 5m entry) — the previous default of 3
    /// was too tight for real market fills.
    /// </summary>
    public int MaximumEntryCandles { get; init; } = 12;
    /// <summary>
    /// How many primary-trend bars may elapse while waiting for confirmation before
    /// the progressive scope expires.
    /// </summary>
    public int MaximumTrendConfirmationBars { get; init; } = 3;
    public PriceActionConfirmationMode PriceActionConfirmation { get; init; } = PriceActionConfirmationMode.Soft;
    public decimal MinimumPriceActionConfidence { get; init; } = 55m;
    public bool RejectStrongOpposingPriceAction { get; init; } = true;
    /// <summary>
    /// Direction-aware RSI divergence/convergence and Bollinger %B/width context.
    /// Aligned evidence can confirm an entry; strong opposing evidence vetoes it.
    /// </summary>
    public RsiBollingerSignalOptions RsiBollingerSignals { get; init; } = new();
    /// <summary>
    /// Directional support/resistance and same-timeframe relative-volume confluence.
    /// These features strengthen an RSI relationship; neither volume nor a zone is
    /// treated as a standalone direction signal.
    /// </summary>
    public ZoneVolumeSignalOptions ZoneVolumeSignals { get; init; } = new();
    /// <summary>
    /// Multi-timeframe PA arms/vetoes. Used when
    /// <see cref="PriceActionConfirmation"/> is Soft, Required, or RequiredWithContext.
    /// </summary>
    public MultiTimeframePriceActionOptions MultiTimeframePriceAction { get; init; } = new();
    /// <summary>
    /// Allowed LTF composite setup types for entry gating. Empty = all core setups.
    /// </summary>
    public IReadOnlyList<PriceActionSetupType> AllowedPriceActionSetups { get; init; } = [];

    /// <summary>
    /// Interval whose <c>AnalysisSnapshot.MarketRegime</c> drives regime-based
    /// routing (see <see cref="MarketRegime"/>). Defaults to <see cref="TrendInterval"/>
    /// when unset - records cannot reference a sibling property as a default literal,
    /// so <see cref="EffectiveRegimeInterval"/> resolves the fallback.
    /// </summary>
    public BarInterval? RegimeInterval { get; init; }
    public BarInterval EffectiveRegimeInterval => RegimeInterval ?? TrendInterval;

    /// <summary>
    /// Regime-based entry gating and risk-multiplier sizing (spec §9). Disabled by
    /// default; when disabled, routing has no effect on decisions.
    /// </summary>
    public MarketRegimePolicyOptions MarketRegime { get; init; } = new();

    public IReadOnlyList<BarInterval> ConfirmationIntervals =>
        [ConfirmationInterval, .. AdditionalConfirmationIntervals];

    public IReadOnlyList<BarInterval> AllRequiredIntervals =>
        [
            EntryInterval,
            ConfirmationInterval,
            .. AdditionalConfirmationIntervals,
            .. SetupIntervals,
            .. SecondaryTrendIntervals,
            TrendInterval,
            .. RegimeInterval is BarInterval regimeInterval && regimeInterval != TrendInterval
                ? (BarInterval[])[regimeInterval]
                : []
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
        if (MinimumRewardRisk <= 0m || StopBufferAtr < 0m || TargetBufferAtr < 0m ||
            MinimumSwingTargetDistanceAtr < 0m ||
            MinimumZoneStrength is < 0m or > 100m ||
            MinimumChannelConfidence is < 0m or > 100m ||
            FallbackStopAtr <= 0m || FallbackTargetAtr <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumRewardRisk));
        }
        if (MaximumEntryCandles < 1) throw new ArgumentOutOfRangeException(nameof(MaximumEntryCandles));
        if (MaximumTrendConfirmationBars < 1)
            throw new ArgumentOutOfRangeException(nameof(MaximumTrendConfirmationBars));
        if (MinimumPriceActionConfidence is < 0m or > 100m ||
            MinimumTrendConfidence is < 0m or > 100m ||
            MinimumSecondaryTrendConfidence is < 0m or > 100m ||
            MinimumSetupConfidence is < 0m or > 100m ||
            MinimumConfirmationConfidence is < 0m or > 100m ||
            MinimumEntryConfidence is < 0m or > 100m)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumPriceActionConfidence));
        }

        ArgumentNullException.ThrowIfNull(MultiTimeframePriceAction);
        ArgumentNullException.ThrowIfNull(AllowedPriceActionSetups);
        ArgumentNullException.ThrowIfNull(RsiBollingerSignals);
        ArgumentNullException.ThrowIfNull(ZoneVolumeSignals);
        ArgumentNullException.ThrowIfNull(MarketRegime);
        MultiTimeframePriceAction.Validate();
        RsiBollingerSignals.Validate();
        ZoneVolumeSignals.Validate();
        MarketRegime.Validate();

        if (RegimeInterval is BarInterval regimeInterval && !regimeInterval.IsValid)
        {
            throw new ArgumentException("The regime interval, when set, must be valid.");
        }
    }
}
