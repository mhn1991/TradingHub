using Brokers.Models;

namespace ChartAnnotator.Liquidity;

/// <summary>
/// Which side of price inferred resting orders sit on. Deliberately "inferred", never "known" -
/// this phase uses price inference only, no broker order-book data (blueprint §6.1).
/// </summary>
public enum LiquiditySide
{
    BuySide,
    SellSide
}

public enum LiquidityPoolType
{
    EqualHighs,
    EqualLows,
    SwingHigh,
    SwingLow,
    RangeHigh,
    RangeLow,
    PreviousSessionHigh,
    PreviousSessionLow,
    PreviousDayHigh,
    PreviousDayLow,
    PreviousWeekHigh,
    PreviousWeekLow,
    RoundNumber
}

/// <summary>
/// A sweep and an accepted breakout are different terminal interpretations of the same pool
/// (blueprint §6.3) - a pool never collapses both into a generic "touched" state.
/// </summary>
public enum LiquidityPoolState
{
    Forming,
    Active,
    Approached,
    Touched,
    Swept,
    Consumed,
    AcceptedBreak,
    Broken,
    Expired,
    Merged
}

public enum LiquidityEventType
{
    Approach,
    Touch,
    UnconfirmedPenetration,
    Sweep,
    AcceptedBreak,
    Retest,
    Consumption,
    Failure
}

/// <summary>
/// One price-inferred liquidity pool. "Price-inferred" - never marketed as visible order-book
/// liquidity (blueprint §2 correct terminology).
/// </summary>
public sealed record LiquidityPool
{
    public required Guid PoolId { get; init; }
    public required InstrumentKey Instrument { get; init; }
    public required BarInterval Interval { get; init; }

    public required LiquiditySide Side { get; init; }
    public required LiquidityPoolType Type { get; init; }

    public required decimal LowerPrice { get; init; }
    public required decimal UpperPrice { get; init; }
    public required decimal ReferencePrice { get; init; }

    public required DateTimeOffset OriginatedAt { get; init; }
    public required DateTimeOffset ConfirmedAt { get; init; }
    public required DateTimeOffset AvailableAt { get; init; }

    public required LiquidityPoolState State { get; init; }
    public required int SourcePointCount { get; init; }
    public required int TouchCount { get; init; }

    public required decimal EqualnessScore { get; init; }
    public required decimal VisibilityScore { get; init; }
    public required decimal CompressionScore { get; init; }
    public required decimal ProminenceScore { get; init; }
    public required decimal FreshnessScore { get; init; }
    public required decimal QualityScore { get; init; }

    public required IReadOnlyList<Guid> SourcePoolIds { get; init; }

    /// <summary>Confirmed swing pivots used by an equal-level or isolated-swing pool.</summary>
    public IReadOnlyList<DateTimeOffset> SourcePivotTimes { get; init; } = [];

    /// <summary>Populated for completed session/day/week references.</summary>
    public DateTimeOffset? ReferencePeriodStartedAt { get; init; }
    public DateTimeOffset? ReferencePeriodEndedAt { get; init; }

    public required long SnapshotVersion { get; init; }
    public required string ProfileHash { get; init; }
}

/// <summary>
/// One occurrence against a <see cref="LiquidityPool"/> (approach, touch, sweep, break, ...).
/// Append-only, mirroring <c>ChartAnnotator.SupplyDemand.SupplyDemandZoneEvent</c>.
/// </summary>
public sealed record LiquidityEvent
{
    public required Guid EventId { get; init; }
    public required Guid PoolId { get; init; }
    public required LiquidityEventType EventType { get; init; }
    public required LiquidityPoolState StateBefore { get; init; }
    public required LiquidityPoolState StateAfter { get; init; }
    public required decimal Price { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required DateTimeOffset AvailableAt { get; init; }
    public required long SnapshotVersion { get; init; }
}

/// <summary>
/// Detailed sweep record (blueprint §7.2) - a specialization of <see cref="LiquidityEvent"/> with
/// the extra fields needed to distinguish a sweep from an accepted breakout or an unconfirmed
/// penetration (blueprint §7.5: "Never reduce all three to 'touched'.").
/// </summary>
public sealed record LiquiditySweepEvent
{
    public required Guid SweepId { get; init; }
    public required Guid PoolId { get; init; }

    public required DateTimeOffset SweepStartedAt { get; init; }
    public required DateTimeOffset ConfirmedAt { get; init; }
    public required DateTimeOffset AvailableAt { get; init; }

    public required decimal ExtremePrice { get; init; }
    public required decimal PenetrationAtr { get; init; }

    public required bool ClosedBackInside { get; init; }
    public required bool ClosedBackBeyondOriginSide { get; init; }
    public required bool DisplacementConfirmed { get; init; }
    public required bool StructureShiftConfirmed { get; init; }

    public required decimal RejectionStrength { get; init; }
    public required decimal QualityScore { get; init; }
    public required long SnapshotVersion { get; init; }
}

/// <summary>
/// Coverage/health descriptor for one <see cref="LiquidityAnalysisSnapshot"/> publication -
/// diagnostic only, never fed into a pool's own <c>QualityScore</c>.
/// </summary>
public sealed record LiquidityAnalysisQuality
{
    public required bool AtrReady { get; init; }
    public required int ActivePoolCount { get; init; }
    public required int SuppressedCandidateCount { get; init; }
    public required DateTimeOffset LastEvaluatedAt { get; init; }

    public static LiquidityAnalysisQuality Empty { get; } = new()
    {
        AtrReady = false,
        ActivePoolCount = 0,
        SuppressedCandidateCount = 0,
        LastEvaluatedAt = DateTimeOffset.MinValue
    };
}

public sealed record LiquidityAnalysisSnapshot
{
    public required bool IsEnabled { get; init; }
    public required string ProfileHash { get; init; }
    public required long SnapshotVersion { get; init; }
    public required DateTimeOffset AvailableAt { get; init; }
    /// <summary>Bounded lifecycle view, including recently terminal pools needed by confluence and charts.</summary>
    public IReadOnlyList<LiquidityPool> Pools { get; init; } = [];
    public required IReadOnlyList<LiquidityPool> ActivePools { get; init; }
    public required IReadOnlyList<LiquidityEvent> RecentEvents { get; init; }
    public IReadOnlyList<LiquiditySweepEvent> RecentSweeps { get; init; } = [];
    public required LiquidityAnalysisQuality Quality { get; init; }

    public static LiquidityAnalysisSnapshot Disabled { get; } = new()
    {
        IsEnabled = false,
        ProfileHash = string.Empty,
        SnapshotVersion = 0,
        AvailableAt = DateTimeOffset.MinValue,
        Pools = [],
        ActivePools = [],
        RecentEvents = [],
        RecentSweeps = [],
        Quality = LiquidityAnalysisQuality.Empty
    };
}
