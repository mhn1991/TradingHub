using Agent.Strategies.Alfonso.Ranges;
using Agent.Strategies.Alfonso.Trend;
using Agent.Strategies.Alfonso.Zones;

namespace Agent.Strategies.Alfonso.Sequence;

/// <summary>A zone the rules currently permit an order at, with the reasoning that admitted it.</summary>
public sealed record TradeCandidate
{
    public required Imbalance Zone { get; init; }

    public required SequenceRole EntryTimeframe { get; init; }

    /// <summary>The higher-timeframe zone this one is nested in, when the scenario demanded one.</summary>
    public Imbalance? Host { get; init; }

    public required ImbalanceKind Side { get; init; }

    public required string Reason { get; init; }
}

/// <summary>
/// A full top-down analysis across three timeframes (modules 8, 9 and 11).
/// <para>
/// Each timeframe is analysed independently and advanced only by its own closed candles - module 9:
/// "Each timeframe will have its trend and imbalances, completely independent from other
/// timeframes." This matters more than it sounds: a previous agent in this repo fed its 30m detector
/// 2h candles, which turned the 30m gate into a second copy of the 2h one, so it agreed with the
/// timeframe above it on every bar and rejected nothing. Three configurations produced byte-
/// identical results, which is what a silent no-op looks like from outside.
/// </para>
/// </summary>
public sealed class AlfonsoSequenceAnalyzer
{
    private readonly Dictionary<SequenceRole, AlfonsoTimeframeAnalyzer> _timeframes = [];
    private readonly bool _freshLevelsOnly;
    private readonly bool _confirmationEntryMode;
    private readonly bool _requireControlAgreement;
    private readonly bool _allowConfirmationEntries;
    private readonly decimal _minimumProfitMargin;
    private readonly decimal _stopPadding;

    public AlfonsoSequenceAnalyzer(
        TimeframeSequence sequence,
        ImbalanceOptions? zoneOptions = null,
        AlfonsoTrendOptions? trendOptions = null,
        RangeOptions? rangeOptions = null,
        bool freshLevelsOnly = true,
        bool requireControlAgreement = true,
        bool allowConfirmationEntries = false,
        decimal minimumProfitMarginMultiple = 0m,
        decimal stopPaddingFraction = 0.25m,
        bool confirmationEntryMode = false)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        sequence.Validate();
        Sequence = sequence;
        _freshLevelsOnly = freshLevelsOnly;
        _confirmationEntryMode = confirmationEntryMode;
        _requireControlAgreement = requireControlAgreement;
        _allowConfirmationEntries = allowConfirmationEntries;
        _minimumProfitMargin = minimumProfitMarginMultiple;
        _stopPadding = stopPaddingFraction;

        foreach ((SequenceRole role, TimeSpan interval) in sequence.All())
            _timeframes[role] = new AlfonsoTimeframeAnalyzer(interval, zoneOptions, trendOptions, rangeOptions);
    }

    public TimeframeSequence Sequence { get; }

    internal bool FreshLevelsOnly => _freshLevelsOnly;

    public AlfonsoTimeframeAnalyzer this[SequenceRole role] => _timeframes[role];

    /// <summary>Applies one CLOSED candle of the timeframe filling <paramref name="role"/>.</summary>
    public ImbalanceDetectorUpdate Apply(SequenceRole role, AlfonsoBar bar) =>
        _timeframes[role].Apply(bar);

    /// <summary>Live zones on one timeframe, for inventory measurement.</summary>
    public IReadOnlyList<Imbalance> ZonesOf(SequenceRole role) => _timeframes[role].Zones;

    /// <summary>Trend on one timeframe of the sequence, for decision-time logging.</summary>
    public AlfonsoTrend TrendOf(SequenceRole role) => _timeframes[role].Trend.Trend;

    /// <summary>The alignment currently in force, resolved against module 11's table.</summary>
    public ScenarioResolution Scenario => ScenarioMatrix.Resolve(
        _timeframes[SequenceRole.Top].Trend.Trend,
        _timeframes[SequenceRole.Middle].Trend.Trend,
        _timeframes[SequenceRole.Lower].Trend.Trend);

    /// <summary>
    /// Zones the rules permit an order at right now, nearest to price first.
    /// <para>
    /// Empty is the normal answer. Module 10's whole framing is the waiting game - "Trading is all
    /// about waiting and being patient" - so an empty list is the rules working, not a failure.
    /// </para>
    /// </summary>
    public IReadOnlyList<TradeCandidate> Candidates(decimal price)
    {
        ScenarioResolution scenario = Scenario;
        if (!scenario.CanTrade || scenario.Side is not ImbalanceKind side)
            return [];

        bool buying = side == ImbalanceKind.Demand;

        // Module 6: "The bigger timeframe always comes first, and in the long run, the bigger
        // timeframe will win most of the time." Every timeframe in the sequence has to permit the
        // direction, not just the one the order sits on.
        foreach ((SequenceRole role, _) in Sequence.All())
        {
            SupplyDemandRange range = _timeframes[role].Range;
            if (buying ? !range.AllowsBuying : !range.AllowsSelling)
                return [];
        }

        // Module 6: "When an imbalance on timeframe X has gained control, trading at timeframes
        // smaller than X will not be allowed. For instance, if weekly supply is in control, no longs
        // will be allowed on timeframes smaller than the weekly."
        //
        // Control was being computed and then read by nothing - the rule existed in the engine and
        // was enforced nowhere, which is the same silent no-op shape as the other defects found in
        // this subsystem.
        if (_requireControlAgreement)
        {
            foreach ((SequenceRole role, _) in Sequence.All())
            {
                if (role == SequenceRole.Lower)
                    continue;

                if (_timeframes[role].InControl is ZoneInControl held && held.Kind != side)
                    return [];
            }
        }

        List<TradeCandidate> candidates = [];

        foreach (ScenarioEntry entry in scenario.Entries)
        {
            AlfonsoTimeframeAnalyzer timeframe = _timeframes[entry.EntryTimeframe];

            // Module 5: "Once a certain timeframe is over-extended, that timeframe can no longer be
            // used to place a trade."
            if (timeframe.Trend.IsOverExtended)
                continue;

            foreach (Imbalance zone in timeframe.TradeableZones(side, price))
            {
                // Module 7: "We will only trade the first pullback to an imbalance, that is, only
                // fresh levels." A tested level needs confirmation - which module 10 defines and
                // which IsConfirmed supplies when it is enabled.
                Imbalance? host = null;
                if (entry.NestedIn is SequenceRole hostRole)
                {
                    host = Nesting.FindHost(zone, _timeframes[hostRole].Zones);
                    if (host is null)
                        continue;
                }

                // Fresh levels take the set-and-forget path. A tested one needs confirmation, and
                // that means a host: the bigger-timeframe imbalance the new zone was created at.
                // Confirmation entry acts on the first pullback rather than ahead of it, so the
                // zone is necessarily Tested by the time the entry is judged. Fresh-only would drop
                // every such zone from this list before the agent ever saw it - which it did, giving
                // 2 trades across six instruments and 80,422 candidates rejected for never having
                // been reached. Module 7's "first pullback only" is still honoured: TestCount 1.
                bool firstPullback = _confirmationEntryMode &&
                    zone.State == ImbalanceState.Tested && zone.TestCount <= 1;
                bool acceptable = AcceptsLevel(zone, _freshLevelsOnly) || firstPullback ||
                    (_allowConfirmationEntries && IsConfirmed(zone, host));
                if (!acceptable)
                    continue;

                // A zone stays plannable until price breaks through its distal - at which point the
                // zone engine has eliminated it anyway.
                //
                // The test used to be "the proximal is still ahead of price", which quietly excluded
                // the one bar that matters: a demand zone at 100 that price dips into and closes at
                // 99.5 inside is no longer "ahead", so it was dropped on the exact bar price
                // arrived. Entries only survived on bars that wicked into a zone and closed back
                // above it, which is a small fraction of arrivals.
                bool live = side == ImbalanceKind.Demand ? price > zone.Distal : price < zone.Distal;
                if (!live)
                    continue;

                if (!HasRoomToTarget(zone, side, timeframe.Zones))
                    continue;

                candidates.Add(new TradeCandidate
                {
                    Zone = zone,
                    EntryTimeframe = entry.EntryTimeframe,
                    Host = host,
                    Side = side,
                    Reason = $"{scenario.Reason} {entry.Description}"
                });
            }
        }

        return candidates
            .OrderBy(candidate => Math.Abs(candidate.Zone.Proximal - price))
            .ToArray();
    }

    /// <summary>
    /// Whether the nearest opposing zone leaves enough room for the target to be reached.
    /// <para>
    /// Module 7 requires "3:1 profit margin or more to the opposing level" alongside the 2:1 impulse
    /// and consolidation away. It is a reachability test, not a quality one: a demand entry with
    /// supply 1.5R above it cannot make a 3:1 target however well the zone scores.
    /// </para>
    /// <para>
    /// Only zones on the SAME timeframe are considered obstacles, and an absent opposing zone is
    /// treated as open road - module 6 says the same of the range, that with nothing opposing there
    /// is no measurement to make rather than a prohibition.
    /// </para>
    /// </summary>
    private bool HasRoomToTarget(Imbalance zone, ImbalanceKind side, IReadOnlyList<Imbalance> onTimeframe)
    {
        if (_minimumProfitMargin <= 0m)
            return true;

        decimal risk = Math.Abs(zone.Proximal - zone.StopPrice(_stopPadding));
        if (risk <= 0m)
            return false;

        ImbalanceKind opposing = side == ImbalanceKind.Demand
            ? ImbalanceKind.Supply
            : ImbalanceKind.Demand;

        // The first opposing level price would meet on the way to target is the one that matters.
        decimal? obstacle = onTimeframe
            .Where(other => other.Kind == opposing &&
                other.State != ImbalanceState.Eliminated &&
                (side == ImbalanceKind.Demand
                    ? other.Proximal > zone.Proximal
                    : other.Proximal < zone.Proximal))
            .Select(other => (decimal?)other.Proximal)
            .DefaultIfEmpty(null)
            .Aggregate((a, b) => side == ImbalanceKind.Demand
                ? (a is null ? b : b is null ? a : Math.Min(a.Value, b.Value))
                : (a is null ? b : b is null ? a : Math.Max(a.Value, b.Value)));

        if (obstacle is not decimal level)
            return true;

        return Math.Abs(level - zone.Proximal) >= risk * _minimumProfitMargin;
    }

    internal static bool AcceptsLevel(Imbalance zone, bool freshLevelsOnly) =>
        !freshLevelsOnly || zone.State == ImbalanceState.Fresh;

    /// <summary>
    /// Whether a level that is no longer fresh may still be traded, because module 10's confirmation
    /// has arrived.
    /// <para>
    /// Module 10 splits entries in two, and only the first half was implemented. Set-and-forget
    /// trades need a fresh level and the trend behind them. Confirmation trades are the rest -
    /// "the level is wicky, tested or used-up, the trend is not with us ... you just wait for a
    /// confirmation" - and module 7 agrees that non-fresh levels "can also work, but rules do not
    /// allow us to take them unless there is confirmation in lower timeframes". Rejecting every
    /// tested level outright therefore tested half the method, not the method.
    /// </para>
    /// <para>
    /// The confirmation itself is module 10's definition: "a brand new imbalance created at a bigger
    /// timeframe imbalance or a bigger timeframe confluence." So a tested level qualifies when it
    /// sits inside a higher-timeframe zone that is still live - the bigger-timeframe imbalance the
    /// new one was created at. A used-up level never qualifies: module 7 puts a third pullback out
    /// of bounds regardless.
    /// </para>
    /// </summary>
    private static bool IsConfirmed(Imbalance zone, Imbalance? host) =>
        zone.State == ImbalanceState.Tested &&
        host is not null &&
        host.State != ImbalanceState.Eliminated;
}
