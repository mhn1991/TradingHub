using System.Text.Json;
using System.Text.Json.Serialization;
using Simulator.Experiments.Models;

namespace Simulator.Experiments.Persistence;

public sealed class FileSimulationStrategyProfileStore : ISimulationStrategyProfileStore
{
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public FileSimulationStrategyProfileStore(string root = ".cache/simulation-profiles")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public async Task<SimulationStrategyProfile> CreateAsync(
        SimulationStrategyProfile draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.Revision != 1)
            throw new ArgumentException("A new profile starts at revision 1.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Directory.Exists(ProfileDirectory(draft.ProfileId)))
                throw new InvalidOperationException($"Profile {draft.ProfileId:N} already exists.");
            SimulationStrategyProfile profile = SimulationStrategyProfile.Create(draft);
            await WriteAsync(profile, cancellationToken).ConfigureAwait(false);
            return profile;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SimulationStrategyProfile> CloneAsync(
        Guid sourceId,
        int sourceRevision,
        Guid cloneId,
        string cloneName,
        CancellationToken cancellationToken = default)
    {
        if (cloneId == Guid.Empty || string.IsNullOrWhiteSpace(cloneName))
            throw new ArgumentException("Clone identity and name are required.");
        SimulationStrategyProfile source = await ReadAsync(sourceId, sourceRevision, cancellationToken)
            .ConfigureAwait(false) ?? throw new KeyNotFoundException($"Profile {sourceId:N}/{sourceRevision} does not exist.");
        return await CreateAsync(source with
        {
            ProfileId = cloneId,
            Revision = 1,
            Name = cloneName.Trim(),
            ContentHash = string.Empty
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SimulationStrategyProfile> CreateRevisionAsync(
        Guid profileId,
        SimulationStrategyProfile draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (draft.ProfileId != profileId)
                throw new ArgumentException("The route profile ID and draft profile ID must match.");
            int latest = LatestRevision(profileId);
            if (latest == 0)
                throw new KeyNotFoundException($"Profile {profileId:N} does not exist.");
            if (File.Exists(ArchivePath(profileId)))
                throw new InvalidOperationException("Archived profiles cannot receive new revisions.");
            SimulationStrategyProfile profile = SimulationStrategyProfile.Create(draft with
            {
                Revision = latest + 1,
                ContentHash = string.Empty
            });
            await WriteAsync(profile, cancellationToken).ConfigureAwait(false);
            return profile;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<SimulationStrategyProfile>> ListAsync(
        bool includeArchived = false,
        CancellationToken cancellationToken = default)
    {
        var profiles = new List<SimulationStrategyProfile>();
        foreach (string directory in Directory.EnumerateDirectories(_root).OrderBy(item => item, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out Guid id))
                continue;
            if (!includeArchived && File.Exists(ArchivePath(id)))
                continue;
            int revision = LatestRevision(id);
            if (revision > 0 && await ReadAsync(id, revision, cancellationToken).ConfigureAwait(false) is { } profile)
                profiles.Add(profile);
        }
        return profiles.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ProfileId).ToArray();
    }

    public async Task<SimulationStrategyProfile?> ReadAsync(
        Guid profileId,
        int revision,
        CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty || revision < 1)
            return null;
        string path = RevisionPath(profileId, revision);
        if (!File.Exists(path))
            return null;
        await using FileStream stream = File.OpenRead(path);
        SimulationStrategyProfile? profile = await JsonSerializer.DeserializeAsync<SimulationStrategyProfile>(
            stream, _json, cancellationToken).ConfigureAwait(false);
        profile?.Validate();
        return profile;
    }

    public async Task ArchiveAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (LatestRevision(profileId) == 0)
                throw new KeyNotFoundException($"Profile {profileId:N} does not exist.");
            await File.WriteAllTextAsync(
                ArchivePath(profileId),
                DateTimeOffset.UtcNow.ToString("O"),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SimulationProfileDiff> DiffAsync(
        Guid leftId,
        int leftRevision,
        Guid rightId,
        int rightRevision,
        CancellationToken cancellationToken = default)
    {
        SimulationStrategyProfile left = await ReadAsync(leftId, leftRevision, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Left profile revision does not exist.");
        SimulationStrategyProfile right = await ReadAsync(rightId, rightRevision, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Right profile revision does not exist.");
        string leftJson = JsonSerializer.Serialize(left, _json);
        string rightJson = JsonSerializer.Serialize(right, _json);
        using JsonDocument leftDocument = JsonDocument.Parse(leftJson);
        using JsonDocument rightDocument = JsonDocument.Parse(rightJson);
        var paths = new List<string>();
        CollectChanges(leftDocument.RootElement, rightDocument.RootElement, "$", paths);
        return new SimulationProfileDiff
        {
            LeftResolvedJson = leftJson,
            RightResolvedJson = rightJson,
            ChangedPaths = paths
        };
    }

    private async Task WriteAsync(SimulationStrategyProfile profile, CancellationToken cancellationToken)
    {
        string directory = ProfileDirectory(profile.ProfileId);
        Directory.CreateDirectory(directory);
        string path = RevisionPath(profile.ProfileId, profile.Revision);
        if (File.Exists(path))
            throw new InvalidOperationException($"Profile revision {profile.ProfileId:N}/{profile.Revision} already exists.");
        string temporary = Path.Combine(directory, $".{profile.Revision}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, profile, _json, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static void CollectChanges(JsonElement left, JsonElement right, string path, ICollection<string> output)
    {
        if (left.ValueKind != right.ValueKind)
        {
            output.Add(path);
            return;
        }
        if (left.ValueKind == JsonValueKind.Object)
        {
            Dictionary<string, JsonElement> leftProperties = left.EnumerateObject().ToDictionary(item => item.Name, item => item.Value);
            Dictionary<string, JsonElement> rightProperties = right.EnumerateObject().ToDictionary(item => item.Name, item => item.Value);
            foreach (string name in leftProperties.Keys.Union(rightProperties.Keys).OrderBy(item => item, StringComparer.Ordinal))
            {
                if (!leftProperties.TryGetValue(name, out JsonElement leftValue) ||
                    !rightProperties.TryGetValue(name, out JsonElement rightValue))
                    output.Add($"{path}.{name}");
                else
                    CollectChanges(leftValue, rightValue, $"{path}.{name}", output);
            }
            return;
        }
        if (left.ValueKind == JsonValueKind.Array)
        {
            JsonElement.ArrayEnumerator leftItems = left.EnumerateArray();
            JsonElement.ArrayEnumerator rightItems = right.EnumerateArray();
            JsonElement[] leftArray = leftItems.ToArray();
            JsonElement[] rightArray = rightItems.ToArray();
            if (leftArray.Length != rightArray.Length)
                output.Add(path);
            int common = Math.Min(leftArray.Length, rightArray.Length);
            for (int index = 0; index < common; index++)
                CollectChanges(leftArray[index], rightArray[index], $"{path}[{index}]", output);
            return;
        }
        if (left.GetRawText() != right.GetRawText())
            output.Add(path);
    }

    private int LatestRevision(Guid id)
    {
        string directory = ProfileDirectory(id);
        if (!Directory.Exists(directory))
            return 0;
        return Directory.EnumerateFiles(directory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => int.TryParse(name, out _))
            .Select(name => int.Parse(name!))
            .DefaultIfEmpty(0)
            .Max();
    }

    private string ProfileDirectory(Guid id) => Path.Combine(_root, id.ToString("N"));
    private string RevisionPath(Guid id, int revision) => Path.Combine(ProfileDirectory(id), $"{revision}.json");
    private string ArchivePath(Guid id) => Path.Combine(ProfileDirectory(id), "archived");
}
