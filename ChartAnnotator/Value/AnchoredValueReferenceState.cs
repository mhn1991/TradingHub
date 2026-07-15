using Brokers.Models;
using ChartAnnotator.Models;

namespace ChartAnnotator.Value;

/// <summary>
/// Maintains bounded session/week/event anchored TWAP or semantic volume-weighted
/// references. Only completed candles and already-confirmed swings/events are accepted.
/// </summary>
public sealed class AnchoredValueReferenceState
{
    private readonly int _maximumReferences;
    private readonly decimal _minimumVolumeCoveragePercent;
    private readonly TimeOnly _sessionOpenUtc;
    private readonly Dictionary<string, Accumulator> _anchors = new(StringComparer.Ordinal);

    public AnchoredValueReferenceState(
        int maximumReferences = 4,
        decimal minimumVolumeCoveragePercent = 80m,
        TimeOnly? sessionOpenUtc = null)
    {
        if (maximumReferences < 2 || minimumVolumeCoveragePercent is < 0m or > 100m)
            throw new ArgumentOutOfRangeException(nameof(maximumReferences));

        _maximumReferences = maximumReferences;
        _minimumVolumeCoveragePercent = minimumVolumeCoveragePercent;
        _sessionOpenUtc = sessionOpenUtc ?? TimeOnly.MinValue;
    }

    public IReadOnlyList<AnchoredValueReference> Update(
        Candle candle,
        IReadOnlyList<Candle> availableCandles,
        IReadOnlyList<SwingPoint> newlyConfirmedSwings,
        PriceActionSnapshot priceAction,
        decimal? atr)
    {
        ArgumentNullException.ThrowIfNull(candle);
        ArgumentNullException.ThrowIfNull(availableCandles);
        ArgumentNullException.ThrowIfNull(newlyConfirmedSwings);
        ArgumentNullException.ThrowIfNull(priceAction);
        if (!candle.IsComplete || candle.CloseTime is null)
            throw new ArgumentException("Anchored value references require a completed candle.", nameof(candle));

        DateTimeOffset sessionAnchor = ResolveSessionAnchor(candle.OpenTime);
        DateTimeOffset weekAnchor = ResolveWeekAnchor(candle.OpenTime);
        EnsurePeriodicAnchor(ValueAnchorType.SessionOpen, sessionAnchor, availableCandles);
        EnsurePeriodicAnchor(ValueAnchorType.WeekOpen, weekAnchor, availableCandles);

        foreach (SwingPoint swing in newlyConfirmedSwings
                     .Where(swing => swing.ConfirmedAt <= candle.CloseTime.Value)
                     .OrderBy(swing => swing.ConfirmedAt)
                     .ThenBy(swing => swing.PivotTime))
        {
            string id = $"swing:{swing.Type}:{swing.PivotTime:O}";
            AddHistoricalAnchor(id, ValueAnchorType.MajorSwing, swing.PivotTime, availableCandles);
        }

        foreach (PriceActionEvent structureBreak in priceAction.Events
                     .Where(item => item.ConfirmedAt <= candle.CloseTime.Value && item.Type is
                         PriceActionEventType.BullishBreakOfStructure or
                         PriceActionEventType.BearishBreakOfStructure or
                         PriceActionEventType.BullishChangeOfCharacter or
                         PriceActionEventType.BearishChangeOfCharacter)
                     .OrderBy(item => item.ConfirmedAt)
                     .ThenBy(item => item.EventId, StringComparer.Ordinal))
        {
            string id = $"structure:{structureBreak.EventId}";
            AddHistoricalAnchor(
                id,
                ValueAnchorType.StructureBreak,
                structureBreak.ConfirmedAt,
                availableCandles,
                includeCandleClosingAtAnchor: true);
        }

        // Existing anchors that were rebuilt through the current candle must not receive it twice.
        foreach (Accumulator anchor in _anchors.Values)
        {
            if (anchor.LastIncludedAt < candle.OpenTime)
                anchor.Add(candle);
        }

        TrimEventAnchors();
        return _anchors.Values
            .OrderBy(anchor => AnchorPriority(anchor.AnchorType))
            .ThenByDescending(anchor => anchor.AnchoredAt)
            .ThenBy(anchor => anchor.AnchorId, StringComparer.Ordinal)
            .Take(_maximumReferences)
            .Select(anchor => anchor.Snapshot(candle.Prices.Close, atr, _minimumVolumeCoveragePercent))
            .ToArray();
    }

    private void EnsurePeriodicAnchor(
        ValueAnchorType type,
        DateTimeOffset anchoredAt,
        IReadOnlyList<Candle> availableCandles)
    {
        string prefix = type == ValueAnchorType.SessionOpen ? "session:" : "week:";
        string id = $"{prefix}{anchoredAt:O}";
        foreach (string stale in _anchors
                     .Where(pair => pair.Value.AnchorType == type && pair.Key != id)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _anchors.Remove(stale);
        }

        AddHistoricalAnchor(id, type, anchoredAt, availableCandles);
    }

    private void AddHistoricalAnchor(
        string id,
        ValueAnchorType type,
        DateTimeOffset anchoredAt,
        IReadOnlyList<Candle> availableCandles,
        bool includeCandleClosingAtAnchor = false)
    {
        if (_anchors.ContainsKey(id))
            return;

        var accumulator = new Accumulator(id, type, anchoredAt);
        foreach (Candle historical in availableCandles
                     .Where(item => item.IsComplete &&
                         (item.OpenTime >= anchoredAt ||
                          (includeCandleClosingAtAnchor &&
                           (item.CloseTime ?? item.OpenTime) >= anchoredAt)))
                     .OrderBy(item => item.OpenTime))
        {
            accumulator.Add(historical);
        }

        if (accumulator.Count > 0)
            _anchors[id] = accumulator;
    }

    private void TrimEventAnchors()
    {
        int periodic = _anchors.Values.Count(anchor => anchor.AnchorType is
            ValueAnchorType.SessionOpen or ValueAnchorType.WeekOpen);
        int eventCapacity = Math.Max(0, _maximumReferences - periodic);
        string[] excess = _anchors.Values
            .Where(anchor => anchor.AnchorType is not (ValueAnchorType.SessionOpen or ValueAnchorType.WeekOpen))
            .OrderByDescending(anchor => anchor.AnchoredAt)
            .ThenBy(anchor => anchor.AnchorId, StringComparer.Ordinal)
            .Skip(eventCapacity)
            .Select(anchor => anchor.AnchorId)
            .ToArray();
        foreach (string id in excess)
            _anchors.Remove(id);
    }

    private DateTimeOffset ResolveSessionAnchor(DateTimeOffset time)
    {
        DateTime utcDate = time.UtcDateTime.Date;
        DateTimeOffset candidate = new(
            utcDate.Year,
            utcDate.Month,
            utcDate.Day,
            _sessionOpenUtc.Hour,
            _sessionOpenUtc.Minute,
            _sessionOpenUtc.Second,
            TimeSpan.Zero);
        return time.ToUniversalTime() < candidate ? candidate.AddDays(-1) : candidate;
    }

    private static DateTimeOffset ResolveWeekAnchor(DateTimeOffset time)
    {
        DateTimeOffset utc = time.ToUniversalTime();
        int daysSinceMonday = ((int)utc.DayOfWeek + 6) % 7;
        return new DateTimeOffset(utc.UtcDateTime.Date, TimeSpan.Zero).AddDays(-daysSinceMonday);
    }

    private static int AnchorPriority(ValueAnchorType type) => type switch
    {
        ValueAnchorType.SessionOpen => 0,
        ValueAnchorType.WeekOpen => 1,
        ValueAnchorType.MajorSwing => 2,
        ValueAnchorType.StructureBreak => 3,
        _ => 4
    };

    private sealed class Accumulator
    {
        private decimal _twapSum;
        private decimal _twapSquareSum;
        private decimal _weightedSum;
        private decimal _weightedSquareSum;
        private decimal _weight;
        private int _volumeSamples;
        private VolumeKind? _volumeKind;

        public Accumulator(string anchorId, ValueAnchorType anchorType, DateTimeOffset anchoredAt)
        {
            AnchorId = anchorId;
            AnchorType = anchorType;
            AnchoredAt = anchoredAt;
        }

        public string AnchorId { get; }
        public ValueAnchorType AnchorType { get; }
        public DateTimeOffset AnchoredAt { get; }
        public int Count { get; private set; }
        public DateTimeOffset LastIncludedAt { get; private set; } = DateTimeOffset.MinValue;

        public void Add(Candle candle)
        {
            decimal typical = (candle.Prices.High + candle.Prices.Low + candle.Prices.Close) / 3m;
            _twapSum += typical;
            _twapSquareSum += typical * typical;
            Count++;
            LastIncludedAt = candle.OpenTime;

            if (candle.Volume is not { Value: > 0m, Kind: not VolumeKind.Unknown } volume)
                return;

            // Do not mix tick activity and traded quantity inside one anchor. Falling
            // back to TWAP is more honest than silently combining incompatible units.
            if (_volumeKind is not null && _volumeKind != volume.Kind)
            {
                _volumeKind = VolumeKind.Unknown;
                _weightedSum = 0m;
                _weightedSquareSum = 0m;
                _weight = 0m;
                _volumeSamples = 0;
                return;
            }

            if (_volumeKind == VolumeKind.Unknown)
                return;

            _volumeKind ??= volume.Kind;
            _weightedSum += typical * volume.Value;
            _weightedSquareSum += typical * typical * volume.Value;
            _weight += volume.Value;
            _volumeSamples++;
        }

        public AnchoredValueReference Snapshot(
            decimal currentClose,
            decimal? atr,
            decimal minimumVolumeCoveragePercent)
        {
            decimal coverage = Count == 0 ? 0m : _volumeSamples * 100m / Count;
            bool useVolume = _weight > 0m && coverage >= minimumVolumeCoveragePercent &&
                _volumeKind is not null and not VolumeKind.Unknown;
            decimal value = useVolume ? _weightedSum / _weight : _twapSum / Count;
            decimal meanSquare = useVolume
                ? _weightedSquareSum / _weight
                : _twapSquareSum / Count;
            decimal variance = Math.Max(0m, meanSquare - value * value);
            decimal deviation = DecimalSqrt(variance);
            ValueReferenceKind kind = useVolume
                ? _volumeKind == VolumeKind.TickCount
                    ? ValueReferenceKind.BrokerTickVolumeVwap
                    : ValueReferenceKind.ExchangeVolumeVwap
                : ValueReferenceKind.AnchoredTwap;

            return new AnchoredValueReference
            {
                AnchorId = AnchorId,
                AnchorType = AnchorType,
                AnchoredAt = AnchoredAt,
                Kind = kind,
                Value = value,
                StandardDeviation = deviation,
                DistanceAtr = atr is > 0m ? (currentClose - value) / atr.Value : null,
                DataCoveragePercent = coverage
            };
        }

        private static decimal DecimalSqrt(decimal value)
        {
            if (value <= 0m)
                return 0m;
            decimal current = (decimal)Math.Sqrt((double)value);
            for (int i = 0; i < 4; i++)
                current = (current + value / current) / 2m;
            return current;
        }
    }
}
