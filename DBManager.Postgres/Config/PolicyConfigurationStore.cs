using DBManager.Abstractions;
using DBManager.Abstractions.Config;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DBManager.Postgres.Config;

/// <summary>
/// EF Core implementation of <see cref="IPolicyConfigurationStore"/> (design doc section 7.2 +
/// 13.4). Config-schema writes are an administrative concern (section 14.2), not part of the live
/// host's high-frequency critical lane, so this uses the same pooled migrator-backed
/// <see cref="IDbContextFactory{TContext}"/> registered for schema management.
/// </summary>
public sealed class PolicyConfigurationStore(IDbContextFactory<TradingHubDbContext> contextFactory)
    : IPolicyConfigurationStore
{
    public async Task<DurableResult> CreatePolicyProfileAsync(
        CreatePolicyProfile command, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        context.PolicyProfiles.Add(new PolicyProfileEntity
        {
            PolicyId = command.PolicyId,
            StrategyId = command.StrategyId,
            StrategyVersion = command.StrategyVersion,
            FeatureSchemaHash = command.FeatureSchemaHash,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = command.CreatedBy,
            Description = command.Description
        });

        return await SaveAsync(context, "policy_profile", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Inserts an immutable revision. No store method ever updates <c>policy_document</c>.</summary>
    public async Task<DurableResult> CreatePolicyRevisionAsync(
        CreatePolicyRevision command, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        bool profileExists = await context.PolicyProfiles
            .AnyAsync(p => p.PolicyId == command.PolicyId, cancellationToken).ConfigureAwait(false);
        if (!profileExists)
            return DurableResult.PermanentFailure("policy_profile_not_found");

        context.PolicyRevisions.Add(new PolicyRevisionEntity
        {
            PolicyRevisionId = command.PolicyRevisionId,
            PolicyId = command.PolicyId,
            Revision = command.Revision,
            ConfigurationHash = command.ConfigurationHash,
            Status = PolicyRevisionStatus.Research,
            PolicyDocumentJson = command.PolicyDocumentJson,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = command.CreatedBy
        });

        return await SaveAsync(context, "policy_revision", cancellationToken).ConfigureAwait(false);
    }

    public async Task<DurableResult> PromotePolicyRevisionAsync(
        PromotePolicyRevision command, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        PolicyRevisionEntity? revision = await context.PolicyRevisions
            .FirstOrDefaultAsync(r => r.PolicyRevisionId == command.PolicyRevisionId, cancellationToken)
            .ConfigureAwait(false);
        if (revision is null)
            return DurableResult.PermanentFailure("policy_revision_not_found");

        if (!IsAllowedTransition(revision.Status, command.ToStatus))
        {
            return DurableResult.PermanentFailure(
                "invalid_status_transition", $"{revision.Status} -> {command.ToStatus} is not allowed.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        PolicyRevisionStatus fromStatus = revision.Status;
        revision.Status = command.ToStatus;
        if (command.ToStatus == PolicyRevisionStatus.ApprovedForDemo)
        {
            revision.ApprovedAt = now;
            revision.ApprovedBy = command.ActorIdentity;
        }
        else if (command.ToStatus == PolicyRevisionStatus.Retired)
        {
            revision.RetiredAt = now;
        }

        context.PolicyPromotionEvents.Add(new PolicyPromotionEventEntity
        {
            PolicyRevisionId = command.PolicyRevisionId,
            OccurredAt = now,
            FromStatus = fromStatus,
            ToStatus = command.ToStatus,
            ActorIdentity = command.ActorIdentity,
            Reason = command.Reason
        });

        return await SaveAsync(context, "policy_promotion", cancellationToken).ConfigureAwait(false);
    }

    public async Task<DurableResult> RegisterCalibrationArtifactAsync(
        RegisterCalibrationArtifact command, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        context.CalibrationArtifacts.Add(new CalibrationArtifactEntity
        {
            ArtifactId = command.ArtifactId,
            ArtifactType = command.ArtifactType,
            StrategyId = command.StrategyId,
            StrategyVersion = command.StrategyVersion,
            FeatureSchemaHash = command.FeatureSchemaHash,
            ContentHash = command.ContentHash,
            StorageUri = command.StorageUri,
            Status = CalibrationArtifactStatus.Registered,
            TrainingFrom = command.TrainingFrom,
            TrainingTo = command.TrainingTo,
            ValidationFrom = command.ValidationFrom,
            ValidationTo = command.ValidationTo,
            TestFrom = command.TestFrom,
            TestTo = command.TestTo,
            SampleCount = command.SampleCount,
            MetricsJson = command.MetricsJson,
            MediaType = "application/octet-stream",
            CreatedAt = DateTimeOffset.UtcNow
        });

        return await SaveAsync(context, "calibration_artifact", cancellationToken).ConfigureAwait(false);
    }

    public async Task<DurableResult> PromoteCalibrationArtifactAsync(
        PromoteCalibrationArtifact command, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        CalibrationArtifactEntity? artifact = await context.CalibrationArtifacts
            .FirstOrDefaultAsync(a => a.ArtifactId == command.ArtifactId, cancellationToken)
            .ConfigureAwait(false);
        if (artifact is null)
            return DurableResult.PermanentFailure("calibration_artifact_not_found");

        if (!IsAllowedTransition(artifact.Status, command.ToStatus))
        {
            return DurableResult.PermanentFailure(
                "invalid_status_transition", $"{artifact.Status} -> {command.ToStatus} is not allowed.");
        }

        artifact.Status = command.ToStatus;
        if (command.ToStatus == CalibrationArtifactStatus.ApprovedForDemo)
        {
            artifact.ApprovedAt = DateTimeOffset.UtcNow;
            artifact.ApprovedBy = command.ActorIdentity;
        }

        return await SaveAsync(context, "calibration_artifact_promotion", cancellationToken).ConfigureAwait(false);
    }

    public async Task<DurableResult> LinkPolicyArtifactAsync(
        LinkPolicyArtifact command, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        PolicyRevisionEntity? revision = await context.PolicyRevisions
            .FirstOrDefaultAsync(r => r.PolicyRevisionId == command.PolicyRevisionId, cancellationToken)
            .ConfigureAwait(false);
        if (revision is null)
            return DurableResult.PermanentFailure("policy_revision_not_found");

        CalibrationArtifactEntity? artifact = await context.CalibrationArtifacts
            .FirstOrDefaultAsync(a => a.ArtifactId == command.ArtifactId, cancellationToken)
            .ConfigureAwait(false);
        if (artifact is null)
            return DurableResult.PermanentFailure("calibration_artifact_not_found");

        PolicyProfileEntity profile = await context.PolicyProfiles
            .FirstAsync(p => p.PolicyId == revision.PolicyId, cancellationToken).ConfigureAwait(false);

        if (!ArtifactIsCompatible(profile, artifact))
            return DurableResult.PermanentFailure("artifact_incompatible", "Strategy/feature-schema mismatch.");

        context.PolicyArtifacts.Add(new PolicyArtifactEntity
        {
            PolicyRevisionId = command.PolicyRevisionId,
            ArtifactId = command.ArtifactId,
            Role = command.Role
        });

        return await SaveAsync(context, "policy_artifact_link", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The atomic policy-activation transaction from section 13.4.</summary>
    public async Task<DurableResult> ActivateDeploymentAsync(
        ActivateDeployment command, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        PolicyRevisionEntity? revision = await context.PolicyRevisions
            .FirstOrDefaultAsync(r => r.PolicyRevisionId == command.PolicyRevisionId, cancellationToken)
            .ConfigureAwait(false);
        if (revision is null)
            return DurableResult.PermanentFailure("policy_revision_not_found");

        List<PolicyArtifactEntity> links = await context.PolicyArtifacts
            .Where(a => a.PolicyRevisionId == command.PolicyRevisionId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (links.Count == 0)
            return DurableResult.PermanentFailure("no_artifacts_linked", "Cannot activate a revision with no linked artifacts.");

        foreach (PolicyArtifactEntity link in links)
        {
            CalibrationArtifactEntity artifact = await context.CalibrationArtifacts
                .FirstAsync(a => a.ArtifactId == link.ArtifactId, cancellationToken).ConfigureAwait(false);
            if (command.ExecutionMode != ExecutionMode.Simulated
                && artifact.Status != CalibrationArtifactStatus.ApprovedForDemo)
            {
                return DurableResult.PermanentFailure(
                    "artifact_not_approved", $"Artifact {link.ArtifactId} is not ApprovedForDemo.");
            }
        }

        if (command.ExecutionMode != ExecutionMode.Simulated
            && revision.Status != PolicyRevisionStatus.ApprovedForDemo)
        {
            return DurableResult.PermanentFailure(
                "policy_revision_not_approved", "Live execution requires an ApprovedForDemo revision.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        DeploymentEntity? previousActive = await context.Deployments
            .FirstOrDefaultAsync(
                d => d.BrokerAccountId == command.BrokerAccountId && d.Status == DeploymentStatus.Active,
                cancellationToken)
            .ConfigureAwait(false);
        if (previousActive is not null)
        {
            previousActive.Status = DeploymentStatus.Stopped;
            previousActive.StoppedAt = now;
            previousActive.StopReason = "superseded_by_activation";
            if (previousActive.PolicyRevisionId is { } previousRevisionId)
            {
                context.DeploymentActivationEvents.Add(new DeploymentActivationEventEntity
                {
                    DeploymentId = previousActive.DeploymentId,
                    OccurredAt = now,
                    EventType = "Closed",
                    PolicyRevisionId = previousRevisionId,
                    ActorIdentity = command.StartedBy,
                    Reason = "superseded_by_activation"
                });
            }
        }

        context.Deployments.Add(new DeploymentEntity
        {
            DeploymentId = command.DeploymentId,
            BrokerAccountId = command.BrokerAccountId,
            PolicyRevisionId = command.PolicyRevisionId,
            HostInstanceId = command.HostInstanceId,
            ExecutionMode = command.ExecutionMode,
            Status = DeploymentStatus.Active,
            RequestedAt = now,
            RunningAt = now,
            StartedAt = now,
            StartedBy = command.StartedBy,
            RequestedBy = command.StartedBy,
            ConcurrencyToken = 1,
            DeploymentHash = command.DeploymentHash
        });
        context.DeploymentActivationEvents.Add(new DeploymentActivationEventEntity
        {
            DeploymentId = command.DeploymentId,
            OccurredAt = now,
            EventType = "Activated",
            PolicyRevisionId = command.PolicyRevisionId,
            ActorIdentity = command.StartedBy
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return DurableResult.Committed();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // Either the deployment ID was reused (Duplicate) or another activation for the same
            // account raced past the partial-unique index (RejectedByConcurrency).
            bool alreadyExists = await context.Deployments
                .AnyAsync(d => d.DeploymentId == command.DeploymentId, cancellationToken)
                .ConfigureAwait(false);
            return alreadyExists
                ? DurableResult.Duplicate("deployment_already_exists")
                : DurableResult.RejectedByConcurrency("concurrent_activation");
        }
    }

    public async Task<DurableResult> SetDeploymentAssignmentAsync(
        SetDeploymentAssignment command, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        DeploymentAssignmentEntity? existing = await context.DeploymentAssignments
            .FirstOrDefaultAsync(
                a => a.DeploymentId == command.DeploymentId
                     && a.InstrumentId == command.InstrumentId
                     && a.StrategyId == command.StrategyId,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            context.DeploymentAssignments.Add(new DeploymentAssignmentEntity
            {
                DeploymentId = command.DeploymentId,
                InstrumentId = command.InstrumentId,
                StrategyId = command.StrategyId,
                AgentMode = command.AgentMode,
                Enabled = command.Enabled
            });
        }
        else
        {
            existing.AgentMode = command.AgentMode;
            existing.Enabled = command.Enabled;
        }

        return await SaveAsync(context, "deployment_assignment", cancellationToken).ConfigureAwait(false);
    }

    public async Task<PolicyRevisionDetail?> GetPolicyRevisionAsync(
        Guid policyRevisionId, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        PolicyRevisionEntity? revision = await context.PolicyRevisions
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.PolicyRevisionId == policyRevisionId, cancellationToken)
            .ConfigureAwait(false);
        if (revision is null)
            return null;

        List<PolicyArtifactLink> artifacts = await context.PolicyArtifacts
            .AsNoTracking()
            .Where(link => link.PolicyRevisionId == policyRevisionId)
            .Join(context.CalibrationArtifacts.AsNoTracking(),
                link => link.ArtifactId,
                artifact => artifact.ArtifactId,
                (link, artifact) => new PolicyArtifactLink
                {
                    ArtifactId = artifact.ArtifactId,
                    Role = link.Role,
                    ContentHash = artifact.ContentHash,
                    StorageUri = artifact.StorageUri,
                    Status = artifact.Status
                })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PolicyRevisionDetail
        {
            PolicyRevisionId = revision.PolicyRevisionId,
            PolicyId = revision.PolicyId,
            Revision = revision.Revision,
            ConfigurationHash = revision.ConfigurationHash,
            Status = revision.Status,
            PolicyDocumentJson = revision.PolicyDocumentJson,
            CreatedAt = revision.CreatedAt,
            CreatedBy = revision.CreatedBy,
            ApprovedAt = revision.ApprovedAt,
            ApprovedBy = revision.ApprovedBy,
            RetiredAt = revision.RetiredAt,
            Artifacts = artifacts
        };
    }

    public async Task<ActiveDeploymentDetail?> GetActiveDeploymentAsync(
        Guid brokerAccountId, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        DeploymentEntity? deployment = await context.Deployments
            .AsNoTracking()
            .FirstOrDefaultAsync(
                d => d.BrokerAccountId == brokerAccountId
                    && d.Status == DeploymentStatus.Active
                    && d.PolicyRevisionId.HasValue
                    && d.StartedAt.HasValue,
                cancellationToken)
            .ConfigureAwait(false);
        if (deployment is null)
            return null;

        return new ActiveDeploymentDetail
        {
            DeploymentId = deployment.DeploymentId,
            BrokerAccountId = deployment.BrokerAccountId,
            PolicyRevisionId = deployment.PolicyRevisionId!.Value,
            HostInstanceId = deployment.HostInstanceId,
            ExecutionMode = deployment.ExecutionMode,
            Status = deployment.Status,
            StartedAt = deployment.StartedAt!.Value
        };
    }

    private static bool ArtifactIsCompatible(PolicyProfileEntity profile, CalibrationArtifactEntity artifact) =>
        profile.StrategyId == artifact.StrategyId
        && profile.StrategyVersion == artifact.StrategyVersion
        && profile.FeatureSchemaHash == artifact.FeatureSchemaHash;

    private static bool IsAllowedTransition(PolicyRevisionStatus from, PolicyRevisionStatus to) => (from, to) switch
    {
        (PolicyRevisionStatus.Research, PolicyRevisionStatus.Reviewed) => true,
        (PolicyRevisionStatus.Reviewed, PolicyRevisionStatus.ApprovedForDemo) => true,
        (PolicyRevisionStatus.Research, PolicyRevisionStatus.Retired) => true,
        (PolicyRevisionStatus.Reviewed, PolicyRevisionStatus.Retired) => true,
        (PolicyRevisionStatus.ApprovedForDemo, PolicyRevisionStatus.Retired) => true,
        _ => false
    };

    private static bool IsAllowedTransition(CalibrationArtifactStatus from, CalibrationArtifactStatus to) =>
        (from, to) switch
        {
            (CalibrationArtifactStatus.Registered, CalibrationArtifactStatus.Validated) => true,
            (CalibrationArtifactStatus.Validated, CalibrationArtifactStatus.ApprovedForDemo) => true,
            (CalibrationArtifactStatus.Registered, CalibrationArtifactStatus.Retired) => true,
            (CalibrationArtifactStatus.Validated, CalibrationArtifactStatus.Retired) => true,
            (CalibrationArtifactStatus.ApprovedForDemo, CalibrationArtifactStatus.Retired) => true,
            _ => false
        };

    private static async Task<DurableResult> SaveAsync(
        TradingHubDbContext context, string entityName, CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return DurableResult.Committed();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            return DurableResult.Duplicate($"{entityName}_already_exists");
        }
    }
}
