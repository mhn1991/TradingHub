using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TradingPolicies;

/// <summary>
/// Persisted <see cref="TradingPolicyProfile"/> storage - replaces today's manual workflow
/// (an operator copies the JSON <see cref="TradingPolicyProfileJson.Serialize"/> returns out of
/// an HTTP response and hand-edits it into <c>LiveTradingHost</c> config). Write-once per
/// (ProfileId, Revision): promoting a new revision of an existing strategy's policy always adds
/// a new entry, never mutates one in place, mirroring <c>ICalibrationArtifactRepository</c>'s
/// immutability discipline.
/// </summary>
public interface ITradingPolicyProfileStore
{
    Task StoreAsync(TradingPolicyProfile profile, CancellationToken cancellationToken = default);

    Task<TradingPolicyProfile?> GetAsync(Guid profileId, int revision, CancellationToken cancellationToken = default);

    /// <summary>Newest <see cref="TradingPolicyProfileStatus.ApprovedForDemo"/> profile for a strategy, or null if none has ever been approved.</summary>
    Task<TradingPolicyProfile?> GetLatestApprovedAsync(string strategyId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TradingPolicyProfile>> ListAsync(string? strategyId = null, int take = 50, CancellationToken cancellationToken = default);
}

/// <summary>
/// File-backed <see cref="ITradingPolicyProfileStore"/>. Mirrors
/// <c>Simulator.Calibration.FileCalibrationArtifactRepository</c>'s pattern: atomic tmp-then-move
/// writes, content re-verified via the profile's own <see cref="TradingPolicyProfile.Validate"/>
/// (which recomputes <see cref="TradingPolicyProfile.ConfigurationHash"/>) on every read rather
/// than a separate envelope hash - the profile is already self-verifying.
/// </summary>
public sealed class FileTradingPolicyProfileStore : ITradingPolicyProfileStore
{
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileTradingPolicyProfileStore(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new ArgumentException("Trading policy profile directory is required.", nameof(rootDirectory));
        _root = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(_root);
    }

    public async Task StoreAsync(TradingPolicyProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        string json = TradingPolicyProfileJson.Serialize(profile);
        string path = GetPath(profile.ProfileId, profile.Revision);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(path))
            {
                // Same (ProfileId, Revision) written twice must be byte-identical content -
                // otherwise something is trying to silently mutate an already-promoted revision.
                string existing = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(NormalizedHash(existing), NormalizedHash(json), StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Profile {profile.ProfileId:N} revision {profile.Revision} already exists with different content.");
                }
                return;
            }

            string temporary = path + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, json, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, path, overwrite: false);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TradingPolicyProfile?> GetAsync(Guid profileId, int revision, CancellationToken cancellationToken = default)
    {
        string path = GetPath(profileId, revision);
        if (!File.Exists(path))
            return null;
        string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return TradingPolicyProfileJson.Deserialize(json);
    }

    public async Task<TradingPolicyProfile?> GetLatestApprovedAsync(string strategyId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        IReadOnlyList<TradingPolicyProfile> all = await ListAsync(strategyId, take: int.MaxValue, cancellationToken).ConfigureAwait(false);
        return all
            .Where(p => p.Status == TradingPolicyProfileStatus.ApprovedForDemo)
            .OrderByDescending(p => p.Revision)
            .FirstOrDefault();
    }

    public async Task<IReadOnlyList<TradingPolicyProfile>> ListAsync(
        string? strategyId = null, int take = 50, CancellationToken cancellationToken = default)
    {
        if (take < 1)
            throw new ArgumentOutOfRangeException(nameof(take));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var items = new List<TradingPolicyProfile>();
            foreach (string path in Directory.EnumerateFiles(_root, "*.json").OrderByDescending(File.GetLastWriteTimeUtc))
            {
                cancellationToken.ThrowIfCancellationRequested();
                TradingPolicyProfile? profile;
                try
                {
                    string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                    profile = TradingPolicyProfileJson.Deserialize(json);
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException)
                {
                    continue; // corrupt/tampered file - skip rather than fail the whole listing
                }
                if (strategyId is not null && !string.Equals(profile.StrategyId, strategyId, StringComparison.OrdinalIgnoreCase))
                    continue;
                items.Add(profile);
                if (items.Count >= take)
                    break;
            }
            return items;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string GetPath(Guid profileId, int revision) => Path.Combine(_root, $"{profileId:N}-r{revision}.json");

    private static string NormalizedHash(string json)
    {
        // Compares content regardless of incidental whitespace differences between writes.
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(json))));
        return Convert.ToHexString(hash);
    }
}
