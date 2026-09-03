namespace Agent.Strategies.Alfonso.Zones;

/// <summary>Which side of the market an imbalance represents.</summary>
public enum ImbalanceKind
{
    /// <summary>A demand zone: bearish leg in, base, bullish leg out. Price is expected to rise from it.</summary>
    Demand,

    /// <summary>A supply zone: bullish leg in, base, bearish leg out. Price is expected to fall from it.</summary>
    Supply
}

/// <summary>
/// What the impulse achieved. Course module 4: "An imbalance is created under these scenarios ...
/// 1. The break of a trendline with a full candlestick. 2. An opposing imbalance has been
/// eliminated. 3. All-time highs or all-time lows are eliminated by an impulse strong enough to
/// consolidate away."
/// <para>
/// Without one of these the impulse is not an imbalance at all, however good it looks. This is a
/// gate, not a score.
/// </para>
/// </summary>
[Flags]
public enum Accomplishment
{
    None = 0,
    TrendlineBreak = 1,
    OpposingImbalanceEliminated = 2,
    ExtremeBroken = 4,

    /// <summary>
    /// The move took out a prior peak or valley. Distinct from <see cref="ExtremeBroken"/>, which
    /// requires the all-time high or low: a zone qualifies if "the move from that zone broke a
    /// trendline or a peak / valley".
    /// </summary>
    SwingBroken = 8
}

/// <summary>
/// How price left the base. Module 7 ranks departures as gap, strong, weak, and scores a weak
/// departure at zero.
/// </summary>
public enum ImpulseStrength
{
    /// <summary>"The candles leaving the zone are usually weak ... Very low score = 0."</summary>
    Weak,

    /// <summary>Two or more extended-range candles away from the base.</summary>
    Strong,

    /// <summary>A gap away from the level - "the biggest representation of an imbalance".</summary>
    Gap
}

/// <summary>
/// Where a zone sits in its lifecycle. Module 7 lists the stages explicitly: fresh, being tested,
/// tested with a full candle consolidating away, tested a second time, used up.
/// </summary>
public enum ImbalanceState
{
    /// <summary>Never revisited. "The first pullback is always the highest odds."</summary>
    Fresh,

    /// <summary>One completed test. "The second pullback does not have the same odds."</summary>
    Tested,

    /// <summary>Two completed tests. "Taking a third pullback to a level is not allowed."</summary>
    UsedUp,

    /// <summary>Distal line penetrated. The zone no longer exists.</summary>
    Eliminated
}

/// <summary>
/// Where inside a zone the entry is planned. Module 10 gives both as options on the same imbalance.
/// </summary>
public enum ZoneEntryPlacement
{
    /// <summary>"Take the full imbalance based on your entry timeframe." Entry at the proximal line.</summary>
    Proximal,

    /// <summary>"Use half the width of the original imbalance." Entry half way to the distal line.</summary>
    Midpoint
}

/// <summary>
/// A supply or demand imbalance, as module 4 defines it: a basing structure bounded by a proximal
/// line (nearest current price) and a distal line (furthest), created by an impulse that
/// accomplished something and consolidated away.
/// </summary>
public sealed record Imbalance
{
    /// <summary>The timeframe this zone was detected on. Every timeframe carries its own zones.</summary>
    public required TimeSpan Interval { get; init; }

    public required ImbalanceKind Kind { get; init; }

    /// <summary>
    /// The edge nearest to price when the zone was created - the top of a demand base, the bottom of
    /// a supply base. Entries are planned here.
    /// </summary>
    public required decimal Proximal { get; init; }

    /// <summary>
    /// The far edge: the lowest low of a demand base, the highest high of a supply base. Module 4:
    /// "The distal line of an imbalance must always include the lowest low in the basing structure
    /// when drawing a demand level and the highest high when drawing a supply level." Protection
    /// goes beyond this line.
    /// </summary>
    public required decimal Distal { get; init; }

    /// <summary>Open time of the first basing candle.</summary>
    public required DateTimeOffset BaseStart { get; init; }

    /// <summary>Open time of the last basing candle.</summary>
    public required DateTimeOffset BaseEnd { get; init; }

    /// <summary>Open time of the candle that confirmed the zone, i.e. completed consolidation away.</summary>
    public required DateTimeOffset ConfirmedAt { get; init; }

    /// <summary>How many candles formed the base. Module 7 caps this at 4-6.</summary>
    public required int BaseCandleCount { get; init; }

    public required ImpulseStrength Strength { get; init; }

    public required Accomplishment Accomplished { get; init; }

    /// <summary>
    /// Height of the leg out divided by the zone width. Module 7 requires at least 2 - "The impulse
    /// created after the basing structure has to be twice as wide as the basing structure".
    /// </summary>
    public required decimal ImpulseToBaseRatio { get; init; }

    /// <summary>
    /// True when the leg in ran the same way as the leg out, making this a pause inside a move
    /// rather than a turn. Module 2 counts both as imbalances - "There are only two types.
    /// 1. Valleys and peaks (swing lows and swing highs). 2. Continuation patterns" - but module 3
    /// bars continuation patterns from trendline construction: "Continuation Patterns (CPs) will
    /// not be used to connect trendlines." The distinction has to survive detection to be available
    /// to the trend layer.
    /// </summary>
    public required bool IsContinuationPattern { get; init; }

    /// <summary>
    /// Whether the zone clears the tradeability bar, as opposed to merely existing. Module 7 is
    /// explicit that these are different things: "The only compulsory factors necessary for an
    /// imbalance are consolidation away, taking out opposing zone and/or breaking a trendline. A
    /// 2:1 RR is a minimum requirement for tradeability. It does not negate the level as a valid
    /// imbalance."
    /// <para>
    /// A valid but unqualified zone still eliminates opposing zones and still shapes the trend, so
    /// it must be tracked rather than discarded.
    /// </para>
    /// </summary>
    public required bool MeetsTradeabilityCriteria { get; init; }

    /// <summary>How far the leg out has carried price away from the proximal line.</summary>
    public required decimal ImpulseDisplacement { get; init; }

    /// <summary>
    /// Candles the leg out has run for. Bounds how long the departure keeps being re-scored: a zone
    /// is confirmed as soon as one full candle stands clear of the base, usually long before the
    /// move has finished, so the distance must keep accruing - but the SPEED of the departure is
    /// settled within the first candles and must not be.
    /// </summary>
    public required int ImpulseBarsTracked { get; init; }

    public ImbalanceState State { get; init; } = ImbalanceState.Fresh;

    /// <summary>Completed tests. Drives <see cref="State"/>; a third is not tradeable.</summary>
    public int TestCount { get; init; }

    /// <summary>Set when the distal line was penetrated, if it has been.</summary>
    public DateTimeOffset? EliminatedAt { get; init; }

    /// <summary>Zone width, always non-negative regardless of side.</summary>
    public decimal Width => Math.Abs(Proximal - Distal);

    /// <summary>
    /// True while the zone can still be traded on the set-and-forget path. A used-up or eliminated
    /// level needs confirmation the core rules do not grant.
    /// </summary>
    public bool IsTradeable =>
        MeetsTradeabilityCriteria && State is ImbalanceState.Fresh or ImbalanceState.Tested;

    /// <summary>
    /// Whether <paramref name="price"/> has reached the zone, i.e. crossed the proximal line into
    /// the band between proximal and distal.
    /// </summary>
    public bool Contains(decimal price) => Kind == ImbalanceKind.Demand
        ? price <= Proximal && price >= Distal
        : price >= Proximal && price <= Distal;

    /// <summary>
    /// Protection price: beyond the distal line by <paramref name="paddingFraction"/> of the zone
    /// width. Module 10 uses 25% padding on the worked examples.
    /// </summary>
    public decimal StopPrice(decimal paddingFraction)
    {
        decimal padding = Width * paddingFraction;
        return Kind == ImbalanceKind.Demand ? Distal - padding : Distal + padding;
    }

    /// <summary>
    /// Where the entry is planned inside the zone. Module 10: the full imbalance is entered at the
    /// proximal line, the half entry "around" the middle of the level.
    /// </summary>
    public decimal EntryPrice(ZoneEntryPlacement placement) => placement == ZoneEntryPlacement.Midpoint
        ? (Proximal + Distal) / 2m
        : Proximal;

    /// <summary>
    /// Target at <paramref name="rewardMultiple"/> times the risk, measured from the entry to the
    /// padded stop. Module 11: "Exit at a fixed target of 3:1, three times the width of the
    /// imbalance including the padding."
    /// </summary>
    public decimal TargetPrice(
        decimal paddingFraction,
        decimal rewardMultiple,
        ZoneEntryPlacement placement = ZoneEntryPlacement.Proximal)
    {
        decimal entry = EntryPrice(placement);
        decimal risk = Math.Abs(entry - StopPrice(paddingFraction));
        return Kind == ImbalanceKind.Demand
            ? entry + (risk * rewardMultiple)
            : entry - (risk * rewardMultiple);
    }
}
