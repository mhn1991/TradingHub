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

    public AlfonsoSequenceAnalyzer(
        TimeframeSequence sequence,
        ImbalanceOptions? zoneOptions = null,
        AlfonsoTrendOptions? trendOptions = null,
        RangeOptions? rangeOptions = null,
        bool freshLevelsOnly = true)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        sequence.Validate();
        Sequence = sequence;
        _freshLevelsOnly = freshLevelsOnly;

        foreach ((SequenceRole role, TimeSpan interval) in sequence.All())
            _timeframes[role] = new AlfonsoTimeframeAnalyzer(interval, zoneOptions, trendOptions, rangeOptions);
    }

    public TimeframeSequence Sequence { get; }

    internal bool FreshLevelsOnly => _freshLevelsOnly;

    public AlfonsoTimeframeAnalyzer this[SequenceRole role] => _timeframes[role];

    /// <summary>Applies one CLOSED candle of the timeframe filling <paramref name="role"/>.</summary>
    public ImbalanceDetectorUpdate Apply(SequenceRole role, AlfonsoBar bar) =>
        _timeframes[role].Apply(bar);

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
                // fresh levels." A tested level needs confirmation the core rules do not grant.
                if (!AcceptsLevel(zone, _freshLevelsOnly))
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

                Imbalance? host = null;
                if (entry.NestedIn is SequenceRole hostRole)
                {
                    host = Nesting.FindHost(zone, _timeframes[hostRole].Zones);
                    if (host is null)
                        continue;
                }

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

    internal static bool AcceptsLevel(Imbalance zone, bool freshLevelsOnly) =>
        !freshLevelsOnly || zone.State == ImbalanceState.Fresh;
}
