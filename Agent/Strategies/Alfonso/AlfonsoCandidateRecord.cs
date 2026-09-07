using Agent.Strategies.Alfonso.Sequence;
using Agent.Strategies.Alfonso.Zones;
using Brokers.Models;

using Agent.Strategies.Alfonso.Trend;

namespace Agent.Strategies.Alfonso;

/// <summary>Why a candidate the agent looked at did not become a trade.</summary>
public enum CandidateOutcome
{
    /// <summary>The order was placed on this candidate.</summary>
    Entered,

    /// <summary>Price had not reached the proximal line yet, so the order would not have filled.</summary>
    NotReached,

    /// <summary>Further from price than orders are ever filled at.</summary>
    TooFarToFill,

    /// <summary>Fresh-level rule, without a confirmation to admit it.</summary>
    NotFresh,

    /// <summary>Round-trip cost would consume too much of the planned risk.</summary>
    CostTooHigh,

    /// <summary>Stop distance below the configured ATR floor.</summary>
    StopTooTight,

    /// <summary>Execution-timeframe volatility outside the permitted percentile band.</summary>
    AtrRegime,

    /// <summary>Stop or target resolved to the wrong side of the entry.</summary>
    DegenerateGeometry,

    /// <summary>A nearer candidate on the same bar was taken instead.</summary>
    Superseded,

    /// <summary>Opposing-zone targeting was requested but no confirmed zone lies ahead of entry.</summary>
    NoOpposingZone
}

/// <summary>
/// One candidate exactly as the agent saw it at decision time, whether or not it became a trade.
/// <para>
/// Realised trades alone cannot answer why the agent behaved as it did: they omit candidates that
/// lost to a nearer zone, ones blocked by an open position, ones that never filled, and every
/// rejection reason. That gap is not hypothetical - a research harness in this repo counted 49
/// entries where the agent took 87, and the divergence was only found by accident. It also makes
/// any future selection study possible, since a filter can only be evaluated against the population
/// it would have filtered.
/// </para>
/// </summary>
public sealed record AlfonsoCandidateRecord
{
    public required DateTimeOffset At { get; init; }
    public required InstrumentKey Instrument { get; init; }
    public required CandidateOutcome Outcome { get; init; }
    public required ImbalanceKind Side { get; init; }
    public required SequenceRole EntryTimeframe { get; init; }

    /// <summary>Trend on each timeframe of the sequence at the moment of the decision.</summary>
    public AlfonsoTrend? TopTrend { get; init; }

    public AlfonsoTrend? MiddleTrend { get; init; }

    public AlfonsoTrend? LowerTrend { get; init; }

    public AlfonsoTrend? ConfirmationTrend { get; init; }

    public DateTimeOffset? ConfirmationClosedAt { get; init; }

    public required decimal Proximal { get; init; }
    public required decimal Distal { get; init; }
    public required decimal Stop { get; init; }
    public required decimal Target { get; init; }
    public required decimal Risk { get; init; }
    public required decimal MarketPrice { get; init; }

    public required bool Nested { get; init; }
    public required bool IsContinuationPattern { get; init; }
    public required ImbalanceState State { get; init; }
    public required ImpulseStrength Strength { get; init; }
    public required Accomplishment Accomplished { get; init; }
    public required decimal ImpulseToBaseRatio { get; init; }
    public required decimal ImpulseDisplacement { get; init; }
    public required int BaseCandleCount { get; init; }

    /// <summary>
    /// Module 7's grade for this zone as of the decision. Null on records written before the scoring
    /// existed; the agent always populates it.
    /// </summary>
    public ZoneScore? Score { get; init; }

    /// <summary>Round-trip cost as a fraction of planned risk, when the platform supplied an estimate.</summary>
    public decimal? CostToRisk { get; init; }

    /// <summary>Stop distance in ATR of the execution timeframe.</summary>
    public decimal? StopAtrMultiple { get; init; }

    /// <summary>Where current ATR sits in its own recent history, 0 to 1.</summary>
    public decimal? AtrPercentile { get; init; }

    public decimal? RangePosition { get; init; }
    public required string Scenario { get; init; }
}
