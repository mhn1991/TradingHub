using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Brokers.Abstractions;
using Brokers.Models;
using Simulator.Models;

namespace Simulator.MarketData;

public sealed record StreamingCacheCommitMetadata(
    long CandleCount,
    DateTimeOffset FirstCandle,
    DateTimeOffset LastCandle,
    string ContentHash,
    DateTimeOffset CreatedAt);

public interface IStreamingCandleCacheWriter : IAsyncDisposable
{
    ValueTask AppendAsync(MarketCandle candle, CancellationToken cancellationToken = default);

    ValueTask CommitAsync(StreamingCacheCommitMetadata metadata, CancellationToken cancellationToken = default);

    ValueTask AbortAsync(CancellationToken cancellationToken = default);

    string TemporaryPath { get; }
    string FinalPath { get; }
}

public sealed record StreamingCacheManifest
{
    public required int SchemaVersion { get; init; }
    public required string DataSourceVersion { get; init; }
    public required string Broker { get; init; }
    public required string Environment { get; init; }
    public required string Instrument { get; init; }
    public required string Interval { get; init; }
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }
    public required string PriceComponents { get; init; }
    public required long CandleCount { get; init; }
    public required DateTimeOffset FirstCandle { get; init; }
    public required DateTimeOffset LastCandle { get; init; }
    public required string ContentHash { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required bool Complete { get; init; }
}

/// <summary>
/// Incremental cache writer. Appends while the simulator consumes the same candles.
/// Temporary files are never published as valid cache until CommitAsync.
/// </summary>
public sealed class StreamingCandleCacheWriter : IStreamingCandleCacheWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _finalPath;
    private readonly string _temporaryPath;
    private readonly string _manifestPath;
    private readonly HistoricalCandleRequest _request;
    private readonly string _broker;
    private readonly BrokerEnvironment _environment;
    private readonly IncrementalHash _hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private FileStream? _file;
    private GZipStream? _gzip;
    private StreamWriter? _writer;
    private long _count;
    private DateTimeOffset? _first;
    private DateTimeOffset? _last;
    private bool _committed;
    private bool _aborted;

    public StreamingCandleCacheWriter(
        string finalPath,
        HistoricalCandleRequest request,
        string broker,
        BrokerEnvironment environment)
    {
        _finalPath = Path.GetFullPath(finalPath);
        _temporaryPath = _finalPath + ".tmp";
        _manifestPath = _finalPath + ".manifest.json";
        _request = request;
        _broker = broker;
        _environment = environment;

        string? directory = Path.GetDirectoryName(_finalPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        // A writer for this exact cache key owns its unpublished sidecars.
        TryDelete(_temporaryPath);
        TryDelete(_manifestPath + ".tmp");
        CleanupStaleTemporary(directory);
    }

    public string TemporaryPath => _temporaryPath;
    public string FinalPath => _finalPath;
    public long CandleCount => _count;
    public string? CurrentHashHex => _count == 0 ? null : Convert.ToHexString(_hasher.GetCurrentHash()).ToLowerInvariant();

    public async ValueTask AppendAsync(MarketCandle candle, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_committed || _aborted, this);
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);

        Candle mid = candle.Mid;
        _first ??= mid.OpenTime;
        _last = mid.OpenTime;
        _count++;

        string rowJson = JsonSerializer.Serialize(new CacheCandleRow(
            mid.OpenTime,
            mid.CloseTime ?? _request.BaseInterval.AddTo(mid.OpenTime),
            mid.Prices.Open,
            mid.Prices.High,
            mid.Prices.Low,
            mid.Prices.Close,
            mid.Volume?.Value,
            mid.Volume?.Kind ?? VolumeKind.Unknown), JsonOptions);
        await _writer!.WriteLineAsync(rowJson.AsMemory(), cancellationToken).ConfigureAwait(false);
        _hasher.AppendData(Encoding.UTF8.GetBytes(rowJson));
    }

    public async ValueTask CommitAsync(
        StreamingCacheCommitMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        if (_aborted)
            throw new InvalidOperationException("Cache writer was aborted.");
        if (_committed)
            return;

        string contentHash = _count == 0
            ? string.Empty
            : Convert.ToHexString(_hasher.GetCurrentHash()).ToLowerInvariant();
        if (metadata.CandleCount != _count ||
            metadata.FirstCandle != _first ||
            metadata.LastCandle != _last ||
            !string.Equals(metadata.ContentHash, contentHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Cache commit metadata does not match the written candle stream.");
        }

        if (_writer is not null)
        {
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            await _writer.DisposeAsync().ConfigureAwait(false);
            _writer = null;
        }

        if (_gzip is not null)
        {
            await _gzip.DisposeAsync().ConfigureAwait(false);
            _gzip = null;
        }

        if (_file is not null)
        {
            await _file.DisposeAsync().ConfigureAwait(false);
            _file = null;
        }

        var manifest = new StreamingCacheManifest
        {
            SchemaVersion = StreamingCandleCache.SchemaVersion,
            DataSourceVersion = StreamingCandleCache.DataSourceVersion,
            Broker = _broker,
            Environment = _environment.ToString(),
            Instrument = _request.Instrument.Value,
            Interval = StreamingCandleCache.FormatInterval(_request.BaseInterval),
            From = _request.From,
            To = _request.To,
            PriceComponents = "mid",
            CandleCount = metadata.CandleCount,
            FirstCandle = metadata.FirstCandle,
            LastCandle = metadata.LastCandle,
            ContentHash = contentHash,
            CreatedAt = metadata.CreatedAt,
            Complete = true
        };

        string manifestTmp = _manifestPath + ".tmp";
        await File.WriteAllTextAsync(
            manifestTmp,
            JsonSerializer.Serialize(manifest, JsonOptions),
            cancellationToken).ConfigureAwait(false);

        File.Move(_temporaryPath, _finalPath, overwrite: true);
        File.Move(manifestTmp, _manifestPath, overwrite: true);
        // Legacy sidecar for older readers.
        await File.WriteAllTextAsync(
            _finalPath + ".meta.json",
            JsonSerializer.Serialize(new
            {
                metadata.CandleCount,
                metadata.FirstCandle,
                metadata.LastCandle,
                Hash = metadata.ContentHash,
                metadata.CreatedAt
            }, JsonOptions),
            cancellationToken).ConfigureAwait(false);

        _committed = true;
    }

    public ValueTask AbortAsync(CancellationToken cancellationToken = default)
    {
        _aborted = true;
        try
        {
            _writer?.Dispose();
            _gzip?.Dispose();
            _file?.Dispose();
        }
        catch
        {
        }

        _writer = null;
        _gzip = null;
        _file = null;
        TryDelete(_temporaryPath);
        TryDelete(_manifestPath + ".tmp");
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_committed && !_aborted)
            await AbortAsync().ConfigureAwait(false);
    }

    public static async Task<bool> TryValidateManifestAsync(
        string dataPath,
        HistoricalCandleRequest request,
        CancellationToken cancellationToken)
    {
        string manifestPath = dataPath + ".manifest.json";
        if (!File.Exists(dataPath) || !File.Exists(manifestPath))
            return false;

        try
        {
            string json = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            StreamingCacheManifest? manifest = JsonSerializer.Deserialize<StreamingCacheManifest>(json, JsonOptions);
            if (manifest is null || !manifest.Complete)
                return false;
            if (manifest.SchemaVersion != StreamingCandleCache.SchemaVersion ||
                manifest.DataSourceVersion != StreamingCandleCache.DataSourceVersion)
                return false;
            if (!string.Equals(manifest.Instrument, request.Instrument.Value, StringComparison.OrdinalIgnoreCase) ||
                manifest.Interval != StreamingCandleCache.FormatInterval(request.BaseInterval) ||
                manifest.From != request.From ||
                manifest.To != request.To ||
                !string.Equals(manifest.PriceComponents, "mid", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Read the entire gzip stream (including its trailer) and validate the
            // manifest against the canonical serialized row stream.
            await using FileStream file = File.OpenRead(dataPath);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            string? header = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(header))
                return false;

            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long count = 0;
            DateTimeOffset? first = null;
            DateTimeOffset? last = null;
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    return false;

                CacheCandleRow? row = JsonSerializer.Deserialize<CacheCandleRow>(line, JsonOptions);
                if (row is null ||
                    row.OpenTime < request.From ||
                    row.OpenTime >= request.To ||
                    row.CloseTime != request.BaseInterval.AddTo(row.OpenTime) ||
                    row.High < Math.Max(row.Open, row.Close) ||
                    row.Low > Math.Min(row.Open, row.Close) ||
                    row.High < row.Low ||
                    row.Volume < 0m ||
                    (last is not null && row.OpenTime <= last))
                {
                    return false;
                }

                first ??= row.OpenTime;
                last = row.OpenTime;
                count++;
                hasher.AppendData(Encoding.UTF8.GetBytes(line));
            }

            string hash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            return count == manifest.CandleCount &&
                   first == manifest.FirstCandle &&
                   last == manifest.LastCandle &&
                   string.Equals(hash, manifest.ContentHash, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task EnsureOpenAsync(CancellationToken cancellationToken)
    {
        if (_writer is not null)
            return;

        _file = new FileStream(
            _temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        _gzip = new GZipStream(_file, CompressionLevel.Fastest, leaveOpen: false);
        _writer = new StreamWriter(_gzip, Encoding.UTF8);

        // Minimal header line for stream readers that skip first line.
        var header = new
        {
            SchemaVersion = StreamingCandleCache.SchemaVersion,
            DataSourceVersion = StreamingCandleCache.DataSourceVersion,
            Instrument = _request.Instrument.Value,
            Interval = StreamingCandleCache.FormatInterval(_request.BaseInterval),
            From = _request.From,
            To = _request.To
        };
        await _writer.WriteLineAsync(JsonSerializer.Serialize(header, JsonOptions).AsMemory(), cancellationToken)
            .ConfigureAwait(false);
    }

    private static void CleanupStaleTemporary(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return;
        foreach (string path in Directory.EnumerateFiles(directory, "*.tmp"))
        {
            try
            {
                FileInfo info = new(path);
                if (DateTime.UtcNow - info.LastWriteTimeUtc > TimeSpan.FromDays(1))
                    info.Delete();
            }
            catch
            {
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private sealed record CacheCandleRow(
        DateTimeOffset OpenTime,
        DateTimeOffset CloseTime,
        decimal Open,
        decimal High,
        decimal Low,
        decimal Close,
        decimal? Volume,
        VolumeKind VolumeKind);
}
