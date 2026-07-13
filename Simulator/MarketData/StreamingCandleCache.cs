using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Brokers.Abstractions;
using Brokers.Models;
using Simulator.Abstractions;
using Simulator.Models;

namespace Simulator.MarketData;

/// <summary>
/// Versioned compressed candle cache. Readers stream candles without loading the full year into RAM.
/// </summary>
public sealed class StreamingCandleCache
{
    public const int SchemaVersion = 2;
    public const string DataSourceVersion = "oanda-mid-v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static string BuildCacheKey(
        string broker,
        BrokerEnvironment environment,
        InstrumentKey instrument,
        BarInterval interval,
        DateTimeOffset from,
        DateTimeOffset to,
        string priceComponents = "mid")
    {
        string raw =
            $"{broker}|{environment}|{instrument.Value}|{FormatInterval(interval)}|{from:O}|{to:O}|{priceComponents}|{SchemaVersion}|{DataSourceVersion}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }

    public static string GetPath(
        string cacheDirectory,
        string broker,
        BrokerEnvironment environment,
        InstrumentKey instrument,
        BarInterval interval,
        DateTimeOffset from,
        DateTimeOffset to,
        string priceComponents = "mid")
    {
        string key = BuildCacheKey(broker, environment, instrument, interval, from, to, priceComponents);
        string instrumentPart = Sanitize(instrument.Value);
        string name = $"{instrumentPart}_{FormatInterval(interval)}_{from:yyyyMMdd}_{to:yyyyMMdd}_{key}.jsonl.gz";
        return Path.GetFullPath(Path.Combine(cacheDirectory, name));
    }

    public static async Task<bool> TryValidateAsync(
        string path,
        HistoricalCandleRequest request,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return false;

        await using FileStream file = File.OpenRead(path);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        string? headerLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (headerLine is null)
            return false;

        CacheHeader? header = JsonSerializer.Deserialize<CacheHeader>(headerLine, JsonOptions);
        if (header is null ||
            header.SchemaVersion != SchemaVersion ||
            header.DataSourceVersion != DataSourceVersion ||
            !string.Equals(header.Instrument, request.Instrument.Value, StringComparison.OrdinalIgnoreCase) ||
            header.Interval != FormatInterval(request.BaseInterval) ||
            header.From != request.From ||
            header.To != request.To)
        {
            return false;
        }

        return true;
    }

    public static async IAsyncEnumerable<MarketCandle> ReadStreamAsync(
        string path,
        InstrumentKey instrument,
        BarInterval interval,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using FileStream file = File.OpenRead(path);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);

        // Skip header.
        _ = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
                break;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            CacheCandleRow? row = JsonSerializer.Deserialize<CacheCandleRow>(line, JsonOptions);
            if (row is null)
                continue;

            yield return MarketCandle.FromMid(new Candle
            {
                Instrument = instrument,
                Interval = interval,
                OpenTime = row.OpenTime,
                CloseTime = row.CloseTime,
                Prices = new Ohlc(row.Open, row.High, row.Low, row.Close),
                Volume = row.Volume is null ? null : new MarketVolume(row.Volume.Value, row.VolumeKind),
                IsComplete = true
            });
        }
    }

    public static async Task WriteFromStreamAsync(
        string path,
        HistoricalCandleRequest request,
        string broker,
        BrokerEnvironment environment,
        IAsyncEnumerable<MarketCandle> candles,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        string temporary = path + ".tmp";
        long count = 0;
        DateTimeOffset? first = null;
        DateTimeOffset? last = null;
        var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        await using (FileStream file = File.Create(temporary))
        await using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
        await using (var writer = new StreamWriter(gzip, Encoding.UTF8))
        {
            // Placeholder header; rewritten is impractical in gzip stream, so we embed counts at end via sidecar meta.
            var header = new CacheHeader(
                SchemaVersion,
                DataSourceVersion,
                broker,
                environment.ToString(),
                request.Instrument.Value,
                FormatInterval(request.BaseInterval),
                request.From,
                request.To,
                "mid",
                0,
                null,
                null,
                string.Empty,
                DateTimeOffset.UtcNow);
            await writer.WriteLineAsync(JsonSerializer.Serialize(header, JsonOptions)).ConfigureAwait(false);

            await foreach (MarketCandle marketCandle in candles.WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                Candle candle = marketCandle.Mid;
                first ??= candle.OpenTime;
                last = candle.OpenTime;
                count++;
                string rowJson = JsonSerializer.Serialize(new CacheCandleRow(
                    candle.OpenTime,
                    candle.CloseTime ?? request.BaseInterval.AddTo(candle.OpenTime),
                    candle.Prices.Open,
                    candle.Prices.High,
                    candle.Prices.Low,
                    candle.Prices.Close,
                    candle.Volume?.Value,
                    candle.Volume?.Kind ?? VolumeKind.Unknown), JsonOptions);
                await writer.WriteLineAsync(rowJson).ConfigureAwait(false);
                hasher.AppendData(Encoding.UTF8.GetBytes(rowJson));
            }

            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        string hash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        string metaPath = path + ".meta.json";
        var meta = new CacheMeta(count, first, last, hash, DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(
            metaPath + ".tmp",
            JsonSerializer.Serialize(meta, JsonOptions),
            cancellationToken).ConfigureAwait(false);
        File.Move(metaPath + ".tmp", metaPath, overwrite: true);
        File.Move(temporary, path, overwrite: true);
    }

    public static string FormatInterval(BarInterval interval) => interval.Unit switch
    {
        BarUnit.Second => $"{interval.Value}s",
        BarUnit.Minute => $"{interval.Value}m",
        BarUnit.Hour => $"{interval.Value}h",
        BarUnit.Day => $"{interval.Value}d",
        BarUnit.Week => $"{interval.Value}w",
        BarUnit.Month => $"{interval.Value}mo",
        _ => interval.ToString()
    };

    private static string Sanitize(string value) => string.Concat(value.Select(character =>
        char.IsLetterOrDigit(character) ? character : '_'));

    private sealed record CacheHeader(
        int SchemaVersion,
        string DataSourceVersion,
        string Broker,
        string Environment,
        string Instrument,
        string Interval,
        DateTimeOffset From,
        DateTimeOffset To,
        string PriceComponents,
        long CandleCount,
        DateTimeOffset? FirstCandle,
        DateTimeOffset? LastCandle,
        string Hash,
        DateTimeOffset CreatedAt);

    private sealed record CacheCandleRow(
        DateTimeOffset OpenTime,
        DateTimeOffset CloseTime,
        decimal Open,
        decimal High,
        decimal Low,
        decimal Close,
        decimal? Volume,
        VolumeKind VolumeKind);

    private sealed record CacheMeta(
        long CandleCount,
        DateTimeOffset? FirstCandle,
        DateTimeOffset? LastCandle,
        string Hash,
        DateTimeOffset CreatedAt);
}
