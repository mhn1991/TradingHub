namespace TrendStatistics.Data;

/// <summary>
/// Blueprint section 51's <c>CandleAggregator</c>: folds a finer series into the agent's timeframe.
/// </summary>
public static class CandleAggregator
{
    /// <summary>
    /// Aggregates by CLOSE time. A partial trailing bucket is dropped, because an unfinished
    /// candle's high and low are still forming and the detector must only ever see completed bars -
    /// section 7's causality requirement.
    /// </summary>
    public static IReadOnlyList<Candle> Aggregate(IEnumerable<Candle> candles, TimeSpan timeframe)
    {
        ArgumentNullException.ThrowIfNull(candles);
        if (timeframe <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeframe));

        Candle[] source = [.. candles.OrderBy(item => item.OpenTime)];
        if (source.Length == 0)
            return [];

        ValidateSource(source);
        TimeSpan? sourceSpan = InferSourceSpan(source, timeframe);
        if (sourceSpan is null)
            return [];
        if (timeframe.Ticks % sourceSpan.Value.Ticks != 0)
        {
            throw new ArgumentException(
                $"Source interval {sourceSpan} does not divide target interval {timeframe}.",
                nameof(candles));
        }

        int expectedCount = checked((int)(timeframe.Ticks / sourceSpan.Value.Ticks));
        List<Candle> result = [];
        List<Candle> bucket = [];
        DateTimeOffset? bucketStart = null;

        foreach (Candle candle in source)
        {
            DateTimeOffset start = Floor(candle.OpenTime, timeframe);
            if (bucketStart is not null && start != bucketStart)
            {
                if (IsCompleteBucket(bucket, bucketStart.Value, sourceSpan.Value, expectedCount))
                    result.Add(Fold(bucket, bucketStart.Value));
                bucket.Clear();
            }

            bucketStart = start;
            bucket.Add(candle);
        }

        if (bucket.Count > 0 && bucketStart is not null
            && IsCompleteBucket(bucket, bucketStart.Value, sourceSpan.Value, expectedCount))
            result.Add(Fold(bucket, bucketStart.Value));

        return result;
    }

    private static void ValidateSource(IReadOnlyList<Candle> source)
    {
        string symbol = source[0].Symbol;
        if (string.IsNullOrWhiteSpace(symbol))
            throw new ArgumentException("Every source candle must specify a symbol.", nameof(source));

        for (int index = 0; index < source.Count; index++)
        {
            Candle candle = source[index];
            if (!candle.IsComplete)
                throw new ArgumentException("Incomplete source candles cannot be aggregated.", nameof(source));
            if (!string.Equals(candle.Symbol, symbol, StringComparison.Ordinal))
                throw new ArgumentException("Source candles must all describe the same symbol.", nameof(source));
            if (index > 0 && candle.OpenTime == source[index - 1].OpenTime)
                throw new ArgumentException("Source candles cannot have duplicate open times.", nameof(source));
        }
    }

    private static TimeSpan? InferSourceSpan(IReadOnlyList<Candle> source, TimeSpan timeframe)
    {
        if (source.Count < 2)
            return null; // A single candle cannot prove its own interval or completeness.

        TimeSpan minimumGap = source
            .Zip(source.Skip(1), (left, right) => right.OpenTime - left.OpenTime)
            .Min();
        if (minimumGap <= TimeSpan.Zero)
            throw new ArgumentException("Source candle times must be strictly increasing.", nameof(source));

        // Already-aggregated sparse data can legitimately contain weekend gaps. If every bar is
        // target-aligned and no observed gap is shorter than the target, treat it as target data.
        if (minimumGap >= timeframe
            && source.All(candle => Floor(candle.OpenTime, timeframe) == candle.OpenTime.ToUniversalTime()))
            return timeframe;

        if (minimumGap > timeframe)
            throw new ArgumentException("The source interval is coarser than the target interval.", nameof(source));
        return minimumGap;
    }

    private static bool IsCompleteBucket(
        IReadOnlyList<Candle> bucket,
        DateTimeOffset bucketStart,
        TimeSpan sourceSpan,
        int expectedCount)
    {
        if (bucket.Count != expectedCount)
            return false;

        for (int index = 0; index < expectedCount; index++)
        {
            if (bucket[index].OpenTime.ToUniversalTime() != bucketStart + (sourceSpan * index))
                return false;
        }
        return true;
    }

    private static Candle Fold(List<Candle> bucket, DateTimeOffset openTime) => new()
    {
        Symbol = bucket[0].Symbol,
        OpenTime = openTime,
        Open = bucket[0].Open,
        High = bucket.Max(candle => candle.High),
        Low = bucket.Min(candle => candle.Low),
        Close = bucket[^1].Close,
        IsComplete = true
    };

    private static DateTimeOffset Floor(DateTimeOffset timestamp, TimeSpan timeframe)
    {
        long ticks = timeframe.Ticks;
        return new DateTimeOffset(timestamp.UtcDateTime.Ticks / ticks * ticks, TimeSpan.Zero);
    }
}
