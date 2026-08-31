using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using TradingClassifier.Features;

namespace TradingClassifierRunner;

/// <summary>Loads the OHLC series the blueprint's section 3 describes.</summary>
public static class CandleSources
{
    /// <summary>
    /// CSV with a <c>timestamp,open,high,low,close</c> row shape. A header line is detected and
    /// skipped; extra trailing columns (volume, spread) are ignored, because section 1 restricts
    /// the model to OHLC.
    /// </summary>
    public static IReadOnlyList<ClassifierCandle> FromCsv(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        List<ClassifierCandle> candles = [];

        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            string[] parts = line.Split(',');
            if (parts.Length < 5)
                continue;
            if (!DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset timestamp))
            {
                continue;   // header or malformed row
            }

            if (!TryDecimal(parts[1], out decimal open) || !TryDecimal(parts[2], out decimal high)
                || !TryDecimal(parts[3], out decimal low) || !TryDecimal(parts[4], out decimal close))
            {
                continue;
            }

            candles.Add(new ClassifierCandle(timestamp, open, high, low, close));
        }

        return Deduplicate(candles);
    }

    /// <summary>
    /// Reads a simulation run's <c>market/chunk-*.json.gz</c> replay files - the artefacts a
    /// backtest already writes, so a model can be trained on exactly the candles a run replayed.
    /// </summary>
    public static IReadOnlyList<ClassifierCandle> FromSimulationMarket(string marketDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(marketDirectory);
        List<ClassifierCandle> candles = [];

        foreach (string file in Directory.EnumerateFiles(marketDirectory, "chunk-*.json.gz").OrderBy(name => name, StringComparer.Ordinal))
        {
            using FileStream stream = File.OpenRead(file);
            using GZipStream gzip = new(stream, CompressionMode.Decompress);
            using JsonDocument document = JsonDocument.Parse(gzip);

            foreach (JsonElement record in document.RootElement.EnumerateArray())
            {
                if (!record.TryGetProperty("availableAt", out JsonElement availableAt))
                    continue;
                record.TryGetProperty("volume", out JsonElement volume);
                candles.Add(new ClassifierCandle(
                    availableAt.GetDateTimeOffset(),
                    record.GetProperty("open").GetDecimal(),
                    record.GetProperty("high").GetDecimal(),
                    record.GetProperty("low").GetDecimal(),
                    record.GetProperty("close").GetDecimal(),
                    volume.ValueKind == JsonValueKind.Number ? volume.GetDecimal() : null));
            }
        }

        return Deduplicate(candles);
    }

    /// <summary>
    /// A deterministic random walk, for smoke-testing the pipeline end to end without market data.
    /// <para>
    /// There is no edge in a random walk by construction, so a model that reports a profit factor
    /// meaningfully above 1 on this is evidence of a leak in the pipeline, not of skill. That makes
    /// it a useful negative control, which is the only reason it exists.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ClassifierCandle> RandomWalk(int count, int seed = 7, decimal start = 1.1000m)
    {
        Random random = new(seed);
        List<ClassifierCandle> candles = new(count);
        decimal price = start;
        DateTimeOffset timestamp = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

        for (int index = 0; index < count; index++)
        {
            decimal open = price;
            decimal drift = (decimal)((random.NextDouble() - 0.5) * 0.0020);
            decimal close = Math.Round(open + drift, 5);
            decimal high = Math.Max(open, close) + (decimal)(random.NextDouble() * 0.0006);
            decimal low = Math.Min(open, close) - (decimal)(random.NextDouble() * 0.0006);

            candles.Add(new ClassifierCandle(timestamp, open, Math.Round(high, 5), Math.Round(low, 5), close));
            price = close;
            timestamp = timestamp.AddMinutes(5);
        }

        return candles;
    }

    /// <summary>
    /// Reads this repo's own historical cache format (<c>.cache/historical/*.jsonl.gz</c>): a
    /// metadata header line followed by one JSON candle per line.
    /// <para>
    /// Uses <c>closeTime</c>, not <c>openTime</c>, as the candle's stamp - the same causal
    /// convention as <c>availableAt</c> in the replay chunks. Taking openTime would date every
    /// candle one interval early and hand the feature engine that interval of hindsight.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ClassifierCandle> FromHistoricalCache(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        List<ClassifierCandle> candles = [];

        using FileStream file = File.OpenRead(path);
        using GZipStream gzip = new(file, CompressionMode.Decompress);
        using StreamReader reader = new(gzip);

        while (reader.ReadLine() is string line)
        {
            // The file is written with a BOM, and the first line is the header, not a candle.
            string trimmed = line.TrimStart('\uFEFF', ' ');
            if (trimmed.Length == 0)
                continue;

            using JsonDocument document = JsonDocument.Parse(trimmed);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("closeTime", out JsonElement closeTime))
                continue;   // header line

            candles.Add(new ClassifierCandle(
                closeTime.GetDateTimeOffset(),
                root.GetProperty("open").GetDecimal(),
                root.GetProperty("high").GetDecimal(),
                root.GetProperty("low").GetDecimal(),
                root.GetProperty("close").GetDecimal()));
        }

        return Deduplicate(candles);
    }

    /// <summary>
    /// Aggregates 1-minute candles into <paramref name="minutes"/>-minute candles.
    /// <para>
    /// Bucketed by <b>close</b> time, because that is what <c>availableAt</c> carries and what the
    /// causal contract is about: a 5m bar stamped 03:15 is the 1m bars closing 03:11 through 03:15,
    /// and is knowable at 03:15. Bucketing by open time instead would stamp that bar 03:10 and
    /// hand every feature five minutes of hindsight.
    /// </para>
    /// <para>
    /// A partial trailing bucket is dropped: an incomplete final candle has a high and low that
    /// have not finished forming, and including it would put one unknowable bar at the end of
    /// every run.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ClassifierCandle> Resample(
        IReadOnlyList<ClassifierCandle> candles,
        int minutes)
    {
        ArgumentNullException.ThrowIfNull(candles);
        if (minutes <= 1)
            return candles;

        List<ClassifierCandle> result = [];
        List<ClassifierCandle> bucket = [];
        DateTimeOffset? bucketEnd = null;

        foreach (ClassifierCandle candle in candles.OrderBy(item => item.Timestamp))
        {
            DateTimeOffset end = CeilingTo(candle.Timestamp, minutes);
            if (bucketEnd is not null && end != bucketEnd)
            {
                result.Add(Fold(bucket, bucketEnd.Value));
                bucket.Clear();
            }

            bucketEnd = end;
            bucket.Add(candle);
        }

        // The final bucket is kept only if the last source candle actually closes it.
        if (bucket.Count > 0 && bucketEnd is not null && bucket[^1].Timestamp == bucketEnd.Value)
            result.Add(Fold(bucket, bucketEnd.Value));

        return result;
    }

    private static ClassifierCandle Fold(List<ClassifierCandle> bucket, DateTimeOffset closeTime) =>
        new(closeTime,
            bucket[0].Open,
            bucket.Max(candle => candle.High),
            bucket.Min(candle => candle.Low),
            bucket[^1].Close,
            // Volume is additive across the bucket. Dropping it here silently produced six
            // constant columns downstream and made the ladder's Volume rung a no-op.
            bucket.Any(candle => candle.Volume is not null)
                ? bucket.Sum(candle => candle.Volume ?? 0m)
                : null);

    private static DateTimeOffset CeilingTo(DateTimeOffset timestamp, int minutes)
    {
        long ticks = TimeSpan.FromMinutes(minutes).Ticks;
        long rounded = (timestamp.UtcDateTime.Ticks + ticks - 1) / ticks * ticks;
        return new DateTimeOffset(rounded, TimeSpan.Zero);
    }

    private static bool TryDecimal(string value, out decimal result) =>
        decimal.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out result);

    /// <summary>
    /// Sorts and drops repeated timestamps. Replay chunks legitimately overlap at their seams, and
    /// a duplicate candle would be folded into every incremental indicator twice.
    /// </summary>
    private static IReadOnlyList<ClassifierCandle> Deduplicate(List<ClassifierCandle> candles)
    {
        return candles
            .GroupBy(candle => candle.Timestamp)
            .Select(group => group.First())
            .OrderBy(candle => candle.Timestamp)
            .ToArray();
    }
}
