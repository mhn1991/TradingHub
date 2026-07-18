using DBManager.Abstractions;
using DBManager.Abstractions.Decision;
using DBManager.Abstractions.Management;
using DBManager.Postgres.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace DBManager.Postgres.Management;

/// <summary>EF Core implementation of <see cref="IManagementStore"/> (section 7.7/7.8, Phase 5).</summary>
public sealed class ManagementStore(IDbContextFactory<TradingHubDbContext> contextFactory) : IManagementStore
{
    private static readonly SafetyDirective[] PausingDirectives =
        [SafetyDirective.PauseEntries, SafetyDirective.FlattenAll];

    public async Task<DurableResult> RecordManagementEventAsync(
        RecordManagementEvent command, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using IDbContextTransaction transaction = await context.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        context.Add(new ManagementEventEntity
        {
            PositionId = command.PositionId,
            OccurredAt = DateTimeOffset.UtcNow,
            EvaluationClock = command.EvaluationClock,
            ActionType = command.ActionType,
            ReasonCode = command.ReasonCode,
            ReferenceBid = command.ReferenceBid,
            ReferenceAsk = command.ReferenceAsk,
            CurrentR = command.CurrentR,
            MfeR = command.MfeR,
            MaeR = command.MaeR,
            PreviousStop = command.PreviousStop,
            RequestedStop = command.RequestedStop,
            ConfirmedStop = command.ConfirmedStop,
            RequestedReduction = command.RequestedReduction,
            ConfirmedReduction = command.ConfirmedReduction,
            BrokerCommandId = command.BrokerCommandId,
            DetailsJson = command.DetailsJson
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // Same broker_command_id already applied — idempotent no-op, not an error.
            return DurableResult.Duplicate("management_action_already_applied");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        PositionManagementStateEntity? state = await context.Set<PositionManagementStateEntity>()
            .FirstOrDefaultAsync(s => s.PositionId == command.PositionId, cancellationToken)
            .ConfigureAwait(false);
        if (state is null)
        {
            context.Add(new PositionManagementStateEntity
            {
                PositionId = command.PositionId,
                ManagementPolicyRevisionId = command.ManagementPolicyRevisionId,
                Stage = command.Stage,
                MfePrice = command.MfePrice,
                MaePrice = command.MaePrice,
                MfeR = command.MfeR,
                MaeR = command.MaeR,
                StatePayloadJson = command.DetailsJson,
                UpdatedAt = now,
                Version = 1
            });
        }
        else
        {
            state.Stage = command.Stage;
            state.MfePrice = Math.Max(state.MfePrice, command.MfePrice);
            state.MaePrice = Math.Min(state.MaePrice, command.MaePrice);
            state.MfeR = Math.Max(state.MfeR, command.MfeR);
            state.MaeR = Math.Min(state.MaeR, command.MaeR);
            state.StatePayloadJson = command.DetailsJson;
            state.UpdatedAt = now;
            state.Version += 1;
            SetIntervalTimestamp(state, command.EvaluationClock, now);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return DurableResult.Committed();
    }

    private static void SetIntervalTimestamp(
        PositionManagementStateEntity state, TriggerInterval clock, DateTimeOffset now)
    {
        switch (clock)
        {
            case TriggerInterval.Fast:
                state.LastFastIntervalAt = now;
                break;
            case TriggerInterval.Main:
                state.LastMainIntervalAt = now;
                break;
            case TriggerInterval.Thesis:
                state.LastThesisIntervalAt = now;
                break;
        }
        state.LastActionAt = now;
    }

    public async Task<ManagementStateDetail?> GetManagementStateAsync(
        Guid positionId, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        PositionManagementStateEntity? state = await context.Set<PositionManagementStateEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.PositionId == positionId, cancellationToken)
            .ConfigureAwait(false);
        if (state is null)
            return null;

        return new ManagementStateDetail
        {
            PositionId = state.PositionId,
            Stage = state.Stage,
            MfeR = state.MfeR,
            MaeR = state.MaeR,
            UpdatedAt = state.UpdatedAt,
            Version = state.Version
        };
    }

    public async Task<SafetyEventRecorded> RecordSafetyEventAsync(
        RecordSafetyEvent command, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        SafetyEventEntity entity = new()
        {
            DeploymentId = command.DeploymentId,
            BrokerAccountId = command.BrokerAccountId,
            OccurredAt = DateTimeOffset.UtcNow,
            Severity = command.Severity,
            Source = command.Source,
            Directive = command.Directive,
            ReasonCode = command.ReasonCode,
            InstrumentId = command.InstrumentId,
            OrderId = command.OrderId,
            PositionId = command.PositionId,
            AutomaticActionJson = command.AutomaticActionJson
        };
        context.Add(entity);

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new SafetyEventRecorded { Result = DurableResult.Committed(), EventId = entity.EventId };
    }

    public async Task<DurableResult> ResolveSafetyEventAsync(
        ResolveSafetyEvent command, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        SafetyEventEntity? safetyEvent = await context.Set<SafetyEventEntity>()
            .FirstOrDefaultAsync(e => e.EventId == command.EventId, cancellationToken).ConfigureAwait(false);
        if (safetyEvent is null)
            return DurableResult.PermanentFailure("safety_event_not_found");

        safetyEvent.ResolvedAt = DateTimeOffset.UtcNow;
        safetyEvent.ResolvedBy = command.ResolvedBy;

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return DurableResult.Committed();
    }

    public async Task<bool> IsEntriesPausedAsync(Guid deploymentId, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        SafetyEventEntity? latestUnresolved = await context.Set<SafetyEventEntity>()
            .AsNoTracking()
            .Where(e => e.DeploymentId == deploymentId && e.ResolvedAt == null)
            .OrderByDescending(e => e.OccurredAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return latestUnresolved is not null && PausingDirectives.Contains(latestUnresolved.Directive);
    }

    public async Task<DurableResult> RecordOperatorCommandAsync(
        RecordOperatorCommand command, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        context.Add(new OperatorCommandEntity
        {
            OperatorCommandId = command.OperatorCommandId,
            IdempotencyKey = command.IdempotencyKey,
            DeploymentId = command.DeploymentId,
            OperatorIdentity = command.OperatorIdentity,
            CommandType = command.CommandType,
            TargetId = command.TargetId,
            Reason = command.Reason,
            RequestedAt = DateTimeOffset.UtcNow,
            Status = OperatorCommandStatus.Pending
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return DurableResult.Committed();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            return DurableResult.Duplicate("operator_command_already_exists");
        }
    }

    public async Task<DurableResult> CompleteOperatorCommandAsync(
        CompleteOperatorCommand command, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        OperatorCommandEntity? entity = await context.Set<OperatorCommandEntity>()
            .FirstOrDefaultAsync(c => c.OperatorCommandId == command.OperatorCommandId, cancellationToken)
            .ConfigureAwait(false);
        if (entity is null)
            return DurableResult.PermanentFailure("operator_command_not_found");

        entity.Status = command.Status;
        entity.CompletedAt = DateTimeOffset.UtcNow;
        entity.ResultJson = command.ResultJson;

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return DurableResult.Committed();
    }

    public async Task<OperatorCommandDetail?> GetOperatorCommandAsync(
        string idempotencyKey, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        OperatorCommandEntity? entity = await context.Set<OperatorCommandEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.IdempotencyKey == idempotencyKey, cancellationToken)
            .ConfigureAwait(false);
        if (entity is null)
            return null;

        return new OperatorCommandDetail
        {
            OperatorCommandId = entity.OperatorCommandId,
            Status = entity.Status,
            ResultJson = entity.ResultJson ?? "{}"
        };
    }

    public async Task<DurableResult> SaveCheckpointAsync(SaveCheckpoint command, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        context.Add(new CheckpointEntity
        {
            CheckpointId = command.CheckpointId,
            DeploymentId = command.DeploymentId,
            CheckpointType = command.CheckpointType,
            Sequence = command.Sequence,
            ContentHash = command.ContentHash,
            PayloadJson = command.PayloadJson,
            CreatedAt = DateTimeOffset.UtcNow
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return DurableResult.Committed();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            return DurableResult.Duplicate("checkpoint_already_exists");
        }
    }

    public async Task<CheckpointDetail?> LoadLatestCheckpointAsync(
        Guid deploymentId, CheckpointType checkpointType, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        CheckpointEntity? entity = await context.Set<CheckpointEntity>()
            .AsNoTracking()
            .Where(c => c.DeploymentId == deploymentId && c.CheckpointType == checkpointType)
            .OrderByDescending(c => c.Sequence)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (entity is null)
            return null;

        return new CheckpointDetail
        {
            CheckpointId = entity.CheckpointId,
            Sequence = entity.Sequence,
            ContentHash = entity.ContentHash,
            PayloadJson = entity.PayloadJson,
            CreatedAt = entity.CreatedAt
        };
    }

    public async Task<DurableResult> RecordAccountSnapshotAsync(
        RecordAccountSnapshot command, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        context.Add(new AccountSnapshotEntity
        {
            BrokerAccountId = command.BrokerAccountId,
            Trigger = command.Trigger,
            SnapshotTime = now,
            Balance = command.Balance,
            Equity = command.Equity,
            UnrealisedPnl = command.UnrealisedPnl,
            RealisedPnl = command.RealisedPnl,
            MarginUsed = command.MarginUsed,
            MarginAvailable = command.MarginAvailable,
            MarginCloseoutRatio = command.MarginCloseoutRatio,
            ReceivedAt = now
        });

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return DurableResult.Committed();
    }
}
