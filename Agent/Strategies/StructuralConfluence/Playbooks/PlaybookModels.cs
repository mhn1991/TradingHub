using Agent.Models;
using ChartAnnotator.Liquidity;
using ChartAnnotator.Models;
using ChartAnnotator.SupplyDemand;
using ChartAnnotator.TargetManagement;

namespace Agent.Strategies.StructuralConfluence.Playbooks;

public sealed record MandatoryGate(string Name, bool Passed, decimal Quality, string ReasonCode);

public sealed record ConfidenceContribution(string Component, decimal Adjustment, string ReasonCode);

public sealed record StructuralGeometry
{
    public required bool IsValid { get; init; }
    public required decimal Entry { get; init; }
    public decimal? Stop { get; init; }
    public decimal? Target { get; init; }
    public decimal? RewardRisk { get; init; }
    public decimal Quality { get; init; }
    public string? StopSource { get; init; }
    public string? TargetSource { get; init; }
    public LiquidityPool? TargetPool { get; init; }
    public required string ReasonCode { get; init; }

    /// <summary>
    /// Adaptive target-map outputs (plan §3-§4). Null unless
    /// <see cref="StructuralConfluenceStrategyOptions.AdaptiveTargetManagement"/> is enabled, in
    /// which case v1's nearest-obstacle <see cref="Target"/>/<see cref="TargetSource"/> above are
    /// populated from <see cref="TargetPlan"/>'s selected terminal/projection for compatibility.
    /// </summary>
    public TradeExitPolicy? ExitPolicy { get; init; }
    public TradeTargetPlan? TargetPlan { get; init; }
}

public sealed record PlaybookEvaluation
{
    public required string PlaybookId { get; init; }
    public required string Version { get; init; }
    public required PriceActionDirection Direction { get; init; }
    public required StructuralSetupLifecycle Lifecycle { get; init; }
    public string? SetupId { get; init; }
    public Guid? PrimaryPoolId { get; init; }
    public Guid? PrimarySweepId { get; init; }
    public Guid? PrimaryZoneId { get; init; }
    public DateTimeOffset? CatalystAt { get; init; }
    public IReadOnlyList<MandatoryGate> MandatoryGates { get; init; } = [];
    public IReadOnlyList<string> SupportingEvidence { get; init; } = [];
    public IReadOnlyList<string> ConflictingEvidence { get; init; } = [];
    public IReadOnlyList<ConfidenceContribution> ConfidenceContributions { get; init; } = [];
    public decimal ContextQuality { get; init; }
    public decimal LocationQuality { get; init; }
    public decimal CatalystQuality { get; init; }
    public decimal TriggerQuality { get; init; }
    public decimal ConfirmationQuality { get; init; }
    public decimal GeometryQuality { get; init; }
    public decimal Confidence { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public required string ReasonCode { get; init; }
    public bool IsReady { get; init; }
    public StructuralGeometry? Geometry { get; init; }
    public LiquidityPool? Pool { get; init; }
    public LiquiditySweepEvent? Sweep { get; init; }
    public SupplyDemandZone? Zone { get; init; }
    public PriceActionEvent? TriggerEvent { get; init; }
    public PriceActionSetup? TriggerSetup { get; init; }
    public string CciConfirmationState { get; init; } = "Unavailable";

    public decimal MandatoryQualityFloor => MandatoryGates.Count == 0
        ? 0m
        : MandatoryGates.Min(item => item.Quality);
}

public sealed record PlaybookRuntimeState
{
    public StructuralSetupLifecycle Lifecycle { get; init; } = StructuralSetupLifecycle.Dormant;
    public string? SetupId { get; init; }
    public DateTimeOffset LastAvailableAt { get; init; } = DateTimeOffset.MinValue;
    public long LastSnapshotVersion { get; init; } = -1;
    public DateTimeOffset? ArmedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public PlaybookEvaluation? LastEvaluation { get; init; }
    /// <summary>
    /// Sticky across non-ready frames (only overwritten when a NEW ready candidate appears) -
    /// unlike <see cref="SetupId"/>, which gets clobbered back to null the moment the playbook
    /// goes dormant. Lets a playbook check "have I already signaled off this exact identity"
    /// even after the position it produced has closed and evaluation resumes: while a position
    /// is open, <c>StructuralConfluenceAgent</c> short-circuits before ever calling
    /// <c>Evaluate</c>, so this field is frozen at the identity that was actually traded for the
    /// whole holding period, then compared against on the first post-close evaluation.
    /// </summary>
    public string? LastReadySetupId { get; init; }
    /// <summary>
    /// Sticky in the same way as <see cref="LastReadySetupId"/> (only overwritten by a new ready
    /// evaluation) - unlike <see cref="LastEvaluation"/>'s own <c>CatalystAt</c>, which gets
    /// overwritten on every bar regardless of readiness. <see cref="IndicatorConfluencePlaybook"/>'s
    /// cooldown needs the timestamp of the last bar that actually went ready, not the last bar
    /// evaluated - reading <c>LastEvaluation.CatalystAt</c> directly let a non-ready bar's freshly
    /// minted catalyst keep pushing the cooldown window forward every subsequent bar, permanently
    /// locking the playbook out after its first trade.
    /// </summary>
    public DateTimeOffset? LastReadyCatalystAt { get; init; }
}
