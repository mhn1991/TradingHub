using DBManager.Abstractions;
using DBManager.Abstractions.Execution;
using DBManager.Abstractions.Operations;
using DBManager.Postgres.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace DBManager.Postgres.Operations;

/// <summary>EF Core implementation of <see cref="IReconciliationStore"/> (section 7.8 + 13.3).</summary>
public sealed class ReconciliationStore(IDbContextFactory<TradingHubDbContext> contextFactory) : IReconciliationStore
{
    public async Task<DurableResult> StartReconciliationRunAsync(
        StartReconciliationRun command, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        context.Add(new ReconciliationRunEntity
        {
            ReconciliationId = command.ReconciliationId,
            BrokerAccountId = command.BrokerAccountId,
            Trigger = command.Trigger,
            StartedAt = DateTimeOffset.UtcNow,
            Status = ReconciliationRunStatus.Running,
            EntriesPaused = false
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return DurableResult.Committed();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            return DurableResult.Duplicate("reconciliation_run_already_exists");
        }
    }

    public async Task<DurableResult> CompleteReconciliationRunAsync(
        CompleteReconciliationRun command, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        ReconciliationRunEntity? run = await context.Set<ReconciliationRunEntity>()
            .FirstOrDefaultAsync(r => r.ReconciliationId == command.ReconciliationId, cancellationToken)
            .ConfigureAwait(false);
        if (run is null)
            return DurableResult.PermanentFailure("reconciliation_run_not_found");

        run.Status = command.Status;
        run.CompletedAt = DateTimeOffset.UtcNow;
        run.BrokerSnapshotTime = command.BrokerSnapshotTime;
        run.SummaryJson = command.SummaryJson;

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return DurableResult.Committed();
    }

    public async Task<DurableResult> RecordDifferenceAsync(
        RecordReconciliationDifference command, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        context.Add(new ReconciliationDifferenceEntity
        {
            DifferenceId = command.DifferenceId,
            ReconciliationId = command.ReconciliationId,
            DifferenceType = command.DifferenceType,
            Severity = command.Severity,
            InstrumentId = command.InstrumentId,
            OrderId = command.OrderId,
            PositionId = command.PositionId,
            LocalValueJson = command.LocalValueJson,
            BrokerValueJson = command.BrokerValueJson
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return DurableResult.Committed();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            return DurableResult.Duplicate("reconciliation_difference_already_exists");
        }
    }

    /// <summary>
    /// The atomic repair transaction from section 13.3. Adopting the broker's value is always this
    /// explicit, audited call — there is no code path that adopts an unknown broker position
    /// automatically.
    /// </summary>
    public async Task<DurableResult> ApplyCorrectionAsync(
        ApplyReconciliationCorrection command, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using IDbContextTransaction transaction = await context.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        ReconciliationDifferenceEntity? difference = await context.Set<ReconciliationDifferenceEntity>()
            .FirstOrDefaultAsync(d => d.DifferenceId == command.DifferenceId, cancellationToken)
            .ConfigureAwait(false);
        if (difference is null)
            return DurableResult.PermanentFailure("reconciliation_difference_not_found");
        if (difference.ResolvedAt is not null)
            return DurableResult.Duplicate("reconciliation_difference_already_resolved");

        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (command.Action == ReconciliationAction.AdoptBrokerValue && difference.OrderId is { } orderId)
        {
            OrderEntity? order = await context.Set<OrderEntity>()
                .FirstOrDefaultAsync(o => o.OrderId == orderId, cancellationToken).ConfigureAwait(false);
            if (order is not null)
            {
                order.Version += 1;
                context.Add(new OrderEventEntity
                {
                    OrderId = orderId,
                    OccurredAt = now,
                    EventType = OrderEventType.ReconciliationCorrection,
                    DetailsJson = command.ResolutionNote is null ? "{}" : $"{{\"note\":{System.Text.Json.JsonSerializer.Serialize(command.ResolutionNote)}}}"
                });
            }
        }

        difference.Action = command.Action;
        difference.ResolvedAt = now;
        difference.ResolutionNote = command.ResolutionNote;

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return DurableResult.Committed();
    }
}
