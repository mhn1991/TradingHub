using DBManager.Abstractions.Config;
using DBManager.Postgres;
using DBManager.Postgres.Config;
using Microsoft.EntityFrameworkCore;
using Simulator.Calibration;
using TradingPolicies;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingHub.Persistence.Postgres.Calibration;

public sealed class PostgresCalibrationBundleApprovalStore(
    IDbContextFactory<TradingHubDbContext> contextFactory,
    TimeProvider? timeProvider = null) : ICalibrationBundleApprovalStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<CalibrationBundleCandidate> AddAsync(
        Guid setupArtifactId, Guid metaModelArtifactId, Guid managementArtifactId,
        TradingPolicyProfile proposedProfile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposedProfile);
        proposedProfile.Validate();
        if (proposedProfile.Status != TradingPolicyProfileStatus.Research)
            throw new ArgumentException("A proposed bundle profile must have Research status.", nameof(proposedProfile));

        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await RequireArtifactAsync(db, setupArtifactId, ArtifactRole.SetupCalibration, cancellationToken).ConfigureAwait(false);
        await RequireArtifactAsync(db, metaModelArtifactId, ArtifactRole.MetaModel, cancellationToken).ConfigureAwait(false);
        await RequireArtifactAsync(db, managementArtifactId, ArtifactRole.ManagementCalibration, cancellationToken).ConfigureAwait(false);
        var candidate = new CalibrationBundleCandidate
        {
            Id = Guid.NewGuid(),
            SetupArtifactId = setupArtifactId,
            MetaModelArtifactId = metaModelArtifactId,
            ManagementArtifactId = managementArtifactId,
            ProposedProfile = proposedProfile,
            Status = CalibrationBundleCandidateStatus.PendingReview,
            CreatedAt = _timeProvider.GetUtcNow()
        };
        db.CalibrationBundleCandidates.Add(ToEntity(candidate));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return candidate;
    }

    public async Task<CalibrationBundleCandidate> ApproveAsync(
        Guid candidateId, string approvedBy, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedBy);
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        CalibrationBundleCandidateEntity row = await RequireCandidateAsync(db, candidateId, cancellationToken).ConfigureAwait(false);
        CalibrationBundleCandidate candidate = ToDomain(row);
        if (candidate.Status != CalibrationBundleCandidateStatus.PendingReview)
            throw new InvalidOperationException($"Candidate {candidateId:N} is {candidate.Status}, not PendingReview.");

        string? latestDocument = await (
                from revision in db.PolicyRevisions
                join profileRoot in db.PolicyProfiles on revision.PolicyId equals profileRoot.PolicyId
                where profileRoot.StrategyId == candidate.ProposedProfile.StrategyId &&
                      revision.Status == PolicyRevisionStatus.ApprovedForDemo
                orderby revision.Revision descending
                select revision.PolicyDocumentJson)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        int revisionNumber = latestDocument is null
            ? 1
            : TradingPolicyProfileJson.Deserialize(latestDocument).Revision + 1;
        DateTimeOffset now = _timeProvider.GetUtcNow();
        TradingPolicyProfile proposed = candidate.ProposedProfile;
        TradingPolicyProfile approved = TradingPolicyProfile.Create(
            proposed.ProfileId, revisionNumber, proposed.StrategyId, proposed.StrategyVersion,
            proposed.EffectiveAgentDefinition(), TradingPolicyProfileStatus.ApprovedForDemo,
            proposed.FeaturePolicy, proposed.PositionSizing, proposed.AdaptiveRisk, proposed.PortfolioRisk,
            proposed.CorrelationRisk, proposed.TradingConditions, proposed.AccountSafety,
            proposed.LegacyManagement, proposed.ImprovedManagement, proposed.StructuralManagement,
            proposed.RegimeManagement, proposed.ManagementCalibration, proposed.MetaModelPolicy, now,
            candidate.SetupArtifactId, candidate.ManagementArtifactId, candidate.MetaModelArtifactId,
            proposed.Description);

        PolicyProfileEntity? root = await db.PolicyProfiles
            .SingleOrDefaultAsync(x => x.PolicyId == approved.ProfileId, cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            db.PolicyProfiles.Add(new PolicyProfileEntity
            {
                PolicyId = approved.ProfileId,
                StrategyId = approved.StrategyId,
                StrategyVersion = approved.StrategyVersion,
                FeatureSchemaHash = approved.FeaturePolicy.ComputeHash(),
                CreatedAt = now,
                CreatedBy = approvedBy,
                Description = approved.Description
            });
        }
        else if (root.StrategyId != approved.StrategyId || root.StrategyVersion != approved.StrategyVersion)
        {
            throw new InvalidOperationException("The approved policy identity conflicts with its existing profile.");
        }

        Guid policyRevisionId = Config.PostgresTradingPolicyProfileStore.RevisionId(approved.ProfileId, approved.Revision);
        if (await db.PolicyRevisions.AnyAsync(x => x.PolicyId == approved.ProfileId && x.Revision == approved.Revision,
                cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException($"Policy revision {approved.ProfileId:N}/r{approved.Revision} already exists.");
        db.PolicyRevisions.Add(new PolicyRevisionEntity
        {
            PolicyRevisionId = policyRevisionId,
            PolicyId = approved.ProfileId,
            Revision = approved.Revision,
            ConfigurationHash = approved.ConfigurationHash,
            Status = PolicyRevisionStatus.ApprovedForDemo,
            PolicyDocumentJson = TradingPolicyProfileJson.Serialize(approved),
            CreatedAt = now,
            CreatedBy = approvedBy,
            ApprovedAt = now,
            ApprovedBy = approvedBy
        });
        db.PolicyPromotionEvents.Add(new PolicyPromotionEventEntity
        {
            PolicyRevisionId = policyRevisionId,
            OccurredAt = now,
            FromStatus = PolicyRevisionStatus.Research,
            ToStatus = PolicyRevisionStatus.ApprovedForDemo,
            ActorIdentity = approvedBy,
            Reason = $"Approved calibration bundle {candidateId:N}."
        });
        Link(db, policyRevisionId, candidate.SetupArtifactId, ArtifactRole.SetupCalibration);
        Link(db, policyRevisionId, candidate.MetaModelArtifactId, ArtifactRole.MetaModel);
        Link(db, policyRevisionId, candidate.ManagementArtifactId, ArtifactRole.ManagementCalibration);
        await MoveArtifactsAsync(db, candidate, CalibrationArtifactStatus.ApprovedForDemo, approvedBy, now, cancellationToken)
            .ConfigureAwait(false);

        row.Status = (short)CalibrationBundleCandidateStatus.Approved;
        row.ReviewedBy = approvedBy;
        row.ReviewedAt = now;
        row.ApprovedProfileId = approved.ProfileId;
        row.ApprovedProfileRevision = approved.Revision;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToDomain(row);
    }

    public async Task<CalibrationBundleCandidate> RejectAsync(
        Guid candidateId, string rejectedBy, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rejectedBy);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        CalibrationBundleCandidateEntity row = await RequireCandidateAsync(db, candidateId, cancellationToken).ConfigureAwait(false);
        CalibrationBundleCandidate candidate = ToDomain(row);
        if (candidate.Status != CalibrationBundleCandidateStatus.PendingReview)
            throw new InvalidOperationException($"Candidate {candidateId:N} is {candidate.Status}, not PendingReview.");
        DateTimeOffset now = _timeProvider.GetUtcNow();
        await MoveArtifactsAsync(db, candidate, CalibrationArtifactStatus.Retired, rejectedBy, now, cancellationToken)
            .ConfigureAwait(false);
        row.Status = (short)CalibrationBundleCandidateStatus.Rejected;
        row.ReviewedBy = rejectedBy;
        row.ReviewedAt = now;
        row.RejectionReason = reason;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToDomain(row);
    }

    public async Task<CalibrationBundleCandidate?> GetAsync(
        Guid candidateId, CancellationToken cancellationToken = default)
    {
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        CalibrationBundleCandidateEntity? row = await db.CalibrationBundleCandidates.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CandidateId == candidateId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToDomain(row);
    }

    public async Task<IReadOnlyList<CalibrationBundleCandidate>> ListAsync(
        CalibrationBundleCandidateStatus? filter = null, int take = 50,
        CancellationToken cancellationToken = default)
    {
        if (take < 1) throw new ArgumentOutOfRangeException(nameof(take));
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        IQueryable<CalibrationBundleCandidateEntity> query = db.CalibrationBundleCandidates.AsNoTracking();
        if (filter.HasValue)
        {
            short status = (short)filter.Value;
            query = query.Where(x => x.Status == status);
        }
        CalibrationBundleCandidateEntity[] rows = await query.OrderByDescending(x => x.CreatedAt)
            .Take(take).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(ToDomain).ToArray();
    }

    private static async Task<CalibrationArtifactEntity> RequireArtifactAsync(
        TradingHubDbContext db, Guid id, ArtifactRole role, CancellationToken cancellationToken)
    {
        CalibrationArtifactEntity? artifact = await db.CalibrationArtifacts
            .SingleOrDefaultAsync(x => x.ArtifactId == id, cancellationToken).ConfigureAwait(false);
        if (artifact is null) throw new KeyNotFoundException($"Calibration artifact {id:N} was not found.");
        if (artifact.ArtifactType != role)
            throw new InvalidOperationException($"Calibration artifact {id:N} does not have the required role {role}.");
        return artifact;
    }

    private static async Task<CalibrationBundleCandidateEntity> RequireCandidateAsync(
        TradingHubDbContext db, Guid id, CancellationToken cancellationToken) =>
        await db.CalibrationBundleCandidates.SingleOrDefaultAsync(x => x.CandidateId == id, cancellationToken)
            .ConfigureAwait(false)
        ?? throw new KeyNotFoundException($"Calibration bundle candidate {id:N} was not found.");

    private static async Task MoveArtifactsAsync(
        TradingHubDbContext db, CalibrationBundleCandidate candidate, CalibrationArtifactStatus to,
        string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Guid[] ids = [candidate.SetupArtifactId, candidate.MetaModelArtifactId, candidate.ManagementArtifactId];
        CalibrationArtifactEntity[] artifacts = await db.CalibrationArtifacts
            .Where(x => ids.Contains(x.ArtifactId)).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (artifacts.Length != ids.Length) throw new InvalidOperationException("The bundle references a missing artifact.");
        foreach (CalibrationArtifactEntity artifact in artifacts)
        {
            CalibrationArtifactStatus from = artifact.Status;
            artifact.Status = to;
            if (to == CalibrationArtifactStatus.ApprovedForDemo)
            {
                artifact.ApprovedAt = now;
                artifact.ApprovedBy = actor;
            }
            CalibrationArtifactMetadata metadata = JsonSerializer.Deserialize<CalibrationArtifactMetadata>(
                artifact.MetricsJson, Json)
                ?? throw new InvalidOperationException($"Calibration artifact {artifact.ArtifactId:N} metadata is invalid.");
            CalibrationPromotionStatus promotion = to == CalibrationArtifactStatus.ApprovedForDemo
                ? CalibrationPromotionStatus.Approved
                : CalibrationPromotionStatus.Rejected;
            artifact.MetricsJson = JsonSerializer.Serialize(
                metadata with { PromotionStatus = promotion }, Json);
            db.CalibrationArtifactStatusEvents.Add(new CalibrationArtifactStatusEventEntity
            {
                ArtifactId = artifact.ArtifactId,
                FromStatus = from,
                ToStatus = to,
                OccurredAt = now,
                ActorIdentity = actor
            });
        }
    }

    private static void Link(TradingHubDbContext db, Guid revisionId, Guid artifactId, ArtifactRole role) =>
        db.PolicyArtifacts.Add(new PolicyArtifactEntity
        {
            PolicyRevisionId = revisionId,
            ArtifactId = artifactId,
            Role = role
        });

    private static CalibrationBundleCandidateEntity ToEntity(CalibrationBundleCandidate candidate) => new()
    {
        CandidateId = candidate.Id,
        SetupArtifactId = candidate.SetupArtifactId,
        MetaModelArtifactId = candidate.MetaModelArtifactId,
        ManagementArtifactId = candidate.ManagementArtifactId,
        ProposedProfileJson = TradingPolicyProfileJson.Serialize(candidate.ProposedProfile),
        Status = (short)candidate.Status,
        CreatedAt = candidate.CreatedAt,
        ReviewedBy = candidate.ReviewedBy,
        ReviewedAt = candidate.ReviewedAt,
        RejectionReason = candidate.RejectionReason,
        ApprovedProfileId = candidate.ApprovedProfileId,
        ApprovedProfileRevision = candidate.ApprovedProfileRevision
    };

    private static CalibrationBundleCandidate ToDomain(CalibrationBundleCandidateEntity row) => new()
    {
        Id = row.CandidateId,
        SetupArtifactId = row.SetupArtifactId,
        MetaModelArtifactId = row.MetaModelArtifactId,
        ManagementArtifactId = row.ManagementArtifactId,
        ProposedProfile = TradingPolicyProfileJson.Deserialize(row.ProposedProfileJson),
        Status = (CalibrationBundleCandidateStatus)row.Status,
        CreatedAt = row.CreatedAt,
        ReviewedBy = row.ReviewedBy,
        ReviewedAt = row.ReviewedAt,
        RejectionReason = row.RejectionReason,
        ApprovedProfileId = row.ApprovedProfileId,
        ApprovedProfileRevision = row.ApprovedProfileRevision
    };
}
