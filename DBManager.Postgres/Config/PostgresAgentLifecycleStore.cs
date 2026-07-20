using DBManager.Abstractions;
using DBManager.Abstractions.Config;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DBManager.Postgres.Config;

/// <summary>
/// PostgreSQL authority for policy execution permissions, parity certification, and the
/// durable multi-agent deployment command/event model.
/// </summary>
public sealed class PostgresAgentLifecycleStore(
    IDbContextFactory<TradingHubDbContext> contextFactory,
    TimeProvider timeProvider) : IAgentLifecycleStore
{
    public async Task<Page<PolicySummary>> ListPoliciesAsync(
        int offset, int limit, CancellationToken cancellationToken = default)
    {
        (offset, limit) = NormalizePage(offset, limit);
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        long total = await db.PolicyProfiles.LongCountAsync(cancellationToken).ConfigureAwait(false);
        PolicySummary[] items = await db.PolicyProfiles.AsNoTracking()
            .OrderBy(x => x.StrategyId).ThenBy(x => x.CreatedAt)
            .Skip(offset).Take(limit)
            .Select(x => new PolicySummary(x.PolicyId, x.StrategyId, x.StrategyVersion, x.CreatedAt, x.Description))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return new Page<PolicySummary>(items, offset, limit, total);
    }

    public async Task<Page<PolicyRevisionSummary>> ListPolicyRevisionsAsync(
        Guid? policyId, int offset, int limit, CancellationToken cancellationToken = default)
    {
        (offset, limit) = NormalizePage(offset, limit);
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        IQueryable<PolicyRevisionEntity> query = db.PolicyRevisions.AsNoTracking();
        if (policyId.HasValue)
            query = query.Where(x => x.PolicyId == policyId.Value);
        long total = await query.LongCountAsync(cancellationToken).ConfigureAwait(false);
        PolicyRevisionSummary[] items = await query
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Revision)
            .Skip(offset).Take(limit)
            .Select(x => new PolicyRevisionSummary(
                x.PolicyRevisionId, x.PolicyId, x.Revision, x.Status, x.ConfigurationHash, x.CreatedAt))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return new Page<PolicyRevisionSummary>(items, offset, limit, total);
    }

    public async Task<PolicyPermissionDetail> GetPermissionsAsync(
        Guid policyRevisionId, string brokerEnvironment, Guid? brokerAccountId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerEnvironment);
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        PolicyPermissionEventEntity[] rows = await db.PolicyPermissionEvents.AsNoTracking()
            .Where(x => x.PolicyRevisionId == policyRevisionId
                && x.BrokerEnvironment == brokerEnvironment
                && (x.BrokerAccountId == null || x.BrokerAccountId == brokerAccountId))
            .OrderBy(x => x.OccurredAt).ThenBy(x => x.PermissionEventId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);

        PolicyExecutionPermission effective = PolicyExecutionPermission.None;
        foreach (PolicyPermissionEventEntity row in rows)
            effective = row.Granted ? effective | row.Permissions : effective & ~row.Permissions;

        return new PolicyPermissionDetail(
            policyRevisionId,
            brokerEnvironment,
            brokerAccountId,
            effective,
            rows.Select(ToPermissionEvent).ToArray());
    }

    public async Task<DurableResult> AppendPermissionAsync(
        Guid permissionEventId, Guid policyRevisionId, string brokerEnvironment, Guid? brokerAccountId,
        PolicyExecutionPermission permissions, bool granted, string configurationHash,
        string actorIdentity, string reason, CancellationToken cancellationToken = default)
    {
        if (permissionEventId == Guid.Empty || policyRevisionId == Guid.Empty)
            return DurableResult.PermanentFailure("invalid_identifier");
        if (permissions == PolicyExecutionPermission.None)
            return DurableResult.PermanentFailure("permission_required");
        if (string.IsNullOrWhiteSpace(brokerEnvironment) || string.IsNullOrWhiteSpace(actorIdentity)
            || string.IsNullOrWhiteSpace(reason))
            return DurableResult.PermanentFailure("permission_event_fields_required");

        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        string? authoritativeHash = await db.PolicyRevisions.AsNoTracking()
            .Where(x => x.PolicyRevisionId == policyRevisionId)
            .Select(x => x.ConfigurationHash)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (authoritativeHash is null)
            return DurableResult.PermanentFailure("policy_revision_not_found");
        if (!StringComparer.Ordinal.Equals(authoritativeHash, configurationHash))
            return DurableResult.RejectedByConcurrency("configuration_hash_mismatch");

        db.PolicyPermissionEvents.Add(new PolicyPermissionEventEntity
        {
            PermissionEventId = permissionEventId,
            PolicyRevisionId = policyRevisionId,
            BrokerEnvironment = brokerEnvironment,
            BrokerAccountId = brokerAccountId,
            Permissions = permissions,
            Granted = granted,
            ConfigurationHash = authoritativeHash,
            OccurredAt = timeProvider.GetUtcNow(),
            ActorIdentity = actorIdentity,
            Reason = reason
        });
        return await SaveAsync(db, "permission_event_already_exists", cancellationToken).ConfigureAwait(false);
    }

    public async Task<ParityCertificationDetail?> GetLatestParityCertificationAsync(
        Guid policyRevisionId, CancellationToken cancellationToken = default)
    {
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        ParityCertificationEntity? row = await db.ParityCertifications.AsNoTracking()
            .Where(x => x.PolicyRevisionId == policyRevisionId)
            .OrderByDescending(x => x.CertifiedAt).ThenByDescending(x => x.CertificationId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToCertification(row);
    }

    public async Task<DurableResult> StoreParityCertificationAsync(
        ParityCertificationDetail certification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(certification);
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        string? authoritativeHash = await db.PolicyRevisions.AsNoTracking()
            .Where(x => x.PolicyRevisionId == certification.PolicyRevisionId)
            .Select(x => x.ConfigurationHash)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (authoritativeHash is null)
            return DurableResult.PermanentFailure("policy_revision_not_found");
        if (!StringComparer.Ordinal.Equals(authoritativeHash, certification.ConfigurationHash))
            return DurableResult.RejectedByConcurrency("configuration_hash_mismatch");

        db.ParityCertifications.Add(new ParityCertificationEntity
        {
            CertificationId = certification.CertificationId,
            PolicyRevisionId = certification.PolicyRevisionId,
            ConfigurationHash = authoritativeHash,
            SourceCommit = certification.SourceCommit,
            RecordingHash = certification.RecordingHash,
            SimulatorBuildHash = certification.SimulatorBuildHash,
            LiveBuildHash = certification.LiveBuildHash,
            ComparedEpochCount = certification.ComparedEpochCount,
            MismatchCount = certification.MismatchCount,
            MismatchDetailsJson = certification.MismatchDetailsJson,
            Status = certification.Status,
            CertifiedAt = certification.CertifiedAt,
            CertifiedBy = certification.CertifiedBy
        });
        return await SaveAsync(db, "parity_certification_already_exists", cancellationToken).ConfigureAwait(false);
    }

    public async Task<DurableResult> CreateDeploymentAsync(
        CreateDeploymentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.DeploymentId == Guid.Empty || request.BrokerAccountId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.Environment) || string.IsNullOrWhiteSpace(request.HostInstanceId)
            || string.IsNullOrWhiteSpace(request.RequestedBy) || string.IsNullOrWhiteSpace(request.DeploymentHash)
            || string.IsNullOrWhiteSpace(request.IdempotencyKey))
            return DurableResult.PermanentFailure("deployment_fields_required");

        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        DateTimeOffset now = timeProvider.GetUtcNow();
        db.Deployments.Add(new DeploymentEntity
        {
            DeploymentId = request.DeploymentId,
            BrokerAccountId = request.BrokerAccountId,
            PolicyRevisionId = null,
            HostInstanceId = request.HostInstanceId,
            Environment = request.Environment,
            ExecutionMode = ExecutionMode.Simulated,
            Status = DeploymentStatus.Requested,
            RequestedAt = now,
            RequestedBy = request.RequestedBy,
            DeploymentHash = request.DeploymentHash,
            ConcurrencyToken = 1,
            IdempotencyKey = request.IdempotencyKey
        });
        db.DeploymentEvents.Add(NewDeploymentEvent(
            request.DeploymentId, null, "DeploymentRequested", request.RequestedBy,
            request.IdempotencyKey, null, null, now));
        return await SaveAsync(db, "deployment_or_idempotency_key_already_exists", cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<DurableResult> AddDeploymentAgentAsync(
        AddDeploymentAgentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.DeploymentAgentId == Guid.Empty || request.DeploymentId == Guid.Empty
            || request.PolicyRevisionId == Guid.Empty || request.InstrumentId <= 0
            || string.IsNullOrWhiteSpace(request.StrategyId) || string.IsNullOrWhiteSpace(request.PackageHash))
            return DurableResult.PermanentFailure("deployment_agent_fields_required");

        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        DeploymentEntity? deployment = await db.Deployments
            .SingleOrDefaultAsync(x => x.DeploymentId == request.DeploymentId, cancellationToken)
            .ConfigureAwait(false);
        if (deployment is null)
            return DurableResult.PermanentFailure("deployment_not_found");
        DeploymentAgentEntity? existing = await db.DeploymentAgents.AsNoTracking()
            .SingleOrDefaultAsync(x => x.DeploymentAgentId == request.DeploymentAgentId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.DeploymentId == request.DeploymentId
                && existing.PolicyRevisionId == request.PolicyRevisionId
                && existing.InstrumentId == request.InstrumentId
                && existing.AgentMode == request.AgentMode
                && StringComparer.Ordinal.Equals(existing.PackageHash, request.PackageHash)
                ? DurableResult.Duplicate("deployment_agent_already_exists")
                : DurableResult.PermanentFailure("deployment_agent_id_conflict");
        }
        if (request.ExpectedVersion.HasValue && deployment.ConcurrencyToken != request.ExpectedVersion.Value)
            return DurableResult.RejectedByConcurrency("deployment_version_mismatch");
        bool compatibleRevision = await (
                from revision in db.PolicyRevisions
                join profile in db.PolicyProfiles on revision.PolicyId equals profile.PolicyId
                where revision.PolicyRevisionId == request.PolicyRevisionId
                    && profile.StrategyId == request.StrategyId
                select revision.PolicyRevisionId)
            .AnyAsync(cancellationToken).ConfigureAwait(false);
        if (!compatibleRevision)
            return DurableResult.PermanentFailure("policy_revision_strategy_mismatch");

        DateTimeOffset now = timeProvider.GetUtcNow();
        db.DeploymentAgents.Add(new DeploymentAgentEntity
        {
            DeploymentAgentId = request.DeploymentAgentId,
            DeploymentId = request.DeploymentId,
            BrokerAccountId = deployment.BrokerAccountId,
            PolicyRevisionId = request.PolicyRevisionId,
            InstrumentId = request.InstrumentId,
            StrategyId = request.StrategyId,
            AgentMode = request.AgentMode,
            Status = DeploymentAgentStatus.Requested,
            Enabled = request.Enabled,
            PackageHash = request.PackageHash,
            CreatedAt = now
        });
        deployment.ConcurrencyToken++;
        db.DeploymentEvents.Add(NewDeploymentEvent(
            request.DeploymentId, request.DeploymentAgentId, "AgentAdded", request.RequestedBy,
            request.DeploymentAgentId.ToString("N"), null, null, now));
        return await SaveAsync(db, "deployment_agent_or_execution_owner_already_exists", cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<DurableResult> EnqueueCommandAsync(
        EnqueueDeploymentCommandRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.CommandId == Guid.Empty || request.DeploymentId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.RequestedBy) || string.IsNullOrWhiteSpace(request.IdempotencyKey))
            return DurableResult.PermanentFailure("command_fields_required");

        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        DeploymentEntity? deployment = await db.Deployments
            .SingleOrDefaultAsync(x => x.DeploymentId == request.DeploymentId, cancellationToken)
            .ConfigureAwait(false);
        if (deployment is null)
            return DurableResult.PermanentFailure("deployment_not_found");
        DeploymentCommandEntity? existing = await db.DeploymentCommands.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CommandId == request.CommandId
                || x.IdempotencyKey == request.IdempotencyKey, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.CommandId == request.CommandId
                && existing.DeploymentId == request.DeploymentId
                && existing.DeploymentAgentId == request.DeploymentAgentId
                && existing.CommandType == request.CommandType
                ? DurableResult.Duplicate("deployment_command_already_exists")
                : DurableResult.PermanentFailure("deployment_command_idempotency_conflict");
        }
        if (deployment.ConcurrencyToken != request.ExpectedVersion)
            return DurableResult.RejectedByConcurrency("deployment_version_mismatch");
        if (request.DeploymentAgentId.HasValue && !await db.DeploymentAgents.AnyAsync(
                x => x.DeploymentAgentId == request.DeploymentAgentId.Value
                    && x.DeploymentId == request.DeploymentId, cancellationToken).ConfigureAwait(false))
            return DurableResult.PermanentFailure("deployment_agent_not_found");

        db.DeploymentCommands.Add(new DeploymentCommandEntity
        {
            CommandId = request.CommandId,
            DeploymentId = request.DeploymentId,
            DeploymentAgentId = request.DeploymentAgentId,
            CommandType = request.CommandType,
            RequestedAt = timeProvider.GetUtcNow(),
            RequestedBy = request.RequestedBy,
            ExpectedVersion = request.ExpectedVersion,
            PayloadJson = request.PayloadJson,
            Status = DeploymentCommandStatus.Pending,
            IdempotencyKey = request.IdempotencyKey
        });
        deployment.ConcurrencyToken++;
        return await SaveAsync(db, "command_or_idempotency_key_already_exists", cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ClaimedDeploymentCommand>> ClaimPendingCommandsAsync(
        string hostInstanceId, int take, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostInstanceId);
        take = Math.Clamp(take, 1, 100);
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        DeploymentCommandEntity[] commands = await db.DeploymentCommands.FromSqlInterpolated($$"""
                SELECT c.*
                FROM operations.deployment_commands AS c
                INNER JOIN config.deployments AS d ON d.deployment_id = c.deployment_id
                WHERE (c.status = 0 OR (c.status = 1 AND c.claimed_at < now() - interval '2 minutes'))
                  AND d.host_instance_id = {{hostInstanceId}}
                ORDER BY c.requested_at, c.command_id
                FOR UPDATE OF c SKIP LOCKED
                LIMIT {{take}}
                """)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = timeProvider.GetUtcNow();
        foreach (DeploymentCommandEntity command in commands)
        {
            command.Status = DeploymentCommandStatus.Claimed;
            command.ClaimedByHost = hostInstanceId;
            command.ClaimedAt = now;
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return commands.Select(x => new ClaimedDeploymentCommand(
            x.CommandId, x.DeploymentId, x.DeploymentAgentId, x.CommandType,
            x.RequestedBy, x.ExpectedVersion, x.PayloadJson, x.IdempotencyKey)).ToArray();
    }

    public async Task<DurableResult> CompleteCommandAsync(
        Guid commandId, string hostInstanceId, bool succeeded, string? errorCode,
        string? errorMessage, CancellationToken cancellationToken = default)
    {
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        DeploymentCommandEntity? command = await db.DeploymentCommands
            .SingleOrDefaultAsync(x => x.CommandId == commandId, cancellationToken).ConfigureAwait(false);
        if (command is null)
            return DurableResult.PermanentFailure("command_not_found");
        if (command.Status is DeploymentCommandStatus.Completed or DeploymentCommandStatus.Failed)
            return DurableResult.Duplicate("command_already_completed");
        if (command.Status != DeploymentCommandStatus.Claimed
            || !StringComparer.Ordinal.Equals(command.ClaimedByHost, hostInstanceId))
            return DurableResult.RejectedByConcurrency("command_not_claimed_by_host");

        command.Status = succeeded ? DeploymentCommandStatus.Completed : DeploymentCommandStatus.Failed;
        command.CompletedAt = timeProvider.GetUtcNow();
        command.ErrorCode = errorCode;
        command.ErrorMessage = errorMessage;
        return await SaveAsync(db, "command_completion_conflict", cancellationToken).ConfigureAwait(false);
    }

    public async Task<DurableResult> TransitionDeploymentAsync(
        Guid deploymentId, long expectedVersion, DeploymentStatus status, string actorIdentity,
        string correlationId, string? reasonCode, CancellationToken cancellationToken = default)
    {
        if (deploymentId == Guid.Empty || string.IsNullOrWhiteSpace(actorIdentity)
            || string.IsNullOrWhiteSpace(correlationId))
            return DurableResult.PermanentFailure("deployment_transition_fields_required");
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        DeploymentEntity? deployment = await db.Deployments
            .SingleOrDefaultAsync(x => x.DeploymentId == deploymentId, cancellationToken).ConfigureAwait(false);
        if (deployment is null)
            return DurableResult.PermanentFailure("deployment_not_found");
        if (deployment.ConcurrencyToken != expectedVersion)
            return DurableResult.RejectedByConcurrency("deployment_version_mismatch");
        if (!CanTransition(deployment.Status, status))
            return DurableResult.PermanentFailure("invalid_deployment_transition",
                $"Cannot transition from {deployment.Status} to {status}.");

        DateTimeOffset now = timeProvider.GetUtcNow();
        deployment.Status = status;
        deployment.ConcurrencyToken++;
        switch (status)
        {
            case DeploymentStatus.Preparing: deployment.PreparingAt = now; break;
            case DeploymentStatus.WarmingUp: deployment.WarmingUpAt = now; break;
            case DeploymentStatus.Active: deployment.RunningAt = now; deployment.StartedAt ??= now; break;
            case DeploymentStatus.Paused: deployment.PausedAt = now; break;
            case DeploymentStatus.Draining: deployment.DrainingAt = now; break;
            case DeploymentStatus.Faulted: deployment.FaultedAt = now; break;
            case DeploymentStatus.Stopped: deployment.StoppedAt = now; deployment.StopReason = reasonCode; break;
        }
        db.DeploymentEvents.Add(NewDeploymentEvent(deploymentId, null,
            $"Deployment{status}", actorIdentity, correlationId, reasonCode, null, now));
        return await SaveAsync(db, "deployment_transition_conflict", cancellationToken).ConfigureAwait(false);
    }

    public async Task<DurableResult> TransitionDeploymentAgentAsync(
        Guid deploymentAgentId, DeploymentAgentStatus status, bool enabled, string actorIdentity,
        string correlationId, string? faultCode = null, string? faultMessage = null,
        CancellationToken cancellationToken = default)
    {
        if (deploymentAgentId == Guid.Empty || string.IsNullOrWhiteSpace(actorIdentity)
            || string.IsNullOrWhiteSpace(correlationId))
            return DurableResult.PermanentFailure("agent_transition_fields_required");
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        DeploymentAgentEntity? agent = await db.DeploymentAgents
            .SingleOrDefaultAsync(x => x.DeploymentAgentId == deploymentAgentId, cancellationToken)
            .ConfigureAwait(false);
        if (agent is null)
            return DurableResult.PermanentFailure("deployment_agent_not_found");
        if (!CanTransition(agent.Status, status))
            return DurableResult.PermanentFailure("invalid_agent_transition",
                $"Cannot transition from {agent.Status} to {status}.");

        DateTimeOffset now = timeProvider.GetUtcNow();
        agent.Status = status;
        agent.Enabled = enabled;
        agent.FaultCode = faultCode;
        agent.FaultMessage = faultMessage;
        if (status == DeploymentAgentStatus.Running)
            agent.StartedAt ??= now;
        if (status == DeploymentAgentStatus.Stopped)
            agent.StoppedAt = now;
        DeploymentEntity deployment = await db.Deployments.SingleAsync(
            x => x.DeploymentId == agent.DeploymentId, cancellationToken).ConfigureAwait(false);
        deployment.ConcurrencyToken++;
        db.DeploymentEvents.Add(NewDeploymentEvent(agent.DeploymentId, deploymentAgentId,
            $"Agent{status}", actorIdentity, correlationId, faultCode, null, now));
        return await SaveAsync(db, "agent_transition_conflict", cancellationToken).ConfigureAwait(false);
    }

    public async Task<DurableResult> AppendDeploymentEventAsync(
        Guid deploymentId, Guid? deploymentAgentId, string eventType, string actorIdentity,
        string correlationId, string? reasonCode, string? detailJson,
        CancellationToken cancellationToken = default)
    {
        if (deploymentId == Guid.Empty || string.IsNullOrWhiteSpace(eventType)
            || string.IsNullOrWhiteSpace(actorIdentity) || string.IsNullOrWhiteSpace(correlationId))
            return DurableResult.PermanentFailure("deployment_event_fields_required");
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        db.DeploymentEvents.Add(NewDeploymentEvent(
            deploymentId, deploymentAgentId, eventType, actorIdentity, correlationId,
            reasonCode, detailJson, timeProvider.GetUtcNow()));
        return await SaveAsync(db, "deployment_event_conflict", cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeploymentDetail?> GetDeploymentAsync(
        Guid deploymentId, CancellationToken cancellationToken = default)
    {
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        DeploymentSummary? deployment = await db.Deployments.AsNoTracking()
            .Where(x => x.DeploymentId == deploymentId)
            .Select(x => new DeploymentSummary(
                x.DeploymentId, x.BrokerAccountId, x.Environment, x.HostInstanceId,
                x.Status, x.ConcurrencyToken, x.RequestedAt, x.RequestedBy))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (deployment is null)
            return null;
        DeploymentAgentDetail[] agents = await QueryAgents(db, deploymentId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        DeploymentEventDetail[] events = await db.DeploymentEvents.AsNoTracking()
            .Where(x => x.DeploymentId == deploymentId)
            .OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.EventId)
            .Take(200)
            .Select(x => new DeploymentEventDetail(
                x.EventId, x.DeploymentId, x.DeploymentAgentId, x.EventType, x.OccurredAt,
                x.ActorIdentity, x.CorrelationId, x.ReasonCode, x.DetailJson))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return new DeploymentDetail(deployment, agents, events);
    }

    public async Task<Page<DeploymentSummary>> ListDeploymentsAsync(
        int offset, int limit, CancellationToken cancellationToken = default)
    {
        (offset, limit) = NormalizePage(offset, limit);
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        long total = await db.Deployments.LongCountAsync(cancellationToken).ConfigureAwait(false);
        DeploymentSummary[] items = await db.Deployments.AsNoTracking()
            .OrderByDescending(x => x.RequestedAt)
            .Skip(offset).Take(limit)
            .Select(x => new DeploymentSummary(
                x.DeploymentId, x.BrokerAccountId, x.Environment, x.HostInstanceId,
                x.Status, x.ConcurrencyToken, x.RequestedAt, x.RequestedBy))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return new Page<DeploymentSummary>(items, offset, limit, total);
    }

    public async Task<IReadOnlyList<DeploymentAgentDetail>> ListDeploymentAgentsAsync(
        Guid deploymentId, CancellationToken cancellationToken = default)
    {
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        return await QueryAgents(db, deploymentId).ToArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeploymentAgentDetail?> GetDeploymentAgentAsync(
        Guid deploymentAgentId, CancellationToken cancellationToken = default)
    {
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        return await QueryAgents(db).SingleOrDefaultAsync(
            x => x.DeploymentAgentId == deploymentAgentId, cancellationToken).ConfigureAwait(false);
    }

    private static IQueryable<DeploymentAgentDetail> QueryAgents(TradingHubDbContext db, Guid deploymentId) =>
        QueryAgents(db).Where(x => x.DeploymentId == deploymentId);

    private static IQueryable<DeploymentAgentDetail> QueryAgents(TradingHubDbContext db) =>
        db.DeploymentAgents.AsNoTracking()
            .OrderBy(x => x.InstrumentId).ThenBy(x => x.CreatedAt)
            .Select(x => new DeploymentAgentDetail
            {
                DeploymentAgentId = x.DeploymentAgentId,
                DeploymentId = x.DeploymentId,
                BrokerAccountId = x.BrokerAccountId,
                PolicyRevisionId = x.PolicyRevisionId,
                InstrumentId = x.InstrumentId,
                StrategyId = x.StrategyId,
                AgentMode = x.AgentMode,
                Status = x.Status,
                Enabled = x.Enabled,
                PackageHash = x.PackageHash,
                CreatedAt = x.CreatedAt,
                StartedAt = x.StartedAt,
                StoppedAt = x.StoppedAt,
                FaultCode = x.FaultCode,
                FaultMessage = x.FaultMessage
            });

    private static PolicyPermissionEvent ToPermissionEvent(PolicyPermissionEventEntity x) => new(
        x.PermissionEventId, x.Permissions, x.Granted, x.OccurredAt,
        x.ActorIdentity, x.Reason, x.ConfigurationHash);

    private static ParityCertificationDetail ToCertification(ParityCertificationEntity x) => new()
    {
        CertificationId = x.CertificationId,
        PolicyRevisionId = x.PolicyRevisionId,
        ConfigurationHash = x.ConfigurationHash,
        SourceCommit = x.SourceCommit,
        RecordingHash = x.RecordingHash,
        SimulatorBuildHash = x.SimulatorBuildHash,
        LiveBuildHash = x.LiveBuildHash,
        ComparedEpochCount = x.ComparedEpochCount,
        MismatchCount = x.MismatchCount,
        MismatchDetailsJson = x.MismatchDetailsJson,
        Status = x.Status,
        CertifiedAt = x.CertifiedAt,
        CertifiedBy = x.CertifiedBy
    };

    private static DeploymentEventEntity NewDeploymentEvent(
        Guid deploymentId, Guid? deploymentAgentId, string eventType, string actorIdentity,
        string correlationId, string? reasonCode, string? detailJson, DateTimeOffset occurredAt) => new()
    {
        DeploymentId = deploymentId,
        DeploymentAgentId = deploymentAgentId,
        EventType = eventType,
        OccurredAt = occurredAt,
        ActorIdentity = actorIdentity,
        CorrelationId = correlationId,
        ReasonCode = reasonCode,
        DetailJson = detailJson
    };

    private static (int Offset, int Limit) NormalizePage(int offset, int limit) =>
        (Math.Max(0, offset), Math.Clamp(limit, 1, 200));

    private static bool CanTransition(DeploymentStatus from, DeploymentStatus to) => from == to || from switch
    {
        DeploymentStatus.Requested => to is DeploymentStatus.Validating or DeploymentStatus.Faulted,
        DeploymentStatus.Validating => to is DeploymentStatus.Preparing or DeploymentStatus.Faulted,
        DeploymentStatus.Preparing => to is DeploymentStatus.WarmingUp or DeploymentStatus.Faulted,
        DeploymentStatus.WarmingUp => to is DeploymentStatus.Reconciling or DeploymentStatus.Faulted,
        DeploymentStatus.Reconciling => to is DeploymentStatus.Active or DeploymentStatus.Paused or DeploymentStatus.Faulted,
        DeploymentStatus.Active => to is DeploymentStatus.Paused or DeploymentStatus.Draining or DeploymentStatus.Faulted,
        DeploymentStatus.Paused => to is DeploymentStatus.Active or DeploymentStatus.Draining or DeploymentStatus.Faulted,
        DeploymentStatus.Draining => to is DeploymentStatus.Stopped or DeploymentStatus.Faulted,
        DeploymentStatus.Faulted => to is DeploymentStatus.Paused or DeploymentStatus.Stopped,
        _ => false
    };

    private static bool CanTransition(DeploymentAgentStatus from, DeploymentAgentStatus to) => from == to || from switch
    {
        DeploymentAgentStatus.Requested => to is DeploymentAgentStatus.Validating
            or DeploymentAgentStatus.ValidationFailed or DeploymentAgentStatus.Faulted,
        DeploymentAgentStatus.Validating => to is DeploymentAgentStatus.Preparing
            or DeploymentAgentStatus.ValidationFailed or DeploymentAgentStatus.Faulted,
        DeploymentAgentStatus.Preparing => to is DeploymentAgentStatus.WarmingUp or DeploymentAgentStatus.Faulted,
        DeploymentAgentStatus.WarmingUp => to is DeploymentAgentStatus.Running or DeploymentAgentStatus.Faulted,
        DeploymentAgentStatus.Running => to is DeploymentAgentStatus.Paused or DeploymentAgentStatus.Draining
            or DeploymentAgentStatus.Stopped or DeploymentAgentStatus.Faulted,
        DeploymentAgentStatus.Paused => to is DeploymentAgentStatus.Running or DeploymentAgentStatus.Draining
            or DeploymentAgentStatus.Stopped or DeploymentAgentStatus.Faulted,
        DeploymentAgentStatus.Draining => to is DeploymentAgentStatus.Stopped or DeploymentAgentStatus.Faulted,
        DeploymentAgentStatus.Faulted => to is DeploymentAgentStatus.Paused or DeploymentAgentStatus.Stopped,
        DeploymentAgentStatus.ValidationFailed => to is DeploymentAgentStatus.Validating or DeploymentAgentStatus.Stopped,
        _ => false
    };

    private static async Task<DurableResult> SaveAsync(
        TradingHubDbContext db, string duplicateReason, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return DurableResult.Committed();
        }
        catch (DbUpdateConcurrencyException)
        {
            return DurableResult.RejectedByConcurrency("deployment_version_mismatch");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            return DurableResult.Duplicate(duplicateReason);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23503" })
        {
            return DurableResult.PermanentFailure("referenced_record_not_found");
        }
    }
}
