using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DBManager.Abstractions.Config;
using DBManager.Postgres;
using DBManager.Postgres.Config;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TradingPolicies;

namespace TradingHub.Persistence.Postgres.Config;

public sealed class PostgresTradingPolicyProfileStore(
    IDbContextFactory<TradingHubDbContext> contextFactory,
    ILogger<PostgresTradingPolicyProfileStore>? logger = null) : ITradingPolicyProfileStore
{
    public async Task StoreAsync(TradingPolicyProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        string document = TradingPolicyProfileJson.Serialize(profile);
        Guid revisionId = RevisionId(profile.ProfileId, profile.Revision);

        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

        PolicyProfileEntity? root = await db.PolicyProfiles
            .SingleOrDefaultAsync(x => x.PolicyId == profile.ProfileId, cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            db.PolicyProfiles.Add(new PolicyProfileEntity
            {
                PolicyId = profile.ProfileId,
                StrategyId = profile.StrategyId,
                StrategyVersion = profile.StrategyVersion,
                FeatureSchemaHash = profile.FeaturePolicy.ComputeHash(),
                CreatedAt = profile.CreatedAt,
                CreatedBy = "runtime",
                Description = profile.Description
            });
        }
        else if (!string.Equals(root.StrategyId, profile.StrategyId, StringComparison.Ordinal) ||
                 !string.Equals(root.StrategyVersion, profile.StrategyVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Policy profile {profile.ProfileId:N} cannot change its strategy identity.");
        }

        PolicyRevisionEntity? existing = await db.PolicyRevisions
            .SingleOrDefaultAsync(x => x.PolicyId == profile.ProfileId && x.Revision == profile.Revision, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (!string.Equals(existing.ConfigurationHash, profile.ConfigurationHash, StringComparison.Ordinal) ||
                !string.Equals(existing.PolicyDocumentJson, document, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Policy profile {profile.ProfileId:N} revision {profile.Revision} is immutable and already contains different content.");
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        db.PolicyRevisions.Add(new PolicyRevisionEntity
        {
            PolicyRevisionId = revisionId,
            PolicyId = profile.ProfileId,
            Revision = profile.Revision,
            ConfigurationHash = profile.ConfigurationHash,
            Status = (PolicyRevisionStatus)profile.Status,
            PolicyDocumentJson = document,
            CreatedAt = profile.CreatedAt,
            CreatedBy = "runtime",
            ApprovedAt = profile.Status == TradingPolicyProfileStatus.ApprovedForDemo ? profile.CreatedAt : null,
            ApprovedBy = profile.Status == TradingPolicyProfileStatus.ApprovedForDemo ? "runtime" : null,
            RetiredAt = profile.Status == TradingPolicyProfileStatus.Retired ? profile.CreatedAt : null
        });
        AddArtifactLink(db, revisionId, profile.SetupCalibrationArtifactId, ArtifactRole.SetupCalibration);
        AddArtifactLink(db, revisionId, profile.MetaModelArtifactId, ArtifactRole.MetaModel);
        AddArtifactLink(db, revisionId, profile.ManagementCalibrationArtifactId, ArtifactRole.ManagementCalibration);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<TradingPolicyProfile?> GetAsync(
        Guid profileId, int revision, CancellationToken cancellationToken = default)
    {
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        string? document = await db.PolicyRevisions.AsNoTracking()
            .Where(x => x.PolicyId == profileId && x.Revision == revision)
            .Select(x => x.PolicyDocumentJson)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return document is null ? null : TradingPolicyProfileJson.Deserialize(document);
    }

    public async Task<TradingPolicyProfile?> GetLatestApprovedAsync(
        string strategyId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        string? document = await (
                from revision in db.PolicyRevisions.AsNoTracking()
                join profile in db.PolicyProfiles.AsNoTracking() on revision.PolicyId equals profile.PolicyId
                where profile.StrategyId == strategyId && revision.Status == PolicyRevisionStatus.ApprovedForDemo
                orderby revision.Revision descending
                select revision.PolicyDocumentJson)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return document is null ? null : TradingPolicyProfileJson.Deserialize(document);
    }

    public async Task<IReadOnlyList<TradingPolicyProfile>> ListAsync(
        string? strategyId = null, int take = 50, CancellationToken cancellationToken = default)
    {
        if (take < 1) throw new ArgumentOutOfRangeException(nameof(take));
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query =
            from revision in db.PolicyRevisions.AsNoTracking()
            join profile in db.PolicyProfiles.AsNoTracking() on revision.PolicyId equals profile.PolicyId
            where strategyId == null || profile.StrategyId == strategyId
            orderby revision.CreatedAt descending
            select revision.PolicyDocumentJson;
        string[] documents = await query.Take(take).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<TradingPolicyProfile>(documents.Length);
        foreach (string document in documents)
        {
            try
            {
                results.Add(TradingPolicyProfileJson.Deserialize(document));
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            {
                // A stale/incompatible document (e.g. written under an older schema) must not take
                // down the whole listing - skip it and keep serving everything that does parse.
                logger?.LogWarning(ex, "Skipping trading policy profile revision with an unparseable document.");
            }
        }
        return results;
    }

    internal static Guid RevisionId(Guid profileId, int revision)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"policy-revision/{profileId:N}/{revision}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static void AddArtifactLink(
        TradingHubDbContext db, Guid revisionId, Guid? artifactId, ArtifactRole role)
    {
        if (artifactId.HasValue)
            db.PolicyArtifacts.Add(new PolicyArtifactEntity
            {
                PolicyRevisionId = revisionId,
                ArtifactId = artifactId.Value,
                Role = role
            });
    }
}
