using System.Diagnostics;
using Brokers.Models;
using ChartAnnotator.Engine;
using ChartAnnotator.MarketData;
using ChartAnnotator.Models;
using Dashboard.Contracts;

namespace BacktestRunner;

internal readonly record struct ReplayFocusWindow(DateTimeOffset From, DateTimeOffset To)
{
    public bool Contains(DateTimeOffset timestamp) => timestamp >= From && timestamp <= To;
}

internal static class ReplayFrameBuilder
{
    public static async Task<IReadOnlyList<ReplaySeries>> BuildAsync(
        InstrumentKey instrument,
        IReadOnlyList<Candle> baseCandles,
        IReadOnlyList<BarInterval> intervals,
        ChartAnnotationOptions annotationOptions,
        IReadOnlyList<ReplayFocusWindow>? focusWindows = null,
        bool fullHistory = false,
        Action<int, int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        BarInterval[] distinctIntervals = intervals.Distinct().ToArray();
        if (distinctIntervals.Length == 0)
        {
            throw new ArgumentException("At least one replay interval is required.", nameof(intervals));
        }

        var aggregator = new MultiTimeframeAggregator(
            instrument,
            distinctIntervals,
            annotationOptions.CandleCapacity,
            BaseCandleGapPolicy.ResetIncompleteBuckets);
        var annotator = new ChartAnnotationEngine(annotationOptions);
        var frames = distinctIntervals.ToDictionary(
            interval => interval,
            _ => new List<ReplayFrame>());
        BarInterval overviewInterval = distinctIntervals
            .OrderByDescending(interval => interval.AddTo(DateTimeOffset.UnixEpoch) - DateTimeOffset.UnixEpoch)
            .First();
        ReplayFocusWindow[] windows = (focusWindows ?? [])
            .OrderBy(window => window.From)
            .ToArray();

        for (int candleIndex = 0; candleIndex < baseCandles.Count; candleIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (CandleClosedEvent candleEvent in aggregator.Apply(baseCandles[candleIndex]))
            {
                long started = Stopwatch.GetTimestamp();
                AnalysisSnapshot snapshot = await annotator.ProcessAsync(candleEvent, cancellationToken)
                    .ConfigureAwait(false);
                double elapsedMicroseconds = Stopwatch.GetElapsedTime(started).TotalMicroseconds;

                bool retain = fullHistory ||
                    candleEvent.Interval == overviewInterval ||
                    windows.Any(window => window.Contains(snapshot.AvailableAt));
                if (!retain)
                {
                    continue;
                }

                List<ReplayFrame> intervalFrames = frames[candleEvent.Interval];
                intervalFrames.Add(ReplayContractMapper.ToFrame(
                    snapshot,
                    intervalFrames.Count,
                    elapsedMicroseconds));
            }

            if ((candleIndex + 1) % 25_000 == 0 || candleIndex + 1 == baseCandles.Count)
            {
                progress?.Invoke(candleIndex + 1, baseCandles.Count);
            }
        }

        return distinctIntervals
            .Where(interval => frames[interval].Count > 0)
            .Select(interval => new ReplaySeries(
                BacktestCommandOptions.FormatInterval(interval),
                checked((int)(interval.AddTo(DateTimeOffset.UnixEpoch) - DateTimeOffset.UnixEpoch).TotalSeconds),
                frames[interval]))
            .ToArray();
    }
}
