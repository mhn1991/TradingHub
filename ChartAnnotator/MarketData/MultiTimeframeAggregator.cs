using Brokers.Models;
using ChartAnnotator.Collections;
using ChartAnnotator.Models;

namespace ChartAnnotator.MarketData;

public enum BaseCandleGapPolicy
{
    Throw,
    ResetIncompleteBuckets
}

/// <summary>
/// Aggregates an ordered base-candle stream into multiple aligned timeframes.
/// One instance should be owned by one instrument-processing worker.
/// </summary>
public sealed class MultiTimeframeAggregator
{
    private static readonly TimeSpan CloseTimeTolerance = TimeSpan.FromSeconds(1);
    private readonly InstrumentKey _instrument;
    private readonly Dictionary<BarInterval, AggregateState> _states;
    private readonly BaseCandleGapPolicy _gapPolicy;
    private long _sequence;
    private BarInterval? _baseInterval;
    private DateTimeOffset? _expectedNextBaseOpenTime;

    public MultiTimeframeAggregator(
        InstrumentKey instrument,
        IEnumerable<BarInterval> intervals,
        int candleCapacity = 2_000,
        BaseCandleGapPolicy gapPolicy = BaseCandleGapPolicy.Throw)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        if (candleCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(candleCapacity));
        }

        if (instrument.IsEmpty)
        {
            throw new ArgumentException("An instrument is required.", nameof(instrument));
        }

        if (!Enum.IsDefined(gapPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(gapPolicy));
        }

        _instrument = instrument;
        _gapPolicy = gapPolicy;
        BarInterval[] targetIntervals = intervals.Distinct().ToArray();
        if (targetIntervals.Any(interval => !interval.IsValid))
        {
            throw new ArgumentException("Every target interval must be valid.", nameof(intervals));
        }

        _states = targetIntervals
            .ToDictionary(
                interval => interval,
                interval => new AggregateState(instrument, interval, candleCapacity));

        if (_states.Count == 0)
        {
            throw new ArgumentException("At least one target interval is required.", nameof(intervals));
        }
    }

    public IReadOnlyCollection<BarInterval> Intervals => _states.Keys;

    /// <summary>
    /// Number of target buckets discarded because their leading member or an interior
    /// base candle was missing. Discarded buckets are never emitted as complete.
    /// </summary>
    public long IncompleteAggregateCount => _states.Values.Sum(state => state.IncompleteAggregateCount);

    public IReadOnlyList<CandleClosedEvent> Apply(Candle baseCandle)
    {
        ArgumentNullException.ThrowIfNull(baseCandle);
        if (baseCandle.Instrument != _instrument)
        {
            throw new ArgumentException("The candle belongs to another instrument.", nameof(baseCandle));
        }

        ValidateBaseCandle(baseCandle);

        if (_baseInterval is BarInterval configuredBaseInterval &&
            configuredBaseInterval != baseCandle.Interval)
        {
            throw new ArgumentException(
                $"The base interval changed from {configuredBaseInterval} to {baseCandle.Interval}.",
                nameof(baseCandle));
        }

        if (_expectedNextBaseOpenTime is DateTimeOffset expectedOpen &&
            baseCandle.OpenTime != expectedOpen)
        {
            if (baseCandle.OpenTime < expectedOpen ||
                _gapPolicy == BaseCandleGapPolicy.Throw)
            {
                throw new InvalidOperationException(
                    $"The base-candle stream is discontinuous. Expected {expectedOpen:O}, " +
                    $"but received {baseCandle.OpenTime:O}.");
            }

            // Historical broker feeds naturally omit closed-market periods such as
            // weekends. Never complete a partially formed aggregate across that gap;
            // discard it and begin cleanly at the next available session candle.
            foreach (AggregateState state in _states.Values)
            {
                state.ResetIncomplete();
            }
        }

        DateTimeOffset sourcePeriodEnd = baseCandle.Interval.AddTo(baseCandle.OpenTime);
        foreach (AggregateState state in _states.Values)
        {
            state.ValidateCoverage(baseCandle.OpenTime, sourcePeriodEnd);
        }

        _baseInterval ??= baseCandle.Interval;
        _expectedNextBaseOpenTime = sourcePeriodEnd;
        var closed = new List<CandleClosedEvent>(_states.Count);

        foreach (AggregateState state in _states.Values)
        {
            foreach (Candle completed in state.Apply(baseCandle, sourcePeriodEnd))
            {
                closed.Add(new CandleClosedEvent(
                    _instrument,
                    state.Interval,
                    completed,
                    ++_sequence));
            }
        }

        closed.Sort(static (left, right) =>
        {
            int timeComparison = Nullable.Compare(
                left.Candle.CloseTime,
                right.Candle.CloseTime);
            if (timeComparison != 0)
            {
                return timeComparison;
            }

            TimeSpan leftDuration = IntervalMath.ApproximateDuration(left.Interval);
            TimeSpan rightDuration = IntervalMath.ApproximateDuration(right.Interval);
            int durationComparison = leftDuration.CompareTo(rightDuration);
            return durationComparison != 0
                ? durationComparison
                : left.Sequence.CompareTo(right.Sequence);
        });

        return closed;
    }

    private static void ValidateBaseCandle(Candle candle)
    {
        if (!candle.Interval.IsValid)
        {
            throw new ArgumentException("The base candle has an invalid interval.", nameof(candle));
        }

        if (!candle.IsComplete)
        {
            throw new ArgumentException("Only complete base candles may be aggregated.", nameof(candle));
        }

        if (candle.CloseTime is not DateTimeOffset closeTime)
        {
            throw new ArgumentException("A base candle must include its close time.", nameof(candle));
        }

        DateTimeOffset expectedClose = candle.Interval.AddTo(candle.OpenTime);
        TimeSpan closeDifference = expectedClose - closeTime;
        if (closeTime <= candle.OpenTime ||
            closeTime > expectedClose ||
            closeDifference > CloseTimeTolerance)
        {
            throw new ArgumentException(
                $"The base candle close time must match its interval boundary {expectedClose:O}.",
                nameof(candle));
        }
    }

    public IReadOnlyList<CandleClosedEvent> Flush(bool includeIncomplete = false)
    {
        var result = new List<CandleClosedEvent>();
        foreach (AggregateState state in _states.Values)
        {
            Candle? candle = state.Flush(includeIncomplete);
            if (candle is not null)
            {
                result.Add(new CandleClosedEvent(
                    _instrument,
                    state.Interval,
                    candle,
                    ++_sequence));
            }
        }

        return result;
    }

    public IReadOnlyList<Candle> GetCandles(BarInterval interval)
    {
        if (!_states.TryGetValue(interval, out AggregateState? state))
        {
            throw new KeyNotFoundException($"Interval {interval} is not configured.");
        }

        return state.Completed.Snapshot();
    }

    private sealed class AggregateState
    {
        private readonly InstrumentKey _instrument;
        private MutableCandle? _current;
        private DateTimeOffset? _skipUntil;

        public AggregateState(InstrumentKey instrument, BarInterval interval, int capacity)
        {
            _instrument = instrument;
            Interval = interval;
            Completed = new RingBuffer<Candle>(capacity);
        }

        public BarInterval Interval { get; }
        public RingBuffer<Candle> Completed { get; }
        public long IncompleteAggregateCount { get; private set; }

        public void ValidateCoverage(DateTimeOffset sourceOpen, DateTimeOffset sourcePeriodEnd)
        {
            DateTimeOffset bucketStart = IntervalMath.BucketStart(sourceOpen, Interval);
            DateTimeOffset bucketEnd = IntervalMath.BucketEnd(bucketStart, Interval);
            if (sourcePeriodEnd > bucketEnd)
            {
                throw new InvalidOperationException(
                    $"Base interval coverage {sourceOpen:O}–{sourcePeriodEnd:O} crosses the " +
                    $"{Interval} target boundary at {bucketEnd:O}.");
            }
        }

        public IReadOnlyList<Candle> Apply(Candle source, DateTimeOffset sourcePeriodEnd)
        {
            DateTimeOffset bucketStart = IntervalMath.BucketStart(source.OpenTime, Interval);
            DateTimeOffset bucketEnd = IntervalMath.BucketEnd(bucketStart, Interval);
            var completed = new List<Candle>(2);

            if (_current is null)
            {
                if (_skipUntil is DateTimeOffset skipUntil)
                {
                    if (source.OpenTime < skipUntil)
                        return completed;
                    _skipUntil = null;
                }

                // A target candle is complete only when its first source member is
                // present. Starting halfway through a bucket would otherwise create a
                // partial OHLC candle marked complete at the target boundary.
                if (source.OpenTime != bucketStart)
                {
                    _skipUntil = bucketEnd;
                    IncompleteAggregateCount++;
                    return completed;
                }

                _current = MutableCandle.Start(_instrument, source, Interval, bucketStart, bucketEnd);
            }
            else if (_current.OpenTime != bucketStart)
            {
                completed.Add(CompleteCurrent(isComplete: true));
                _current = MutableCandle.Start(_instrument, source, Interval, bucketStart, bucketEnd);
            }
            else
            {
                _current.Update(source);
            }

            if (_current is not null && sourcePeriodEnd >= _current.CloseTime)
            {
                completed.Add(CompleteCurrent(isComplete: true));
            }

            return completed;
        }

        public void ResetIncomplete()
        {
            if (_current is null)
                return;

            _skipUntil = _current.CloseTime;
            _current = null;
            IncompleteAggregateCount++;
        }

        public Candle? Flush(bool includeIncomplete)
        {
            if (_current is null || !includeIncomplete)
            {
                return null;
            }

            return CompleteCurrent(isComplete: false);
        }

        private Candle CompleteCurrent(bool isComplete)
        {
            MutableCandle current = _current
                ?? throw new InvalidOperationException("No aggregate candle is active.");
            Candle candle = current.Build(isComplete);
            if (isComplete)
            {
                Completed.Add(candle);
            }

            _current = null;
            return candle;
        }
    }

    private sealed class MutableCandle
    {
        private readonly InstrumentKey _instrument;
        private readonly BarInterval _interval;
        private readonly decimal _open;
        private decimal _high;
        private decimal _low;
        private decimal _close;
        private decimal _volume;
        private VolumeKind _volumeKind;

        private MutableCandle(
            InstrumentKey instrument,
            BarInterval interval,
            DateTimeOffset openTime,
            DateTimeOffset closeTime,
            decimal open,
            decimal high,
            decimal low,
            decimal close,
            decimal volume,
            VolumeKind volumeKind)
        {
            _instrument = instrument;
            _interval = interval;
            OpenTime = openTime;
            CloseTime = closeTime;
            _open = open;
            _high = high;
            _low = low;
            _close = close;
            _volume = volume;
            _volumeKind = volumeKind;
        }

        public DateTimeOffset OpenTime { get; }
        public DateTimeOffset CloseTime { get; }

        public static MutableCandle Start(
            InstrumentKey instrument,
            Candle source,
            BarInterval interval,
            DateTimeOffset openTime,
            DateTimeOffset closeTime)
        {
            MarketVolume? volume = source.Volume;
            return new MutableCandle(
                instrument,
                interval,
                openTime,
                closeTime,
                source.Prices.Open,
                source.Prices.High,
                source.Prices.Low,
                source.Prices.Close,
                volume?.Value ?? 0m,
                volume?.Kind ?? VolumeKind.Unknown);
        }

        public void Update(Candle source)
        {
            _high = Math.Max(_high, source.Prices.High);
            _low = Math.Min(_low, source.Prices.Low);
            _close = source.Prices.Close;

            if (source.Volume is null)
            {
                return;
            }

            if (_volumeKind != source.Volume.Kind)
            {
                _volumeKind = VolumeKind.Unknown;
            }

            _volume += source.Volume.Value;
        }

        public Candle Build(bool isComplete) => new()
        {
            Instrument = _instrument,
            Interval = _interval,
            OpenTime = OpenTime,
            CloseTime = CloseTime,
            Prices = new Ohlc(_open, _high, _low, _close),
            Volume = _volume == 0m && _volumeKind == VolumeKind.Unknown
                ? null
                : new MarketVolume(_volume, _volumeKind),
            IsComplete = isComplete
        };
    }
}
