using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DBManager.Abstractions;
using DBManager.Abstractions.Config;
using DBManager.Postgres.Reference;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DBManager.Postgres.Config;

public sealed class RuntimeProfileStore(
    IDbContextFactory<TradingHubDbContext> contextFactory,
    TimeProvider timeProvider) : IRuntimeProfileStore
{
    public async Task<DurableResult> CreateProfileAsync(
        CreateRuntimeProfile command,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Name);
        await using TradingHubDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        context.RuntimeProfiles.Add(new RuntimeProfileEntity
        {
            RuntimeProfileId = command.RuntimeProfileId,
            ProfileKind = command.Kind,
            Name = command.Name,
            CreatedAt = timeProvider.GetUtcNow(),
            CreatedBy = command.CreatedBy
        });
        return await SaveAsync(context, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DurableResult> CreateRevisionAsync(
        CreateRuntimeProfileRevision command,
        CancellationToken cancellationToken)
    {
        using JsonDocument _ = JsonDocument.Parse(command.SettingsJson);
        string calculatedHash = Hash(command.SettingsJson);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(calculatedHash),
                Encoding.ASCII.GetBytes(command.SettingsHash.ToLowerInvariant())))
            return DurableResult.PermanentFailure("runtime_profile_hash_mismatch", "Settings hash does not match payload.");

        await using TradingHubDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        context.RuntimeProfileRevisions.Add(new RuntimeProfileRevisionEntity
        {
            RuntimeProfileRevisionId = command.RuntimeProfileRevisionId,
            RuntimeProfileId = command.RuntimeProfileId,
            Revision = command.Revision,
            Status = RuntimeProfileRevisionStatus.Draft,
            SettingsJson = command.SettingsJson,
            SettingsHash = calculatedHash,
            CreatedAt = timeProvider.GetUtcNow(),
            CreatedBy = command.CreatedBy
        });
        return await SaveAsync(context, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DurableResult> ApproveRevisionAsync(
        ApproveRuntimeProfileRevision command,
        CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        RuntimeProfileRevisionEntity? row = await context.RuntimeProfileRevisions.SingleOrDefaultAsync(
            value => value.RuntimeProfileRevisionId == command.RuntimeProfileRevisionId,
            cancellationToken).ConfigureAwait(false);
        if (row is null)
            return DurableResult.PermanentFailure("runtime_profile_revision_not_found", "Runtime profile revision was not found.");
        if (row.Status == RuntimeProfileRevisionStatus.Approved)
            return DurableResult.Duplicate("runtime_profile_revision_already_approved");
        if (row.Status != RuntimeProfileRevisionStatus.Draft)
            return DurableResult.PermanentFailure("runtime_profile_invalid_transition", "Only a draft revision can be approved.");
        row.Status = RuntimeProfileRevisionStatus.Approved;
        row.ApprovedAt = timeProvider.GetUtcNow();
        row.ApprovedBy = command.ApprovedBy;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return DurableResult.Committed();
    }

    public async Task<RuntimeProfileRevisionDetail?> GetRevisionAsync(
        Guid revisionId,
        CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        return await (
                from revision in context.RuntimeProfileRevisions.AsNoTracking()
                join profile in context.RuntimeProfiles.AsNoTracking()
                    on revision.RuntimeProfileId equals profile.RuntimeProfileId
                where revision.RuntimeProfileRevisionId == revisionId
                select new RuntimeProfileRevisionDetail
                {
                    RuntimeProfileRevisionId = revision.RuntimeProfileRevisionId,
                    RuntimeProfileId = revision.RuntimeProfileId,
                    Kind = profile.ProfileKind,
                    Name = profile.Name,
                    Revision = revision.Revision,
                    Status = revision.Status,
                    SettingsJson = revision.SettingsJson,
                    SettingsHash = revision.SettingsHash,
                    CreatedAt = revision.CreatedAt,
                    CreatedBy = revision.CreatedBy,
                    ApprovedAt = revision.ApprovedAt,
                    ApprovedBy = revision.ApprovedBy
                }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<RuntimeProfileRevisionDetail?> GetApprovedRevisionAsync(
        RuntimeProfileKind kind,
        string name,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await using TradingHubDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        return await (
                from revision in context.RuntimeProfileRevisions.AsNoTracking()
                join profile in context.RuntimeProfiles.AsNoTracking()
                    on revision.RuntimeProfileId equals profile.RuntimeProfileId
                where profile.ProfileKind == kind && profile.Name == name &&
                      revision.Status == RuntimeProfileRevisionStatus.Approved
                orderby revision.Revision descending
                select new RuntimeProfileRevisionDetail
                {
                    RuntimeProfileRevisionId = revision.RuntimeProfileRevisionId,
                    RuntimeProfileId = revision.RuntimeProfileId,
                    Kind = profile.ProfileKind,
                    Name = profile.Name,
                    Revision = revision.Revision,
                    Status = revision.Status,
                    SettingsJson = revision.SettingsJson,
                    SettingsHash = revision.SettingsHash,
                    CreatedAt = revision.CreatedAt,
                    CreatedBy = revision.CreatedBy,
                    ApprovedAt = revision.ApprovedAt,
                    ApprovedBy = revision.ApprovedBy
                }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static async Task<DurableResult> SaveAsync(
        TradingHubDbContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return DurableResult.Committed();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            return DurableResult.Duplicate("runtime_profile_already_exists");
        }
    }
}

public sealed class RuntimeConfigurationResolver(
    IRuntimeProfileStore profiles,
    IPolicyConfigurationStore policies,
    IDbContextFactory<TradingHubDbContext> contextFactory) : IRuntimeConfigurationResolver
{
    public async Task<ResolvedRuntimeConfiguration> ResolveAsync(
        RuntimeConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        RuntimeProfileRevisionDetail profile = await profiles.GetRevisionAsync(
            request.RuntimeProfileRevisionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Runtime profile revision was not found.");
        if (profile.Status != RuntimeProfileRevisionStatus.Approved)
            throw new InvalidOperationException("Runtime profile revision is not approved.");

        PolicyRevisionDetail? policy = request.PolicyRevisionId is { } policyId
            ? await policies.GetPolicyRevisionAsync(policyId, cancellationToken).ConfigureAwait(false)
            : null;
        if (request.PolicyRevisionId is not null && policy is null)
            throw new InvalidOperationException("Policy revision was not found.");

        BrokerEndpointRevisionDetail? endpoint = null;
        if (request.BrokerEnvironmentId is { } environmentId)
        {
            // Resolution is intentionally by the pinned environment ID; the active endpoint is
            // copied into the immutable aggregate and is never requeried in a candle loop.
            await using TradingHubDbContext context = await contextFactory
                .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            BrokerEndpointRevisionEntity? row = await context.BrokerEndpointRevisions.AsNoTracking()
                .Where(value => value.BrokerEnvironmentId == environmentId && value.Active &&
                                value.Kind == BrokerEndpointKind.Rest)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (row is null)
                throw new InvalidOperationException("Active broker REST endpoint revision was not found.");
            endpoint = new BrokerEndpointRevisionDetail
            {
                BrokerEndpointRevisionId = row.BrokerEndpointRevisionId,
                BrokerEnvironmentId = row.BrokerEnvironmentId,
                Kind = row.Kind,
                Revision = row.Revision,
                BaseAddress = new Uri(row.BaseAddress),
                Active = row.Active,
                ContentHash = row.ContentHash
            };
        }

        var snapshot = new
        {
            runtimeProfileRevisionId = profile.RuntimeProfileRevisionId,
            runtimeProfileHash = profile.SettingsHash,
            policyRevisionId = policy?.PolicyRevisionId,
            policyHash = policy?.ConfigurationHash,
            brokerEnvironmentId = request.BrokerEnvironmentId,
            brokerEndpointRevisionId = endpoint?.BrokerEndpointRevisionId,
            brokerEndpointHash = endpoint?.ContentHash
        };
        string json = JsonSerializer.Serialize(snapshot);
        return new ResolvedRuntimeConfiguration
        {
            RuntimeProfile = profile,
            PolicyRevisionId = policy?.PolicyRevisionId,
            BrokerEnvironmentId = request.BrokerEnvironmentId,
            BrokerEndpointRevisionId = endpoint?.BrokerEndpointRevisionId,
            ConfigurationHash = RuntimeProfileStore.Hash(json),
            SnapshotJson = json
        };
    }
}
