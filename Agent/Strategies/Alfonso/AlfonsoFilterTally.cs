using Agent.Strategies.Alfonso.Zones;

namespace Agent.Strategies.Alfonso;

/// <summary>
/// Counts, per side, which gate in <c>Candidates</c> discarded each zone.
/// <para>
/// Measured inventory near price is symmetric - nearest demand and nearest supply within 0.1 ATR on
/// every instrument - yet placements within reachable distance run 1.88:1 supply-heavy. The
/// asymmetry is therefore introduced between the zone engine and the candidate list, which is our
/// own filtering rather than anything the market does. This says which filter.
/// </para>
/// </summary>
public sealed class AlfonsoFilterTally
{
    /// <summary>The scenario permitted no trading at all, so no side was even considered.</summary>
    public long ScenarioBlocked { get; set; }

    /// <summary>Module 6's 20/80 range refused this direction on some timeframe.</summary>
    public long RangeBlocked { get; set; }

    /// <summary>An opposing zone held control on a higher timeframe.</summary>
    public long ControlBlocked { get; set; }

    /// <summary>The entry timeframe was over-extended.</summary>
    public long OverExtended { get; set; }

    /// <summary>
    /// The three conditions inside <c>TradeableZones</c>, split, and each split again by whether the
    /// zone was near enough to price to matter. Near is what decides behaviour: fill rate runs
    /// 21-35% inside 3 ATR and is zero beyond 16, and the whole asymmetry turned out to be that
    /// filters remove the NEAR demand zones while passing both sides at a similar overall rate.
    /// </summary>
    public long BarNear { get; set; }

    public long BarFar { get; set; }

    public long StateNear { get; set; }

    public long StateFar { get; set; }

    public long PendingNear { get; set; }

    public long PendingFar { get; set; }

    public long PassedNear { get; set; }

    public long NotTradeable => BarNear + BarFar + StateNear + StateFar + PendingNear + PendingFar;

    /// <summary>Nesting was required and no host zone was found.</summary>
    public long NoHost { get; set; }

    /// <summary>Freshness: not Fresh, and no confirmation route admitted it.</summary>
    public long NotAccepted { get; set; }

    /// <summary>Price had already broken the distal, so the zone was no longer plannable.</summary>
    public long NotLive { get; set; }

    /// <summary>Module 7's profit-margin rule found no room to the opposing level.</summary>
    public long NoRoom { get; set; }

    /// <summary>Survived every gate and became a candidate.</summary>
    public long Passed { get; set; }

    public long Considered =>
        NotTradeable + NoHost + NotAccepted + NotLive + NoRoom + Passed;
}

/// <summary>One cumulative tally snapshot for one side.</summary>
public sealed record AlfonsoFilterSnapshot
{
    public required DateTimeOffset At { get; init; }
    public required Brokers.Models.InstrumentKey Instrument { get; init; }
    public required ImbalanceKind Side { get; init; }
    public required AlfonsoFilterTally Tally { get; init; }
}
