using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Brokers.Models;

namespace BacktestRunner;

internal static class HistoricalCandleCache
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static string GetPath(BacktestCommandOptions options)
    {
        string instrument = Sanitize(options.Instrument.Value);
        string interval = BacktestCommandOptions.FormatInterval(options.ExecutionInterval);
        string name = $"{instrument}_{interval}_{options.From:yyyyMMdd}_{options.To:yyyyMMdd}.json.gz";
        return Path.GetFullPath(Path.Combine(options.CacheDirectory, name));
    }

    public static async Task<IReadOnlyList<Candle>?> TryReadAsync(
        string path,
        BacktestCommandOptions options,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;

        await using FileStream file = File.OpenRead(path);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        CandleCacheDocument? document = await JsonSerializer.DeserializeAsync<CandleCacheDocument>(
            gzip,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        if (document is null ||
            document.SchemaVersion != 1 ||
            !string.Equals(document.Instrument, options.Instrument.Value, StringComparison.OrdinalIgnoreCase) ||
            document.Interval != BacktestCommandOptions.FormatInterval(options.ExecutionInterval) ||
            document.From != options.From ||
            document.To != options.To)
        {
            return null;
        }

        return document.Candles.Select(item => new Candle
        {
            Instrument = options.Instrument,
            Interval = options.ExecutionInterval,
            OpenTime = item.OpenTime,
            CloseTime = item.CloseTime,
            Prices = new Ohlc(item.Open, item.High, item.Low, item.Close),
            Volume = item.Volume is null ? null : new MarketVolume(item.Volume.Value, item.VolumeKind),
            IsComplete = true
        }).ToArray();
    }

    public static async Task WriteAsync(
        string path,
        BacktestCommandOptions options,
        IReadOnlyList<Candle> candles,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        string temporary = path + ".tmp";
        var document = new CandleCacheDocument(
            1,
            options.Instrument.Value,
            BacktestCommandOptions.FormatInterval(options.ExecutionInterval),
            options.From,
            options.To,
            candles.Select(candle => new CandleCacheItem(
                candle.OpenTime,
                candle.CloseTime ?? options.ExecutionInterval.AddTo(candle.OpenTime),
                candle.Prices.Open,
                candle.Prices.High,
                candle.Prices.Low,
                candle.Prices.Close,
                candle.Volume?.Value,
                candle.Volume?.Kind ?? VolumeKind.Unknown)).ToArray());

        await using (FileStream file = File.Create(temporary))
        await using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
        {
            await JsonSerializer.SerializeAsync(gzip, document, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(temporary, path, overwrite: true);
    }

    private static string Sanitize(string value) => string.Concat(value.Select(character =>
        char.IsLetterOrDigit(character) ? character : '_'));

    private sealed record CandleCacheDocument(
        int SchemaVersion,
        string Instrument,
        string Interval,
        DateTimeOffset From,
        DateTimeOffset To,
        IReadOnlyList<CandleCacheItem> Candles);

    private sealed record CandleCacheItem(
        DateTimeOffset OpenTime,
        DateTimeOffset CloseTime,
        decimal Open,
        decimal High,
        decimal Low,
        decimal Close,
        decimal? Volume,
        VolumeKind VolumeKind);
}
