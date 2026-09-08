using Agent.Strategies.Alfonso.Ranges;
using Agent.Strategies.Alfonso.Sequence;
using Agent.Strategies.Alfonso.Trend;
using Agent.Strategies.Alfonso.Zones;
using Brokers.Models;

namespace Agent.Strategies.Alfonso;

/// <summary>
/// Configuration for the Set and Forget supply and demand agent.
/// <para>
/// Every timeframe is a parameter. Module 8 lists five sequences and leaves the choice to the
/// trader, so the agent must be able to run any of them without a code change - the rules read
/// roles, never durations.
/// </para>
/// </summary>
public sealed record AlfonsoStrategyOptions
{
    /// <summary>Top timeframe: direction. "We will only trade in the direction of this chart."</summary>
    public BarInterval TopInterval { get; init; } = BarInterval.Hours(4);

    /// <summary>Middle timeframe: intermediate direction.</summary>
    public BarInterval MiddleInterval { get; init; } = BarInterval.Hours(1);

    /// <summary>Lower timeframe: execution. Orders are planned at this timeframe's zones.</summary>
    public BarInterval LowerInterval { get; init; } = BarInterval.Minutes(15);

    /// <summary>Opt-in protection beyond the extreme confirmed swing on the execution timeframe.</summary>
    public bool UseStructuralSwingStop { get; init; }

    /// <summary>Target the nearest confirmed opposing entry-timeframe zone; skip if none exists.</summary>
    public bool UseOpposingZoneTarget { get; init; }

    /// <summary>Opt-in: revalidate owned, unfilled limits on each closed 5m candle.</summary>
    public bool RevalidatePendingOnFiveMinute { get; init; }

    /// <summary>Opt-in buy filter: closed 5m high >= BB upper and (RSI > 70 or CCI >= 100).</summary>
    public bool BlockExhaustedBuysOnFiveMinute { get; init; }

    /// <summary>Optional NDJSON research log. Reversal observations never authorize orders.</summary>
    public string? ReversalShadowLogPath { get; init; }

    /// <summary>Closed execution candles containing eligible anchors; pivots require 2 bars each side.</summary>
    public int StructuralStopLookbackCandles { get; init; } = 48;

    public ImbalanceOptions Zones { get; init; } = new();

    public AlfonsoTrendOptions Trend { get; init; } = new();

    public RangeOptions Range { get; init; } = new();

    /// <summary>Core by default. The reversal-only experiment requires closed-candle entry confirmation.</summary>
    public AlfonsoEntryPolicy EntryPolicy { get; init; } = AlfonsoEntryPolicy.Core;

    public decimal Quantity { get; init; } = 1_000m;

    /// <summary>
    /// Whether an entry may be planned at a zone that has already been tested once. Module 7 says
    /// no on the set-and-forget path - "We will only trade the first pullback to an imbalance, that
    /// is, only fresh levels" - and a second pullback "require[s] new imbalances to be traded".
    /// </summary>
    public bool FreshLevelsOnly { get; init; } = true;

    /// <summary>
    /// Whether an entry must sit inside a higher-timeframe zone of the same side.
    /// <para>
    /// Module 11's first row - all three timeframes aligned, enter at the execution timeframe's own
    /// zones - needs no nesting, and on 3.5 years of gold that row is where the entire loss came
    /// from: standalone entries returned -0.4865R over 50 trades while nested ones returned +0.2512R
    /// over 37, and the split holds in both halves of the period. Module 9's own argument for
    /// nesting is risk, not selection - "a very powerful and mechanical way of lowering the risk in
    /// our entries" - so this being the strongest predictor available is not something the course
    /// claims.
    /// </para>
    /// </summary>
    public bool RequireNestedEntries { get; init; }

    /// <summary>
    /// Whether a higher timeframe holding an opposing zone in control blocks entries below it.
    /// Module 6: "When an imbalance on timeframe X has gained control, trading at timeframes smaller
    /// than X will not be allowed." Control was computed and enforced nowhere until this was wired.
    /// </summary>
    public bool RequireControlAgreement { get; init; } = true;

    /// <summary>
    /// Whether module 10's confirmation trades are taken alongside set-and-forget ones. Off by
    /// default because the core rules present set-and-forget as the path a beginner trades; on, a
    /// tested level nested in a live higher-timeframe zone becomes tradeable.
    /// </summary>
    public bool AllowConfirmationEntries { get; init; }

    /// <summary>
    /// Largest share of a trade's initial risk that estimated round-trip cost may consume. Zero
    /// disables the gate, which is the default.
    /// <para>
    /// This attacks the binding constraint rather than searching for signal. A fixed 3:1 target
    /// needs a 28.2% win rate to clear costs at M15 on gold and the method delivers 19.5%, and the
    /// shortfall is not uniform across trades: measured cost drag is 12.6% of R at the median zone
    /// but 30.5% at the tightest decile. The tightest stops are structurally unprofitable before
    /// price moves at all, and this removes them without touching Alfonso's structural stop.
    /// </para>
    /// </summary>
    public decimal MaximumCostToRiskFraction { get; init; }

    /// <summary>
    /// Smallest stop distance, in ATR of the execution timeframe, that a trade may have. Zero
    /// disables it. A second expression of the same concern as
    /// <see cref="MaximumCostToRiskFraction"/>, for runs where no cost estimate is available.
    /// </summary>
    public decimal MinimumStopAtrMultiple { get; init; }

    /// <summary>
    /// Smallest stop distance, in ATR of the TOP timeframe, that a trade may have. Zero disables it.
    /// <para>
    /// Module 8 gives the top timeframe the direction and module 10 sizes the stop from the zone,
    /// which on a drilled-down entry is a much smaller structure - so the thesis lives on one
    /// timeframe and the risk on another. Measured over 135 trades, the median stop was **0.21** of a
    /// 4h ATR and 93% sat under half of one, on trades whose direction came from that 4h chart
    /// (3.66). Module 10's fourth entry option is the book's own guard - "make sure you use the stop
    /// padding you would use for the bigger timeframe imbalance" - and 3.61 records it as unbuilt.
    /// </para>
    /// <para>
    /// This is the cruder ATR-shaped version of that rule, and it is off by default because it is a
    /// behaviour change with its own A/B, not a repair.
    /// </para>
    /// </summary>
    public decimal MinimumStopTopAtrMultiple { get; init; }

    /// <summary>
    /// ATR percentile band, measured against this instrument's own recent history on the execution
    /// timeframe, outside which entries are refused. Defaults to the full range, i.e. no veto.
    /// <para>
    /// A regime veto rather than an entry trigger - the course forbids indicators as triggers, and
    /// this does not create, price or time a trade. The low end excludes stretches so quiet that
    /// costs dominate the R the zone can offer; the high end excludes conditions where fills and
    /// slippage stop resembling the model.
    /// </para>
    /// </summary>
    public decimal MinimumAtrPercentile { get; init; }

    public decimal MaximumAtrPercentile { get; init; } = 1m;

    /// <summary>Candles of ATR history used to compute the percentile.</summary>
    public int AtrPercentileLookback { get; init; } = 200;

    /// <summary>
    /// Room the trade must have to the nearest opposing zone, as a multiple of its own risk. Zero
    /// disables the check.
    /// <para>
    /// Module 7 lists this alongside the 2:1 impulse and consolidation away: "A minimum 2:1
    /// imbalance and one full OHCL candle consolidating away from the level is needed, as well as
    /// 3:1 profit margin or more to the opposing level." It asks whether the target is REACHABLE -
    /// a demand entry with supply sitting 1.5R above cannot make a 3:1 target whatever the zone
    /// scores - and it was missing entirely, so every trade measured so far was taken without it,
    /// including ones that could not physically reach their target.
    /// </para>
    /// <para>
    /// Should not sit below <see cref="ImbalanceOptions.RewardMultiple"/>: requiring less room than
    /// the target needs would defeat the purpose.
    /// </para>
    /// </summary>
    public decimal MinimumProfitMarginMultiple { get; init; } = 3.0m;

    /// <summary>
    /// CSV path for decision-time candidate logging, or null to log nothing. Set by
    /// --alfonso-candidate-log.
    /// </summary>
    public string? CandidateLogPath { get; init; }

    /// <summary>
    /// CSV path for periodic zone-inventory snapshots, or null to log nothing. Set by
    /// --alfonso-inventory-log. Written once per top-timeframe bar.
    /// </summary>
    public string? InventoryLogPath { get; init; }

    /// <summary>
    /// Optional JSONL sink for the trend layer's decision-time state - trend, trendline and live
    /// imbalances per timeframe - written once per order placed. See <see cref="AlfonsoStructureLog"/>
    /// for why this cannot be recovered by replaying the analyzer outside the run (3.67).
    /// </summary>
    public string? StructureLogPath { get; init; }

    /// <summary>Optional continuous baseline/individual/combined trend audit on the live candle feed.</summary>
    public string? TrendAuditPath { get; init; }

    /// <summary>
    /// Distance from price, in ATR, inside which a resting order has a realistic chance of filling.
    /// Used only to label inventory snapshots. Measured fill rates: 21-35% inside 3 ATR, ~5% at 3-6,
    /// under 1.5% beyond 6, zero beyond 16.
    /// </summary>
    public decimal ReachableDistanceAtr { get; init; } = 6m;

    /// <summary>
    /// Maximum distance from price, in ATR, at which an order will be placed. 0 places at any
    /// distance, which is the behaviour every result before 2026-09-02 was measured under.
    /// <para>
    /// Default 3 as of 2026-09-02. A far order costs twice over. It squats the single order slot -
    /// the agent rests one order per instrument and holds it while that zone stays a valid candidate
    /// ahead of price, and a far zone stays valid a long time - and it loses more when it does fill.
    /// Matching all 127 baseline fills back to their placements: inside 3 ATR avgR -0.1810, beyond
    /// 3 ATR -0.4366.
    /// </para>
    /// <para>
    /// Measured over six instruments: baseline 127 trades at -0.2394 and -6,706; cap 6 gives 139 at
    /// -0.1750 and -4,954; cap 3 gives 142 at -0.1681 and -5,051. Note the trade count RISES as the
    /// cap tightens, which a filter cannot do - that is the freed slot, and it is how the occupancy
    /// defect was found. Releasing the slot instead of refusing the order
    /// (<see cref="RestingOrderReplacementAtr"/>) recovers only about a fifth of the gain, because
    /// blocking is the smaller of the two harms.
    /// </para>
    /// <para>
    /// This is a defect repair, not an edge: every configuration leaves 1 of 6 instruments positive
    /// and every confidence interval overlaps the baseline. Set 0 to restore the old behaviour.
    /// </para>
    /// </summary>
    public decimal MaximumPlacementDistanceAtr { get; init; } = 3m;

    /// <summary>
    /// Cancel a resting order when a candidate appears this many ATR nearer to price, so the nearer
    /// one can be taken instead. 0 keeps the original behaviour of holding the first commitment.
    /// <para>
    /// The agent rests one order per instrument and holds it while its zone remains a valid
    /// candidate ahead of price. A zone far from price stays valid for a long time - price rarely
    /// reaches it, and it lives until its distal breaks - so a far order squats the only slot and
    /// blocks nearer opportunities that appear later. This was found by accident: capping placement
    /// distance at 16 ATR was predicted to be a no-op, because zero fills were measured beyond 16,
    /// and instead it INCREASED trade counts on three of five instruments. Removing far candidates
    /// cannot create trades unless a far order was blocking a nearer one.
    /// </para>
    /// </summary>
    public decimal RestingOrderReplacementAtr { get; init; }

    /// <summary>
    /// Whether the platform may manage an open position - move the stop to break-even, or trail it -
    /// instead of leaving the bracket untouched.
    /// <para>
    /// Off by default, and off is what the book asks for. Module 11: "Do not move the stop loss to
    /// breakeven. It's either a win or a loss." Turning it on is a deliberate departure.
    /// </para>
    /// <para>
    /// The reason to test it: of 97 losing trades, 32 reached an average of +2.02R before returning
    /// to a full loss, and another 38 were stopped within six minutes by noise that immediately
    /// reversed. Replaying the price path over those trades, a break-even stop at +1R raised gross
    /// expectancy from +0.0709R to +0.1654R, and trailing 1R behind the peak raised the win rate
    /// from 26.8% to 48.0%. Both figures are in-sample and need confirming in a real run.
    /// </para>
    /// </summary>
    public bool AllowPositionManagement { get; init; }

    /// <summary>
    /// Whether an entry waits for the level to prove it held, instead of resting a limit at the
    /// proximal that fills on first touch.
    /// <para>
    /// A resting limit cannot tell a level that holds from one price is about to run straight
    /// through, so it takes every failure at full size. Measured over six instruments the agent
    /// placed 1,212 buy limits and 1,379 sell limits - near symmetric - but filled 2.89% of buys
    /// against 6.67% of sells, a 2.31x asymmetry that is the entire long/short skew: in a drifting
    /// market price walks into the limits facing the drift and away from the others. Only 4.9% of
    /// placed orders ever filled, so fill selection, not zone selection, decided what was traded.
    /// </para>
    /// <para>
    /// With this on, the candle must trade into the zone and close back out of it on the trade's
    /// side, without closing beyond the distal, and entry is at market on that close. The cost is a
    /// worse entry price: the stop still sits at the padded distal, so risk is measured from the
    /// close rather than from the proximal, and the target is recomputed to preserve the configured
    /// reward multiple. Off by default - this departs from the book's set-and-forget premise.
    /// </para>
    /// </summary>
    public bool RequireReversalConfirmation { get; init; }

    /// <summary>
    /// Bars of top-timeframe history used to judge drift, or 0 to ignore drift entirely.
    /// <para>
    /// Measured causally as the sign of the change in top-timeframe close over this many bars, so it
    /// only ever reads closed history. With <see cref="RequireDriftAlignment"/> on, only the side
    /// that agrees with the drift may be traded.
    /// </para>
    /// </summary>
    public int DriftLookbackCandles { get; init; } = 60;

    /// <summary>
    /// Whether a candidate must agree with the prevailing drift.
    /// <para>
    /// 3.32 established that intentions are near-symmetric while fills run 2.31:1 against us, because
    /// a drifting market brings price to the levels facing the drift far more often. 3.33 showed that
    /// changing the entry mechanics at the level cannot fix that - it made the asymmetry worse - so
    /// the remaining lever is to stop placing orders on the drift-favoured side at all.
    /// </para>
    /// <para>
    /// Off by default. Note this is a momentum overlay on a mean-reversion method, and the six-
    /// instrument window it was measured on is one where five of six instruments rose, which is
    /// exactly the sample in which such a filter flatters itself.
    /// </para>
    /// </summary>
    public bool RequireDriftAlignment { get; init; }

    /// <summary>
    /// Lowest grade module 7's scoring may return for a zone still to be traded.
    /// <para>
    /// Defaults to <see cref="ZoneGrade.Weak"/>, i.e. no gate, so the scoring is measured before it
    /// is trusted. Module 7 asks for exactly this instrument - "If the particular trade gets a
    /// passing score, it must be traded" - but never states where the pass mark sits, and 3.27 in
    /// PROJECT_STATE.md is the record of what happens in this strategy when a threshold is chosen to
    /// suit a sample. Raising it also tightens which higher-timeframe zones may host a nested entry,
    /// which is the second half of module 7's negation rule.
    /// </para>
    /// </summary>
    public ZoneGrade MinimumZoneGrade { get; init; } = ZoneGrade.Weak;

    /// <summary>
    /// Whether a nested entry requires its higher-timeframe host to be a valid imbalance rather than
    /// merely a tracked structure. Module 7: "A bigger timeframe impulse that doesn't become an
    /// imbalance negates lower timeframe imbalances." On by default - the alternative admits an entry
    /// leaning on something module 4 does not consider an imbalance at all.
    /// </summary>
    public bool RequireValidHost { get; init; } = true;

    public BarInterval? ConfirmationInterval => EntryPolicy == AlfonsoEntryPolicy.LowerTimeframeAligned
        ? BarInterval.Minutes(5) : null;

    public IReadOnlySet<BarInterval> RequiredIntervals
    {
        get
        {
            HashSet<BarInterval> intervals = [TopInterval, MiddleInterval, LowerInterval];
            if (ConfirmationInterval is BarInterval confirmation)
                intervals.Add(confirmation);
            return intervals;
        }
    }

    public TimeframeSequence Sequence => new()
    {
        Top = ToTimeSpan(TopInterval),
        Middle = ToTimeSpan(MiddleInterval),
        Lower = ToTimeSpan(LowerInterval)
    };

    /// <summary>
    /// Interval as a span, for the timeframe-agnostic engines. Months are approximated, which is
    /// safe because the span is only ever used to label and order timeframes, never to advance a
    /// clock - candles are advanced by their own open times.
    /// </summary>
    public static TimeSpan ToTimeSpan(BarInterval interval) => interval.Unit switch
    {
        BarUnit.Second => TimeSpan.FromSeconds(interval.Value),
        BarUnit.Minute => TimeSpan.FromMinutes(interval.Value),
        BarUnit.Hour => TimeSpan.FromHours(interval.Value),
        BarUnit.Day => TimeSpan.FromDays(interval.Value),
        BarUnit.Week => TimeSpan.FromDays(interval.Value * 7),
        BarUnit.Month => TimeSpan.FromDays(interval.Value * 30),
        _ => throw new ArgumentOutOfRangeException(nameof(interval))
    };

    public void Validate()
    {
        Zones.Validate();
        Range.Validate();
        Sequence.Validate();

        if (StructuralStopLookbackCandles is < 5 or > 10000)
            throw new InvalidOperationException("StructuralStopLookbackCandles must be between 5 and 10000.");

        if (!Enum.IsDefined(EntryPolicy))
            throw new InvalidOperationException("Unknown Alfonso entry policy.");
        if (RevalidatePendingOnFiveMinute && EntryPolicy != AlfonsoEntryPolicy.LowerTimeframeAligned)
            throw new InvalidOperationException("5m pending revalidation requires lower-aligned entry policy.");
        if (BlockExhaustedBuysOnFiveMinute && EntryPolicy != AlfonsoEntryPolicy.LowerTimeframeAligned)
            throw new InvalidOperationException("5m buy exhaustion filter requires lower-aligned entry policy.");
        if (ReversalShadowLogPath is not null && (string.IsNullOrWhiteSpace(ReversalShadowLogPath) ||
            EntryPolicy != AlfonsoEntryPolicy.LowerTimeframeAligned))
            throw new InvalidOperationException("Reversal shadow logging requires a path and lower-aligned entry policy.");
        if (EntryPolicy == AlfonsoEntryPolicy.LowerTimeframeAligned && LowerInterval != BarInterval.Minutes(15))
            throw new InvalidOperationException("Lower-timeframe alignment experiment requires 15m entries and 5m confirmation.");
        if (EntryPolicy == AlfonsoEntryPolicy.LowerTimeframeReversal && !RequireReversalConfirmation)
            throw new InvalidOperationException("Lower-timeframe reversal policy requires --alfonso-confirm-entry.");

        if (Quantity <= 0m)
            throw new InvalidOperationException("Quantity must be positive.");
        if (MaximumCostToRiskFraction is < 0m or >= 1m)
            throw new InvalidOperationException("MaximumCostToRiskFraction must be within [0, 1).");
        if (MinimumStopAtrMultiple < 0m)
            throw new InvalidOperationException("MinimumStopAtrMultiple cannot be negative.");
        if (MinimumAtrPercentile is < 0m or > 1m || MaximumAtrPercentile is < 0m or > 1m)
            throw new InvalidOperationException("ATR percentiles must be within [0, 1].");
        if (MinimumAtrPercentile >= MaximumAtrPercentile)
            throw new InvalidOperationException("MinimumAtrPercentile must be below MaximumAtrPercentile.");
        if (MinimumProfitMarginMultiple < 0m)
            throw new InvalidOperationException("MinimumProfitMarginMultiple cannot be negative.");
        if (AtrPercentileLookback < 2)
            throw new InvalidOperationException("AtrPercentileLookback must be at least 2.");
    }
}
