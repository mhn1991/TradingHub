using Agent.Strategies.Alfonso.Ranges;
using Agent.Strategies.Alfonso.Trend;
using Agent.Strategies.Alfonso.Zones;

namespace Agent.Strategies.Alfonso;

/// <summary>
/// One timeframe's complete Set and Forget picture: its zones, its trendlines and its trend.
/// <para>
/// Module 9: "Each timeframe will have its trend and imbalances, completely independent from other
/// timeframes." One analyzer per timeframe, fed only that timeframe's closed candles, is what keeps
/// that independence real - the previous agent in this repo fed a 30m detector 2h bars and produced
/// a gate that could never disagree with the timeframe above it.
/// </para>
/// </summary>
public sealed class AlfonsoTimeframeAnalyzer
{
    private readonly ImbalanceDetector _zones;
    private readonly AlfonsoTrendDetector _trend;

    private readonly RangeOptions _rangeOptions;

    public AlfonsoTimeframeAnalyzer(
        TimeSpan interval,
        ImbalanceOptions? zoneOptions = null,
        AlfonsoTrendOptions? trendOptions = null,
        RangeOptions? rangeOptions = null)
    {
        Interval = interval;
        _zones = new ImbalanceDetector(interval, zoneOptions);
        _trend = new AlfonsoTrendDetector(trendOptions);
        _rangeOptions = rangeOptions ?? new RangeOptions();
        _rangeOptions.Validate();
        _zones.TrendlineBreakLookup = WasTrendlineBroken;
    }

    public TimeSpan Interval { get; }

    public IReadOnlyList<Imbalance> Zones => _zones.Zones;

    public AlfonsoTrendSnapshot Trend => _trend.Snapshot;

    /// <summary>Where price sat in this timeframe's range as of the last applied candle.</summary>
    public SupplyDemandRange Range { get; private set; } =
        SupplyDemandRange.Unavailable("No candles applied yet.");

    /// <summary>
    /// The zone currently in control, if any. Module 6: "An imbalance in control is an imbalance
    /// that has been tested (any number of times) and remains unbroken. It must be hit and unbroken
    /// to remain in control without an opposing zone gaining control."
    /// </summary>
    public ZoneInControl? InControl { get; private set; }

    public IReadOnlyList<Imbalance> TradeableZones(ImbalanceKind kind, decimal price) =>
        _zones.TradeableZones(kind, price);

    /// <summary>Whether this zone is sitting out a test the engine has not yet resolved.</summary>
    public bool HasPendingTest(Imbalance zone) => _zones.HasPendingTest(zone);

    /// <summary>
    /// Applies one CLOSED candle of this timeframe.
    /// <para>
    /// Order matters and is deliberate. Existing trendlines are checked against the closing candle
    /// first, so that same candle can count as an accomplishment for a zone it confirms. Zone
    /// events are then applied to the trend state and may create swings for later trendlines.
    /// </para>
    /// </summary>
    public ImbalanceDetectorUpdate Apply(AlfonsoBar bar)
    {
        _trend.PreviewBreaks(bar);
        ImbalanceDetectorUpdate update = _zones.Apply(bar);
        _trend.Apply(bar, update);
        UpdateControl(bar, update);
        Range = RangeCalculator.Compute(_zones.Zones, bar.Close, _rangeOptions);
        return update;
    }

    /// <summary>
    /// Maintains which zone holds control. Control passes on arrival at a proximal line, is lost
    /// when that zone breaks, and - per module 6's "without an opposing zone gaining control" - is
    /// taken by whichever side price reached most recently.
    /// </summary>
    private void UpdateControl(AlfonsoBar bar, ImbalanceDetectorUpdate update)
    {
        if (InControl is ZoneInControl held &&
            update.Eliminated.Any(zone => zone.BaseEnd == held.Zone.BaseEnd && zone.Kind == held.Kind))
        {
            InControl = null;
        }

        if (update.Touched.Count == 0)
            return;

        // When price reaches more than one level on the same candle, the nearest to the close is the
        // one it is actually sitting in.
        Imbalance nearest = update.Touched
            .OrderBy(zone => Math.Abs(zone.Proximal - bar.Close))
            .First();

        InControl = new ZoneInControl { Zone = nearest, Since = bar.OpenTime };
    }

    /// <summary>
    /// Whether a trendline whose break creates <paramref name="kind"/> was broken inside the impulse
    /// window. Breaking a bearish line upward creates demand; breaking a bullish line downward
    /// creates supply.
    /// </summary>
    private bool WasTrendlineBroken(DateTimeOffset from, DateTimeOffset to, ImbalanceKind kind)
    {
        TrendlineDirection wanted = kind == ImbalanceKind.Demand
            ? TrendlineDirection.Bearish
            : TrendlineDirection.Bullish;

        foreach ((DateTimeOffset at, TrendlineDirection direction) in _trend.RecentBreaks)
        {
            if (direction == wanted && at >= from && at <= to)
                return true;
        }

        return false;
    }
}
