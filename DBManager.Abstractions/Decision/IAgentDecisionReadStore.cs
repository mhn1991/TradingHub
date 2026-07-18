namespace DBManager.Abstractions.Decision;

/// <summary>Keyset cursor per section 18.1 — never large <c>OFFSET</c> pagination.</summary>
public sealed record DecisionCursor(DateTimeOffset DecisionTime, long EvaluationSequence);

public sealed record DecisionQuery
{
    public Guid? DeploymentId { get; init; }
    public string? StrategyId { get; init; }
    public long? InstrumentId { get; init; }
    public DecisionCursor? Before { get; init; }
    public int PageSize { get; init; } = 100;
}

public sealed record CursorPage<T>
{
    public required IReadOnlyList<T> Items { get; init; }
    public DecisionCursor? NextCursor { get; init; }
    public required bool HasMore { get; init; }
}

public sealed record AgentDecisionSummary
{
    public required Guid DecisionId { get; init; }
    public required Guid DeploymentId { get; init; }
    public required string StrategyId { get; init; }
    public required long InstrumentId { get; init; }
    public required DateTimeOffset DecisionTime { get; init; }
    public required AgentEvaluationAction Action { get; init; }
    public required string PrimaryReasonCode { get; init; }
    public required long EvaluationSequence { get; init; }
}

public sealed record CandidateStageEventSummary
{
    public required DateTimeOffset OccurredAt { get; init; }
    public required CandidateStage Stage { get; init; }
    public required StageOutcome Outcome { get; init; }
    public required string ReasonCode { get; init; }
}

/// <summary>The full funnel history for one candidate — proves "candidate funnel is reconstructable" (section 30).</summary>
public sealed record CandidateFunnelDetail
{
    public required Guid CandidateId { get; init; }
    public required Guid DecisionId { get; init; }
    public required string SetupId { get; init; }
    public required CandidateStatus Status { get; init; }
    public required IReadOnlyList<CandidateStageEventSummary> StageEvents { get; init; }
}

public interface IAgentDecisionReadStore
{
    Task<CursorPage<AgentDecisionSummary>> ReadDecisionsAsync(
        DecisionQuery query, CancellationToken cancellationToken);

    Task<CandidateFunnelDetail?> ReadCandidateFunnelAsync(Guid candidateId, CancellationToken cancellationToken);
}
