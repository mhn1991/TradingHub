using System.Diagnostics;
using Brokers.Models;
using ChartAnnotator.Engine;
using ChartAnnotator.Models;
using Dashboard.Contracts;

namespace Dashboard.Live;

internal enum CandleDisposition
{
    New,
    DuplicateOrOld,
    Gap
}

internal sealed class ClosedCandleCursor(BarInterval interval)
{
    private DateTimeOffset? _lastOpenTime;

    public DateTimeOffset? LastOpenTime => _lastOpenTime;

    public CandleDisposition Classify(Candle candle)
    {
        ArgumentNullException.ThrowIfNull(candle);
        if (_lastOpenTime is null)
        {
            return CandleDisposition.New;
        }

        if (candle.OpenTime <= _lastOpenTime.Value)
        {
            return CandleDisposition.DuplicateOrOld;
        }

        DateTimeOffset expected = interval.AddTo(_lastOpenTime.Value);
        if (candle.OpenTime < expected)
        {
            throw new InvalidOperationException(
                $"The live candle at {candle.OpenTime:O} overlaps the previous interval.");
        }

        return candle.OpenTime > expected ? CandleDisposition.Gap : CandleDisposition.New;
    }

    public void Commit(Candle candle) => _lastOpenTime = candle.OpenTime;
}

internal sealed class LiveAnalysisState
{
    private readonly object _sync = new();
    private readonly InstrumentKey _instrument;
    private readonly BarInterval _interval;
    private readonly int _frameCapacity;
    private readonly ChartAnnotationEngine _annotator;
    private readonly ClosedCandleCursor _cursor;
    private readonly Func<DateTimeOffset, DateTimeOffset, bool>? _isExpectedGap;
    private readonly List<ReplayFrame> _frames = [];
    private long _sequence;
    private int _processedFrames;

    public LiveAnalysisState(
        InstrumentKey instrument,
        BarInterval interval,
        ChartAnnotationOptions annotationOptions,
        int frameCapacity,
        Func<DateTimeOffset, DateTimeOffset, bool>? isExpectedGap = null)
    {
        if (instrument.IsEmpty)
        {
            throw new ArgumentException("An instrument is required.", nameof(instrument));
        }

        if (!interval.IsValid)
        {
            throw new ArgumentException("A valid interval is required.", nameof(interval));
        }

        if (frameCapacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(frameCapacity));
        }

        ArgumentNullException.ThrowIfNull(annotationOptions);
        _instrument = instrument;
        _interval = interval;
        _frameCapacity = frameCapacity;
        _annotator = new ChartAnnotationEngine(annotationOptions);
        _cursor = new ClosedCandleCursor(interval);
        _isExpectedGap = isExpectedGap;
    }

    public long GapsDetected { get; private set; }

    public async ValueTask<ReplayFrame?> ProcessAsync(
        Candle candle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candle);
        if (!candle.IsComplete)
        {
            return null;
        }

        if (candle.Instrument != _instrument || candle.Interval != _interval)
        {
            throw new ArgumentException(
                "The live candle identity does not match the configured feed.",
                nameof(candle));
        }

        CandleDisposition disposition = _cursor.Classify(candle);
        if (disposition == CandleDisposition.DuplicateOrOld)
        {
            return null;
        }

        long started = Stopwatch.GetTimestamp();
        AnalysisSnapshot snapshot = await _annotator.ProcessAsync(
            new CandleClosedEvent(_instrument, _interval, candle, ++_sequence),
            cancellationToken);
        double elapsedMicroseconds = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
        DateTimeOffset? previousOpenTime = _cursor.LastOpenTime;
        _cursor.Commit(candle);
        if (disposition == CandleDisposition.Gap &&
            (previousOpenTime is null ||
             _isExpectedGap?.Invoke(previousOpenTime.Value, candle.OpenTime) != true))
        {
            GapsDetected++;
        }

        ReplayFrame frame = ReplayContractMapper.ToFrame(
            snapshot,
            _processedFrames++,
            elapsedMicroseconds);
        lock (_sync)
        {
            _frames.Add(frame);
            if (_frames.Count > _frameCapacity)
            {
                _frames.RemoveAt(0);
            }
        }

        return frame;
    }

    public IReadOnlyList<ReplayFrame> Snapshot()
    {
        lock (_sync)
        {
            return _frames.ToArray();
        }
    }
}
