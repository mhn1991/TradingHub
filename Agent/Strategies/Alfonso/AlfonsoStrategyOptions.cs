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

    public ImbalanceOptions Zones { get; init; } = new();

    public AlfonsoTrendOptions Trend { get; init; } = new();

    public RangeOptions Range { get; init; } = new();

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

    public IReadOnlySet<BarInterval> RequiredIntervals =>
        new HashSet<BarInterval> { TopInterval, MiddleInterval, LowerInterval };

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
