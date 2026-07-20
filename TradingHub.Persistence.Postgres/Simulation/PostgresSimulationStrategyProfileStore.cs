using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DBManager.Postgres;
using DBManager.Postgres.Simulation;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Simulator.Experiments.Models;
using Simulator.Experiments.Persistence;

namespace TradingHub.Persistence.Postgres.Simulation;

public sealed class PostgresSimulationStrategyProfileStore(
    IDbContextFactory<TradingHubDbContext> contextFactory,
    TimeProvider? timeProvider = null) : ISimulationStrategyProfileStore
{
    private const string ProfileNameIndex = "ix_experiment_profiles_name";
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<SimulationStrategyProfile> CreateAsync(SimulationStrategyProfile draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.Revision != 1)
            throw new ArgumentException("A new profile starts at revision 1.");
        SimulationStrategyProfile profile = SimulationStrategyProfile.Create(draft);
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        if (await db.SimulationProfiles.AnyAsync(x => x.ProfileId == profile.ProfileId, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException($"Profile {profile.ProfileId:N} already exists.");
        string name = profile.Name.Trim();
        if (await db.SimulationProfiles.AnyAsync(
                x => x.ArchivedAt == null && x.Name == name,
                cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException($"An active profile named '{name}' already exists.");
        db.SimulationProfiles.Add(new SimulationProfileEntity
        {
            ProfileId = profile.ProfileId,
            Name = name,
            CreatedAt = _timeProvider.GetUtcNow()
        });
        db.SimulationProfileRevisions.Add(ToRevision(profile));
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: ProfileNameIndex
            })
        {
            throw new InvalidOperationException($"An active profile named '{name}' already exists.", exception);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return profile;
    }

    public async Task<SimulationStrategyProfile> CloneAsync(Guid sourceId, int sourceRevision, Guid cloneId, string cloneName, CancellationToken cancellationToken = default)
    {
        if (cloneId == Guid.Empty || string.IsNullOrWhiteSpace(cloneName))
            throw new ArgumentException("Clone identity and name are required.");
        SimulationStrategyProfile source = await ReadAsync(sourceId, sourceRevision, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Profile {sourceId:N}/{sourceRevision} does not exist.");
        return await CreateAsync(source with
        {
            ProfileId = cloneId,
            Revision = 1,
            Name = cloneName.Trim(),
            ContentHash = string.Empty
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SimulationStrategyProfile> CreateRevisionAsync(Guid profileId, SimulationStrategyProfile draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.ProfileId != profileId)
            throw new ArgumentException("The route profile ID and draft profile ID must match.");
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        SimulationProfileEntity profileRow = await db.SimulationProfiles.SingleOrDefaultAsync(x => x.ProfileId == profileId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Profile {profileId:N} does not exist.");
        if (profileRow.ArchivedAt.HasValue)
            throw new InvalidOperationException("Archived profiles cannot receive new revisions.");
        int revision = await db.SimulationProfileRevisions.Where(x => x.ProfileId == profileId)
            .MaxAsync(x => (int?)x.Revision, cancellationToken).ConfigureAwait(false) ?? 0;
        SimulationStrategyProfile resolved = SimulationStrategyProfile.Create(draft with
        {
            Revision = revision + 1,
            ContentHash = string.Empty
        });
        db.SimulationProfileRevisions.Add(ToRevision(resolved));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return resolved;
    }

    public async Task<IReadOnlyList<SimulationStrategyProfile>> ListAsync(bool includeArchived = false, CancellationToken cancellationToken = default)
    {
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        Guid[] ids = await db.SimulationProfiles.AsNoTracking()
            .Where(x => includeArchived || x.ArchivedAt == null)
            .Select(x => x.ProfileId).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        List<SimulationProfileRevisionEntity> rows = await db.SimulationProfileRevisions.AsNoTracking()
            .Where(x => ids.Contains(x.ProfileId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.GroupBy(x => x.ProfileId).Select(x => x.MaxBy(row => row.Revision)!)
            .Select(Deserialize).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.ProfileId).ToArray();
    }

    public async Task<SimulationStrategyProfile?> ReadAsync(Guid profileId, int revision, CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty || revision < 1)
            return null;
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        SimulationProfileRevisionEntity? row = await db.SimulationProfileRevisions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProfileId == profileId && x.Revision == revision, cancellationToken).ConfigureAwait(false);
        return row is null ? null : Deserialize(row);
    }

    public async Task ArchiveAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        SimulationProfileEntity row = await db.SimulationProfiles.SingleOrDefaultAsync(x => x.ProfileId == profileId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Profile {profileId:N} does not exist.");
        row.ArchivedAt ??= _timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SimulationProfileDiff> DiffAsync(Guid leftId, int leftRevision, Guid rightId, int rightRevision, CancellationToken cancellationToken = default)
    {
        SimulationStrategyProfile left = await ReadAsync(leftId, leftRevision, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Left profile revision does not exist.");
        SimulationStrategyProfile right = await ReadAsync(rightId, rightRevision, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Right profile revision does not exist.");
        string leftJson = JsonSerializer.Serialize(left, Json);
        string rightJson = JsonSerializer.Serialize(right, Json);
        using JsonDocument leftDocument = JsonDocument.Parse(leftJson);
        using JsonDocument rightDocument = JsonDocument.Parse(rightJson);
        var paths = new List<string>();
        CollectChanges(leftDocument.RootElement, rightDocument.RootElement, "$", paths);
        return new SimulationProfileDiff { LeftResolvedJson = leftJson, RightResolvedJson = rightJson, ChangedPaths = paths };
    }

    internal static Guid RevisionId(Guid profileId, int revision)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"simulation-profile/{profileId:N}/{revision}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private SimulationProfileRevisionEntity ToRevision(SimulationStrategyProfile profile) => new()
    {
        ProfileRevisionId = RevisionId(profile.ProfileId, profile.Revision),
        ProfileId = profile.ProfileId,
        Revision = profile.Revision,
        ConfigurationHash = profile.ContentHash,
        SettingsJson = JsonSerializer.Serialize(profile, Json),
        CreatedAt = _timeProvider.GetUtcNow()
    };

    private static SimulationStrategyProfile Deserialize(SimulationProfileRevisionEntity row)
    {
        SimulationStrategyProfile profile = JsonSerializer.Deserialize<SimulationStrategyProfile>(row.SettingsJson, Json)
            ?? throw new JsonException($"Simulation profile revision {row.ProfileRevisionId} is empty.");
        profile.Validate();
        if (!string.Equals(profile.ContentHash, row.ConfigurationHash, StringComparison.Ordinal))
            throw new InvalidOperationException($"Simulation profile revision {row.ProfileRevisionId} failed hash verification.");
        return profile;
    }

    private static void CollectChanges(JsonElement left, JsonElement right, string path, ICollection<string> output)
    {
        if (left.ValueKind != right.ValueKind) { output.Add(path); return; }
        if (left.ValueKind == JsonValueKind.Object)
        {
            Dictionary<string, JsonElement> a = left.EnumerateObject().ToDictionary(x => x.Name, x => x.Value);
            Dictionary<string, JsonElement> b = right.EnumerateObject().ToDictionary(x => x.Name, x => x.Value);
            foreach (string name in a.Keys.Union(b.Keys).Order())
            {
                if (!a.TryGetValue(name, out JsonElement av) || !b.TryGetValue(name, out JsonElement bv)) output.Add($"{path}.{name}");
                else CollectChanges(av, bv, $"{path}.{name}", output);
            }
            return;
        }
        if (left.ValueKind == JsonValueKind.Array)
        {
            JsonElement[] a = left.EnumerateArray().ToArray();
            JsonElement[] b = right.EnumerateArray().ToArray();
            if (a.Length != b.Length) output.Add(path);
            for (int index = 0; index < Math.Min(a.Length, b.Length); index++) CollectChanges(a[index], b[index], $"{path}[{index}]", output);
            return;
        }
        if (left.GetRawText() != right.GetRawText()) output.Add(path);
    }
}
