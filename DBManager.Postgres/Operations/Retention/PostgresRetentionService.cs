using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using TradingObservability.Abstractions;

namespace DBManager.Postgres.Operations.Retention;

public sealed record RetentionRequest(
    bool DryRun = true,
    int DetailedTelemetryDays = 90,
    int ActivityAggregateDays = 365);

public sealed record RetentionResult(
    bool DryRun,
    DateTimeOffset DetailedTelemetryCutoff,
    DateTimeOffset ActivityAggregateCutoff,
    long EligibleDetailedTelemetry,
    long EligibleActivityAggregates,
    long DeletedDetailedTelemetry,
    long DeletedActivityAggregates,
    long ElapsedMilliseconds);

/// <summary>
/// Conservative retention for observation-only data. It never removes normalized decisions,
/// orders, fills, positions, safety/reconciliation records, policies, artifacts, deployments,
/// runtime sessions, or any telemetry correlated to a trading-domain identity.
/// </summary>
public sealed class PostgresRetentionService(
    IDbContextFactory<TradingHubDbContext> contextFactory,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<RetentionResult> RunAsync(
        RetentionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.DetailedTelemetryDays < 30)
            throw new ArgumentOutOfRangeException(nameof(request), "Detailed telemetry retention cannot be less than 30 days.");
        if (request.ActivityAggregateDays < request.DetailedTelemetryDays)
            throw new ArgumentOutOfRangeException(nameof(request), "Aggregate retention cannot be shorter than detailed retention.");

        Stopwatch stopwatch = Stopwatch.StartNew();
        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset detailCutoff = now.AddDays(-request.DetailedTelemetryDays);
        DateTimeOffset aggregateCutoff = now.AddDays(-request.ActivityAggregateDays);
        await using TradingHubDbContext db = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        IQueryable<TradingTelemetryEventEntity> eligibleDetails = db.TradingTelemetryEvents.Where(row =>
            row.OccurredAt < detailCutoff &&
            row.Type == TradingTelemetryType.AgentStateTransition &&
            row.Severity <= TradingTelemetrySeverity.Information &&
            row.ExperimentId == null && row.SimulationId == null && row.ResearchRunId == null &&
            row.DeploymentId == null && row.PolicyRevisionId == null && row.DecisionId == null &&
            row.CandidateId == null && row.ReservationId == null && row.OrderCommandId == null &&
            row.OrderId == null && row.FillId == null && row.PositionId == null);
        IQueryable<AgentActivityWindowEntity> eligibleAggregates = db.AgentActivityWindows
            .Where(row => row.WindowEnd < aggregateCutoff);

        long detailCount = await eligibleDetails.LongCountAsync(cancellationToken).ConfigureAwait(false);
        long aggregateCount = await eligibleAggregates.LongCountAsync(cancellationToken).ConfigureAwait(false);
        long deletedDetails = 0;
        long deletedAggregates = 0;
        if (!request.DryRun)
        {
            deletedDetails = await eligibleDetails.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            deletedAggregates = await eligibleAggregates.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        return new RetentionResult(
            request.DryRun,
            detailCutoff,
            aggregateCutoff,
            detailCount,
            aggregateCount,
            deletedDetails,
            deletedAggregates,
            stopwatch.ElapsedMilliseconds);
    }
}
