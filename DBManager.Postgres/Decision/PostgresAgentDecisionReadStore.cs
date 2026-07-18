using DBManager.Abstractions.Decision;
using Microsoft.EntityFrameworkCore;

namespace DBManager.Postgres.Decision;

/// <summary>
/// EF Core read model (section 14.2: Dashboard read models, moderate-volume queries) with keyset
/// pagination per section 18.1 — never large <c>OFFSET</c>.
/// </summary>
public sealed class PostgresAgentDecisionReadStore(IDbContextFactory<TradingHubDbContext> contextFactory)
    : IAgentDecisionReadStore
{
    public async Task<CursorPage<AgentDecisionSummary>> ReadDecisionsAsync(
        DecisionQuery query, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        IQueryable<AgentEvaluationEntity> evaluations = context.AgentEvaluations.AsNoTracking();

        if (query.DeploymentId is { } deploymentId)
            evaluations = evaluations.Where(e => e.DeploymentId == deploymentId);
        if (query.StrategyId is { } strategyId)
            evaluations = evaluations.Where(e => e.StrategyId == strategyId);
        if (query.InstrumentId is { } instrumentId)
            evaluations = evaluations.Where(e => e.InstrumentId == instrumentId);
        if (query.Before is { } cursor)
        {
            evaluations = evaluations.Where(e =>
                e.DecisionTime < cursor.DecisionTime
                || (e.DecisionTime == cursor.DecisionTime && e.EvaluationSequence < cursor.EvaluationSequence));
        }

        int pageSize = Math.Max(1, query.PageSize);
        List<AgentEvaluationEntity> rows = await evaluations
            .OrderByDescending(e => e.DecisionTime)
            .ThenByDescending(e => e.EvaluationSequence)
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        bool hasMore = rows.Count > pageSize;
        if (hasMore)
            rows.RemoveAt(rows.Count - 1);

        DecisionCursor? nextCursor = hasMore && rows.Count > 0
            ? new DecisionCursor(rows[^1].DecisionTime, rows[^1].EvaluationSequence)
            : null;

        return new CursorPage<AgentDecisionSummary>
        {
            Items = rows.Select(e => new AgentDecisionSummary
            {
                DecisionId = e.DecisionId,
                DeploymentId = e.DeploymentId,
                StrategyId = e.StrategyId,
                InstrumentId = e.InstrumentId,
                DecisionTime = e.DecisionTime,
                Action = e.Action,
                PrimaryReasonCode = e.PrimaryReasonCode,
                EvaluationSequence = e.EvaluationSequence
            }).ToArray(),
            NextCursor = nextCursor,
            HasMore = hasMore
        };
    }

    public async Task<CandidateFunnelDetail?> ReadCandidateFunnelAsync(
        Guid candidateId, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        CandidateKeyEntity? key = await context.CandidateKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.CandidateId == candidateId, cancellationToken)
            .ConfigureAwait(false);
        if (key is null)
            return null;

        TradeCandidateEntity candidate = await context.TradeCandidates
            .AsNoTracking()
            .FirstAsync(c => c.CandidateId == candidateId, cancellationToken)
            .ConfigureAwait(false);

        List<CandidateStageEventSummary> stageEvents = await context.CandidateStageEvents
            .AsNoTracking()
            .Where(e => e.CandidateId == candidateId)
            .OrderBy(e => e.OccurredAt)
            .Select(e => new CandidateStageEventSummary
            {
                OccurredAt = e.OccurredAt,
                Stage = e.Stage,
                Outcome = e.Outcome,
                ReasonCode = e.ReasonCode
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new CandidateFunnelDetail
        {
            CandidateId = candidateId,
            DecisionId = key.DecisionId,
            SetupId = candidate.SetupId,
            Status = key.CurrentStatus,
            StageEvents = stageEvents
        };
    }
}
