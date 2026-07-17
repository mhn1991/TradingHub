using Brokers.Models;

namespace QuantResearchRunner.Experiments;

/// <summary>
/// Computes one master date range covering every fold/variant window in an experiment, and
/// slices a single prefetched candle series into per-window subsets. Avoids
/// <c>StreamingCandleCache</c>'s exact-from/to cache-key miss on overlapping-but-different
/// walk-forward fold windows by fetching the underlying market data exactly once per
/// experiment rather than once per fold.
/// </summary>
public static class CandlePrefetchPlanner
{
    public static (DateTimeOffset From, DateTimeOffset To) ComputeMasterRange(
        IReadOnlyList<(DateTimeOffset From, DateTimeOffset To)> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        if (windows.Count == 0)
            throw new ArgumentException("At least one window is required.", nameof(windows));

        DateTimeOffset from = windows.Min(window => window.From);
        DateTimeOffset to = windows.Max(window => window.To);
        return (from, to);
    }

    /// <summary>
    /// Returns candles whose <see cref="Candle.OpenTime"/> falls within
    /// [<paramref name="streamFrom"/>, <paramref name="to"/>). When
    /// <paramref name="streamFrom"/> is earlier than <paramref name="from"/> (warmup),
    /// those pre-evaluation bars are included so the simulator can warm indicators without
    /// trading them.
    /// </summary>
    public static IReadOnlyList<Candle> SliceForWindow(
        IReadOnlyList<Candle> masterCandles,
        DateTimeOffset from,
        DateTimeOffset to,
        DateTimeOffset? streamFrom = null)
    {
        ArgumentNullException.ThrowIfNull(masterCandles);
        if (from >= to)
            throw new ArgumentException("From must be earlier than to.", nameof(to));

        DateTimeOffset lower = streamFrom ?? from;
        if (lower > from)
            throw new ArgumentException("streamFrom must be at or before from.", nameof(streamFrom));

        return masterCandles
            .Where(candle => candle.OpenTime >= lower && candle.OpenTime < to)
            .ToArray();
    }
}
