using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dashboard.Live;

/// <summary>
/// Last-known-good OANDA instrument list, persisted to disk.
/// <para>
/// Instrument discovery is an account-scoped call, and OANDA's account API can be degraded while
/// its market-data API is perfectly healthy (observed 2026-08-28: <c>/v3/accounts/{id}/instruments</c>
/// returned 503 while <c>/v3/instruments/{i}/candles</c> returned normally). Without a cache, that
/// outage downgrades the whole broker to "not configured" and the workspace becomes unusable even
/// though candles would stream fine.
/// </para>
/// <para>
/// Persisted rather than kept in memory only, because the case that actually hurts is a backend
/// restart *during* the outage - an in-memory cache is empty at exactly the moment it is needed.
/// </para>
/// </summary>
public sealed class OandaInstrumentCache
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _path;
    private readonly ILogger _logger;

    public OandaInstrumentCache(string stateDirectory, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        ArgumentNullException.ThrowIfNull(logger);
        _path = Path.Combine(stateDirectory, "oanda-instruments.json");
        _logger = logger;
    }

    /// <summary>
    /// Reads the cached list for this account/environment, or null when absent, unreadable, or
    /// recorded against a different account. The account check matters: serving one account's
    /// instruments to another would silently offer symbols that account cannot trade.
    /// </summary>
    public IReadOnlyList<WorkspaceAsset>? TryLoad(string accountId, string environment)
    {
        try
        {
            if (!File.Exists(_path))
                return null;
            CachedInstruments? cached = JsonSerializer.Deserialize<CachedInstruments>(
                File.ReadAllText(_path), SerializerOptions);
            if (cached?.Assets is null || cached.Assets.Count == 0)
                return null;
            if (!string.Equals(cached.AccountId, accountId, StringComparison.Ordinal) ||
                !string.Equals(cached.Environment, environment, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return cached.Assets;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable cache must never break startup - it is an optimisation.
            _logger.LogWarning(exception, "Could not read the cached OANDA instrument list.");
            return null;
        }
    }

    /// <summary>Records a successful discovery. Empty lists are ignored - never cache a miss.</summary>
    public void Save(string accountId, string environment, IReadOnlyList<WorkspaceAsset> assets, DateTimeOffset at)
    {
        if (assets.Count == 0)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var payload = new CachedInstruments
            {
                AccountId = accountId,
                Environment = environment,
                RetrievedAt = at,
                Assets = assets
            };
            // Write-then-move so a crash mid-write cannot leave a truncated file behind.
            string temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(payload, SerializerOptions));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Could not persist the OANDA instrument list.");
        }
    }

    public sealed record CachedInstruments
    {
        public string AccountId { get; init; } = string.Empty;
        public string Environment { get; init; } = string.Empty;
        public DateTimeOffset RetrievedAt { get; init; }
        public IReadOnlyList<WorkspaceAsset> Assets { get; init; } = [];
    }
}
