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
    private readonly double _gapToleranceFraction;
    private long _sequence;
    private BarInterval? _baseInterval;
    private DateTimeOffset? _expectedNextBaseOpenTime;

    /// <summary>
    /// Aggregates an ordered base-candle stream into every requested timeframe.
    /// </summary>
    /// <param name="instrument">The instrument every base candle must belong to.</param>
    /// <param name="intervals">Target timeframes to aggregate into.</param>
    /// <param name="candleCapacity">Completed candles retained per timeframe.</param>
    /// <param name="gapPolicy">Whether a discontinuous stream throws or resets buckets.</param>
    /// <param name="gapToleranceFraction">
    /// How much of a bucket a stream gap may consume before that bucket is discarded, as a fraction
    /// of the bucket's own length. Zero - the default - discards every incomplete bucket on any gap,
    /// which is the long-standing behaviour.
    /// <para>
    /// The default cannot express markets that close every day. A one-hour session break is 100% of
    /// an hourly bucket but only 4% of a daily one, yet zero tolerance discards both. Since gold's
    /// stream contains a maintenance break in every 24-hour span, a daily bucket always spans one and
    /// so can NEVER complete: a run configured with `1d` produced 1m/5m/15m/1h/4h closes and not a
    /// single daily one, and any agent requiring daily analysis silently observed forever. The same
    /// effect already costs 4h buckets about a tenth of their closes.
    /// </para>
    /// </param>
    public MultiTimeframeAggregator(
        InstrumentKey instrument,
        IEnumerable<BarInterval> intervals,
        int candleCapacity = 2_000,
        BaseCandleGapPolicy gapPolicy = BaseCandleGapPolicy.Throw,
        double gapToleranceFraction = 0.0)
    {
        if (gapToleranceFraction is < 0.0 or >= 1.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(gapToleranceFraction), "Tolerance must be within [0, 1).");
        }

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
        _gapToleranceFraction = gapToleranceFraction;
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

            // Historical broker feeds naturally omit closed-market periods such as weekends. A
            // partially formed aggregate is discarded rather than completed across that gap - but
            // only where the gap is large enough, relative to that bucket, to have actually damaged
            // it. Judging every interval by the same absolute gap discards a daily bucket for a
            // one-hour session break that removed 4% of it.
            TimeSpan gap = baseCandle.OpenTime - expectedOpen;
            foreach (AggregateState state in _states.Values)
            {
                if (_gapToleranceFraction <= 0.0 || !state.Tolerates(gap, _gapToleranceFraction))
                {
                    state.ResetIncomplete();
                }
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

        /// <summary>
        /// Whether a stream gap of <paramref name="gap"/> is small enough, relative to this bucket's
        /// own length, to leave the bucket usable. A bucket with nothing in it yet has nothing to
        /// damage, so it always survives.
        /// </summary>
        public bool Tolerates(TimeSpan gap, double fraction)
        {
            if (_current is null)
            {
                return true;
            }

            double bucketSeconds = BarIntervalParser.ApproximateSeconds(Interval);
            return bucketSeconds > 0 && gap.TotalSeconds <= bucketSeconds * fraction;
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
