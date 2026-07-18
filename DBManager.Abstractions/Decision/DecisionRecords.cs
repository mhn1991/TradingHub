namespace DBManager.Abstractions.Decision;

/// <summary>
/// Immutable record a producer creates and hands to the operational channel (section 1.2).
/// Carries its own <see cref="DecisionId"/> so the batch writer can insert into
/// <c>decision.decision_keys</c> and <c>decision.agent_evaluations</c> together.
/// </summary>
public sealed record AgentEvaluationRecord
{
    public required Guid DecisionId { get; init; }
    public required Guid DeploymentId { get; init; }
    public required Guid PolicyRevisionId { get; init; }
    public required long InstrumentId { get; init; }
    public required string StrategyId { get; init; }
    public required DateTimeOffset DecisionTime { get; init; }
    public required DateTimeOffset ReceivedAt { get; init; }
    public required AgentEvaluationAction Action { get; init; }
    public required AgentState StateBefore { get; init; }
    public required AgentState StateAfter { get; init; }
    public required TriggerInterval TriggerInterval { get; init; }
    public required long SnapshotVersion { get; init; }
    public decimal? RawConfidence { get; init; }
    public decimal? MtfAlignment { get; init; }
    public Regime? Regime { get; init; }
    public required string PrimaryReasonCode { get; init; }
    public required string DiagnosticsJson { get; init; }
}

public sealed record TradeCandidateRecord
{
    public required Guid CandidateId { get; init; }
    public required Guid DecisionId { get; init; }
    public required string SetupId { get; init; }
    public required Guid DeploymentId { get; init; }
    public required Guid PolicyRevisionId { get; init; }
    public required long InstrumentId { get; init; }
    public required string StrategyId { get; init; }
    public required TradeDirection Direction { get; init; }
    public required DateTimeOffset DecisionTime { get; init; }
    public decimal? ReferenceBid { get; init; }
    public decimal? ReferenceAsk { get; init; }
    public required decimal ReferenceMid { get; init; }
    public required decimal StopPrice { get; init; }
    public decimal? TargetPrice { get; init; }
    public required decimal RawConfidence { get; init; }
    public required decimal MtfAlignment { get; init; }
    public required Regime Regime { get; init; }
    public required string SetupCalibrationAuditJson { get; init; }
    public required string MetaLabelAuditJson { get; init; }
    public required string TradingConditionAuditJson { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>Append-only funnel entry (section 7.4) — the record of one stage's pass/reject/defer.</summary>
public sealed record CandidateStageEventRecord
{
    public required Guid CandidateId { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required CandidateStage Stage { get; init; }
    public required StageOutcome Outcome { get; init; }
    public required string ReasonCode { get; init; }
    public decimal? RiskMultiplier { get; init; }
    public decimal? ConfidenceBefore { get; init; }
    public decimal? ConfidenceAfter { get; init; }
    public required string DetailsJson { get; init; }
}
