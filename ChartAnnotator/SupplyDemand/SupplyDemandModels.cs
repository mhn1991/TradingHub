using Brokers.Models;

namespace ChartAnnotator.SupplyDemand;

/// <summary>
/// Price-derived supply/demand base pattern. "Price-derived" because these are computed purely
/// from completed-candle geometry - never claimed as known institutional order placement.
/// </summary>
public enum SupplyDemandPattern
{
    DropBaseRally,
    RallyBaseRally,
    RallyBaseDrop,
    DropBaseDrop
}

public enum SupplyDemandZoneType
{
    Demand,
    Supply
}

/// <summary>
/// Append-only lifecycle. A zone's <see cref="SupplyDemandZone.State"/> only ever advances
/// through <see cref="SupplyDemandZoneEvent"/>s - never rewritten or deleted in place.
/// </summary>
public enum SupplyDemandZoneState
{
    Forming,
    ConfirmedFresh,
    Approached,
    Tested,
    PartiallyMitigated,
    Mitigated,
    Invalidated,
    Expired,
    Merged
}

public enum SupplyDemandZoneEventType
{
    Formed,
    Confirmed,
    Approached,
    Touched,
    PartiallyMitigated,
    Mitigated,
    Invalidated,
    Expired,
    Merged
}

/// <summary>
/// Immutable for the life of the zone (blueprint: "The boundary mode is immutable for the
/// zone") - a profile change never silently repaints an already-published zone's boundaries.
/// </summary>
public enum ZoneBoundaryMode
{
    FullWickRange,
    BodyToExtreme,
    DepartureOriginBody
}

public enum ZoneInvalidationMode
{
    CloseBeyondDistal,
    WickBeyondDistal,
    PenetrationThreshold
}

/// <summary>
/// One price-derived supply or demand zone. Every timestamp field keeps origin, confirmation,
/// and availability distinct so no consumer can observe the zone before <see cref="AvailableAt"/>
/// (blueprint non-negotiable: "No object may be visible before its confirmation/availability
/// time").
/// </summary>
public sealed record SupplyDemandZone
{
    public required Guid ZoneId { get; init; }
    public required InstrumentKey Instrument { get; init; }
    public required BarInterval Interval { get; init; }
    public required SupplyDemandZoneType Type { get; init; }
    public required SupplyDemandPattern Pattern { get; init; }

    public required decimal ProximalPrice { get; init; }
    public required decimal DistalPrice { get; init; }

    public required DateTimeOffset BaseStartedAt { get; init; }
    public required DateTimeOffset BaseEndedAt { get; init; }
    public required DateTimeOffset DepartureStartedAt { get; init; }
    public required DateTimeOffset ConfirmedAt { get; init; }
    public required DateTimeOffset AvailableAt { get; init; }

    public required SupplyDemandZoneState State { get; init; }
    public required int BaseCandleCount { get; init; }
    public required int TouchCount { get; init; }

    public required decimal DepartureAtr { get; init; }
    public required decimal DepartureEfficiency { get; init; }
    public required decimal BaseCompactness { get; init; }
    public required decimal ImbalanceRatio { get; init; }
    public required decimal PenetrationRatio { get; init; }
    public required decimal FreshnessScore { get; init; }
    public required decimal QualityScore { get; init; }

    public required bool BrokeStructure { get; init; }
    public required bool HasFairValueGap { get; init; }

    public required ZoneBoundaryMode BoundaryMode { get; init; }
    public required IReadOnlyList<Guid> SourceZoneIds { get; init; }

    public required long SnapshotVersion { get; init; }
    public required string ProfileHash { get; init; }
}

/// <summary>
/// One append-only state transition for a <see cref="SupplyDemandZone"/>. The full history for a
/// zone is the ordered set of events sharing its <see cref="ZoneId"/> - never mutate or delete an
/// existing event to reflect a later observation.
/// </summary>
public sealed record SupplyDemandZoneEvent
{
    public required Guid EventId { get; init; }
    public required Guid ZoneId { get; init; }
    public required SupplyDemandZoneEventType EventType { get; init; }
    public required SupplyDemandZoneState StateBefore { get; init; }
    public required SupplyDemandZoneState StateAfter { get; init; }
    public required decimal Price { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required DateTimeOffset AvailableAt { get; init; }
    public required long SnapshotVersion { get; init; }

    /// <summary>Set only for <see cref="SupplyDemandZoneEventType.Merged"/> - the surviving
    /// zone's ID, preserving merge lineage per blueprint §5.10 ("Preserve source IDs and merge
    /// lineage").</summary>
    public Guid? RelatedZoneId { get; init; }
}

/// <summary>
/// Coverage/health descriptor for one <see cref="SupplyDemandAnalysisSnapshot"/> publication -
/// diagnostic only, never fed into <c>QualityScore</c> (which stays per-zone).
/// </summary>
public sealed record SupplyDemandAnalysisQuality
{
    public required bool AtrReady { get; init; }
    public required int ActiveZoneCount { get; init; }
    public required int SuppressedCandidateCount { get; init; }
    public required DateTimeOffset LastEvaluatedAt { get; init; }

    public static SupplyDemandAnalysisQuality Empty { get; } = new()
    {
        AtrReady = false,
        ActiveZoneCount = 0,
        SuppressedCandidateCount = 0,
        LastEvaluatedAt = DateTimeOffset.MinValue
    };
}

public sealed record SupplyDemandAnalysisSnapshot
{
    public required bool IsEnabled { get; init; }
    public required string ProfileHash { get; init; }
    public required long SnapshotVersion { get; init; }
    public required DateTimeOffset AvailableAt { get; init; }
    /// <summary>Bounded lifecycle view, including recently terminal zones needed by charts and attribution.</summary>
    public IReadOnlyList<SupplyDemandZone> Zones { get; init; } = [];
    public required IReadOnlyList<SupplyDemandZone> ActiveZones { get; init; }
    public required IReadOnlyList<SupplyDemandZoneEvent> RecentEvents { get; init; }
    public required SupplyDemandAnalysisQuality Quality { get; init; }

    public static SupplyDemandAnalysisSnapshot Disabled { get; } = new()
    {
        IsEnabled = false,
        ProfileHash = string.Empty,
        SnapshotVersion = 0,
        AvailableAt = DateTimeOffset.MinValue,
        Zones = [],
        ActiveZones = [],
        RecentEvents = [],
        Quality = SupplyDemandAnalysisQuality.Empty
    };
}
