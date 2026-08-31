using Brokers.Models;
using TrendStatistics.Trading;
using TradingClassifier.Configuration;

namespace Agent.Strategies.TrendTactical;

/// <summary>
/// Options for the trend-gated tactical agent — see
/// <c>Books/Design — Trend-Gated Tactical Agent.md</c>.
/// </summary>
public sealed record TrendTacticalStrategyOptions
{
    /// <summary>
    /// Higher timeframe that supplies direction, phase and progress.
    /// <para>
    /// 2H, not the blueprint's 4H. PROJECT_STATE §2.18 walk-forwarded both over 16.5 years: 2H gave
    /// PF 1.694 / 8-of-12 positive windows / 8.11% max drawdown against 4H's 1.174 / 7-of-12 /
    /// 20.44%, and 2H barely degraded from its in-sample 1.704 while 4H collapsed from 1.802. The
    /// blueprint's choice of 4H is not supported by that data.
    /// </para>
    /// </summary>
    public BarInterval TrendInterval { get; init; } = BarInterval.Hours(2);

    /// <summary>Timeframe the classifier times entries on — the blueprint's "tactical" layer.</summary>
    public BarInterval TriggerInterval { get; init; } = BarInterval.Minutes(15);

    public ClassifierOptions Classifier { get; init; } = new();

    // ---- Rung A: the validated TrendStatistics entry -------------------------------------------
    // PROJECT_STATE §2.18's PF 1.694 came from SwingSignalGenerator over frozen profiles, not from
    // the raw detector. Before these existed the agent entered on EVERY eligible trigger bar, which
    // is a different strategy that merely shares a trend detector — so its results could not be
    // compared with the research result at all (review finding P1).

    /// <summary>
    /// Require the research swing-entry rule: enter only when the live trend's progress percentile
    /// is still below <see cref="EntryPercentile"/>. Off by default so enabling it is a measurable
    /// change rather than a silent redefinition.
    /// </summary>
    public bool UseSwingEntryRule { get; init; }

    /// <summary>Progress percentile below which an entry is still early enough to take.</summary>
    public decimal EntryPercentile { get; init; } = 0.05m;

    /// <summary>
    /// Exhaustion guard: refuse entry above this percentile even if other conditions pass. §2.18's
    /// per-window selections were scattered across the grid, so neither value is a settled optimum.
    /// </summary>
    public decimal MaximumEntryPercentile { get; init; } = 0.75m;

    /// <summary>PriceOnly / TimeOnly / PriceAndTime — the research mode.</summary>
    public SwingEntryMode SwingEntryMode { get; init; } = SwingEntryMode.PriceOnly;

    /// <summary>
    /// Completed trends required before a profile is trusted. Below this the agent does not trade:
    /// a percentile computed from four trends is noise, and §2.18 recorded windows of 1-4 trades
    /// producing profit factors of 17 and infinity.
    /// </summary>
    public int MinimumTrendSamples { get; init; } = 20;

    public decimal Quantity { get; init; } = 1_000m;

    /// <summary>
    /// Minimum classifier probability, in the trend's own direction, required to enter while the
    /// trend is Confirmed.
    /// </summary>
    public double BaseEntryThreshold { get; init; } = 0.55;

    /// <summary>
    /// Added to <see cref="BaseEntryThreshold"/> once the trend is Mature — blueprint §43's
    /// "higher confidence requirement" as the move ages.
    /// </summary>
    public double MatureThresholdStep { get; init; } = 0.10;

    /// <summary>
    /// When false the classifier is bypassed and every trend-aligned trigger bar is taken.
    /// <para>
    /// This is rung B of the design document's §5 validation ladder — "does the trend gate alone
    /// carry it?" — and it is also the only way to generate candidates before a model exists, since
    /// the builder falls back to a no-trade model without one. Rung C then asks whether the
    /// classifier beats this baseline; §3.13's lesson is that ML must beat the simple rule, not just
    /// the unfiltered source.
    /// </para>
    /// </summary>
    public bool RequireClassifierAgreement { get; init; } = true;

    /// <summary>
    /// Additional trend timeframes (minutes) that must not OPPOSE the primary direction.
    /// <para>
    /// The gate counterpart to feeding trend state to the model as features. Deliberately
    /// "must not oppose" rather than "must agree": a neutral or still-forming secondary trend is not
    /// evidence against the trade, and requiring positive agreement from every timeframe would cut
    /// the trade count hard — this agent already produces only ~29 trades in 41 days with a cooldown,
    /// and small samples are what have made every short-window result here unreadable.
    /// </para>
    /// </summary>
    public IReadOnlyList<int> SecondaryTrendMinutes { get; init; } = [];

    /// <summary>Trade while the trend is Candidate as well as Confirmed. Off by default: a
    /// candidate trend has not met the confirmation displacement yet.</summary>
    public bool TradeCandidatePhase { get; init; }

    /// <summary>
    /// Protective stop distance in ATR, used as a floor when structure gives a tighter level.
    /// <para>
    /// There is no target multiple here on purpose. §3.19 measured breakout-detector's fixed 3.0 ATR
    /// target as reachable by only 9.6% of trades, and every target multiple in the valid range
    /// still lost money. This agent exits on trend state instead, which is why it declares
    /// <c>ProtectiveStopAndStrategyExit</c>.
    /// </para>
    /// </summary>
    public decimal StopAtrMultiple { get; init; } = 1.5m;

    /// <summary>
    /// Trigger-timeframe bars to wait after a position closes before entering again.
    /// <para>
    /// Independent of <see cref="OneEntryPerTrend"/> so the two can be measured separately. Zero
    /// keeps the original behaviour, where a structural exit leaves the trend still Confirmed and
    /// the agent re-enters on the very next trigger bar — which is why PROJECT_STATE §3.22 recorded
    /// 84 trades from 7 detected trends.
    /// </para>
    /// </summary>
    public int ReentryCooldownBars { get; init; }

    /// <summary>
    /// Take at most one entry per detected higher-timeframe trend.
    /// <para>
    /// The stricter of the two controls: it makes trade count track trend count rather than
    /// re-entry frequency, which is what the blueprint's "one swing position per 4H trend" (§1)
    /// actually describes. Trends are identified by their structural start, so a genuinely new
    /// trend re-arms the agent.
    /// </para>
    /// </summary>
    public bool OneEntryPerTrend { get; init; }

    /// <summary>
    /// Place the stop from a predicted adverse excursion instead of a fixed ATR floor.
    /// <para>
    /// Off by default so it is a measurable A/B. Requires an <c>IStopPlacementModel</c>; without one
    /// the agent silently keeps the structural stop rather than pretending to use a model it does
    /// not have.
    /// </para>
    /// </summary>
    public bool UseMlStopPlacement { get; init; }

    /// <summary>
    /// Multiplier applied to the predicted adverse excursion. Above 1 places the stop BEYOND where
    /// the move is expected to reach against us, which is the point: a stop exactly at the expected
    /// adverse excursion is hit roughly half the time by construction.
    /// </summary>
    public decimal MlStopSafetyMultiple { get; init; } = 1.25m;

    /// <summary>Bounds on the predicted stop, so a degenerate prediction cannot size the risk.</summary>
    public decimal MinimumStopAtrMultiple { get; init; } = 0.75m;

    public decimal MaximumStopAtrMultiple { get; init; } = 4.0m;

    /// <summary>Exit an open position once the trend reaches Exhaustion.</summary>
    public bool ExitOnExhaustion { get; init; } = true;

    /// <summary>Exit an open position when the trend direction flips against it.</summary>
    public bool ExitOnTrendReversal { get; init; } = true;

    /// <summary>
    /// Secondary trend timeframes are included: the agent reads each from its OWN snapshot, so the
    /// host must actually produce them. Omitting them here would make the snapshot lookup miss
    /// silently and the gate would never fire — which is exactly how it behaved before this fix.
    /// </summary>
    public IReadOnlySet<BarInterval> RequiredIntervals =>
        new HashSet<BarInterval>(
            [TriggerInterval, TrendInterval, .. SecondaryTrendMinutes.Select(BarInterval.Minutes)]);

    public void Validate()
    {
        if (!TrendInterval.IsValid)
            throw new ArgumentOutOfRangeException(nameof(TrendInterval));
        if (!TriggerInterval.IsValid)
            throw new ArgumentOutOfRangeException(nameof(TriggerInterval));
        if (Quantity <= 0m)
            throw new ArgumentOutOfRangeException(nameof(Quantity));
        if (BaseEntryThreshold is <= 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(BaseEntryThreshold));
        if (MatureThresholdStep is < 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(MatureThresholdStep));
        if (StopAtrMultiple <= 0m)
            throw new ArgumentOutOfRangeException(nameof(StopAtrMultiple));
        if (EntryPercentile is < 0m or > 1m)
            throw new ArgumentOutOfRangeException(nameof(EntryPercentile));
        if (MaximumEntryPercentile <= EntryPercentile || MaximumEntryPercentile > 1m)
            throw new ArgumentOutOfRangeException(nameof(MaximumEntryPercentile));
        if (MinimumTrendSamples < 1)
            throw new ArgumentOutOfRangeException(nameof(MinimumTrendSamples));
        if (ReentryCooldownBars < 0)
            throw new ArgumentOutOfRangeException(nameof(ReentryCooldownBars));
        if (MlStopSafetyMultiple <= 0m)
            throw new ArgumentOutOfRangeException(nameof(MlStopSafetyMultiple));
        if (MinimumStopAtrMultiple <= 0m || MaximumStopAtrMultiple <= MinimumStopAtrMultiple)
            throw new ArgumentOutOfRangeException(nameof(MinimumStopAtrMultiple));
        Classifier.Validate();
    }
}
