using Brokers.Models;

namespace ChartAnnotator.TargetManagement;

/// <summary>
/// Exit-policy routing selected for a structural-confluence-v2 decision
/// (Structural Indicator and Adaptive Target Management Plan §4.1).
/// </summary>
public enum TradeExitPolicy
{
    FixedStructuralTarget,
    PartialThenRunner,
    ManagedExpansion
}

/// <summary>Role assigned to a ranked target-map candidate (plan §3.2).</summary>
public enum TradeTargetRole
{
    Checkpoint,
    Terminal,
    HardBarrier,
    Projection
}

/// <summary>Origin of a target-map candidate (plan §3.1).</summary>
public enum TradeTargetSourceKind
{
    Swing,
    LiquidityPool,
    SupplyDemandZone,
    RMultipleProjection
}

/// <summary>Deterministic ranking tier assigned before distance is considered (plan §3.3).</summary>
public enum TradeTargetSignificanceTier
{
    TierA,
    TierB,
    TierC
}

/// <summary>
/// One ranked, causally-available objective on the outward target map (plan §3, §5.1). Immutable
/// once produced for a given <see cref="TradeTargetPlan"/> revision; a rebuild produces a new set
/// with new candidate identities where the underlying evidence changed.
/// </summary>
public sealed record TradeTargetCandidate
{
    public required string CandidateId { get; init; }
    public required string ClusterId { get; init; }
    public required TradeTargetSourceKind SourceKind { get; init; }
    public required string SourceId { get; init; }
    public required BarInterval SourceInterval { get; init; }
    /// <summary>
    /// The source's native lifecycle state name (e.g. a <c>LiquidityPoolState</c> or
    /// <c>SupplyDemandZoneState</c> value). Stored as a string because the candidate sources use
    /// different lifecycle enums and a swing/projection candidate has none.
    /// </summary>
    public required string LifecycleState { get; init; }
    public required decimal LowerBoundary { get; init; }
    public required decimal UpperBoundary { get; init; }
    /// <summary>The buffered price a bracket/logical order would actually use (plan §3.5).</summary>
    public required decimal ExecutionPrice { get; init; }
    public required TradeTargetRole Role { get; init; }
    public required TradeTargetSignificanceTier Tier { get; init; }
    public decimal Quality { get; init; }
    public decimal Prominence { get; init; }
    public decimal Freshness { get; init; }
    public int? PriorTouchCount { get; init; }
    public required decimal DistanceAtr { get; init; }
    /// <summary>Cost-adjusted reward multiple if this candidate were the exit (plan §3.7).</summary>
    public decimal? TargetR { get; init; }
    /// <summary>Constituent source identities when this candidate represents a cluster (plan §3.4).</summary>
    public IReadOnlyList<string> ConstituentSourceIds { get; init; } = [];
    public required DateTimeOffset AvailableAt { get; init; }
    public IReadOnlyList<string> ReasonCodes { get; init; } = [];
}

/// <summary>
/// Ordered outward target map plus the routing/partial parameters derived from it for one
/// structural-confluence-v2 trade (plan §3, §5.1). Persisted with the trade so restart recovery
/// and reporting can reconstruct exactly what was decided and why.
/// </summary>
public sealed record TradeTargetPlan
{
    public required int PlanVersion { get; init; }
    public required int Revision { get; init; }
    public required TradeExitPolicy ExitPolicy { get; init; }
    public required decimal OriginalEntry { get; init; }
    public required decimal OriginalStop { get; init; }
    public required decimal InitialRiskPrice { get; init; }
    public string? SelectedCheckpointId { get; init; }
    public string? SelectedTerminalId { get; init; }
    public required IReadOnlyList<TradeTargetCandidate> Candidates { get; init; }
    public required decimal PartialFraction { get; init; }
    public required decimal MinimumRunnerFraction { get; init; }
    public required decimal PlannedR { get; init; }
    public required decimal ConservativeOpportunityR { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset LastRevisedAt { get; init; }
    public string? LastRevisionReason { get; init; }
}

/// <summary>
/// One target-plan advancement event (acceptance/consumption of a candidate and the replacement
/// selection), recorded for audit and report visibility (plan §4.5, §5.1).
/// </summary>
public sealed record TargetPlanRevision
{
    public required int PreviousRevision { get; init; }
    public required int NewRevision { get; init; }
    public required string TriggeringEvent { get; init; }
    public string? ConsumedCandidateId { get; init; }
    public string? NewlySelectedCandidateId { get; init; }
    public required DateTimeOffset DecisionAt { get; init; }
}
