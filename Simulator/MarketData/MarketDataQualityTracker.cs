using System.Security.Cryptography;
using System.Text;
using Brokers.Models;
using Simulator.Models;

namespace Simulator.MarketData;

/// <summary>Tracks stream hygiene while candles are consumed; never fabricates missing bars.</summary>
public sealed class MarketDataQualityTracker
{
    private readonly BarInterval _baseInterval;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private DateTimeOffset? _previousOpen;
    private DateTimeOffset? _first;
    private DateTimeOffset? _last;
    private long _count;
    private long _duplicates;
    private long _outOfOrder;
    private long _missing;
    private long _weekendGaps;
    private long _sessionGaps;
    private long _incompleteAggregates;

    public MarketDataQualityTracker(BarInterval baseInterval)
    {
        if (!baseInterval.IsValid)
            throw new ArgumentException("Base interval must be valid.", nameof(baseInterval));
        _baseInterval = baseInterval;
    }

    public long CandleCount => _count;
    public string? CurrentHashHex => _count == 0 ? null : Convert.ToHexString(_hash.GetCurrentHash()).ToLowerInvariant();

    public void Observe(Candle candle)
    {
        ArgumentNullException.ThrowIfNull(candle);
        _first ??= candle.OpenTime;
        _last = candle.OpenTime;
        _count++;

        string line =
            $"{candle.OpenTime:O}|{candle.Prices.Open}|{candle.Prices.High}|{candle.Prices.Low}|{candle.Prices.Close}|{candle.Volume?.Value ?? 0m}";
        byte[] bytes = Encoding.UTF8.GetBytes(line);
        _hash.AppendData(bytes);

        if (_previousOpen is DateTimeOffset previous)
        {
            if (candle.OpenTime < previous)
            {
                _outOfOrder++;
            }
            else if (candle.OpenTime == previous)
            {
                _duplicates++;
            }
            else
            {
                DateTimeOffset expected = _baseInterval.AddTo(previous);
                if (candle.OpenTime > expected)
                {
                    long missingSteps = EstimateMissingSteps(previous, candle.OpenTime);
                    _missing += Math.Max(0, missingSteps);
                    if (IsWeekendGap(previous, candle.OpenTime))
                        _weekendGaps++;
                    else
                        _sessionGaps++;
                }
            }
        }

        _previousOpen = candle.OpenTime;
    }

    public void RecordIncompleteAggregate() => _incompleteAggregates++;

    public MarketDataQualityReport BuildReport(string? explicitHash = null)
    {
        if (_first is null || _last is null)
        {
            throw new InvalidOperationException("No candles were observed.");
        }

        return new MarketDataQualityReport
        {
            CandleCount = _count,
            DuplicateCount = _duplicates,
            OutOfOrderCount = _outOfOrder,
            MissingIntervalCount = _missing,
            WeekendGapCount = _weekendGaps,
            SessionGapCount = _sessionGaps,
            IncompleteAggregateCount = _incompleteAggregates,
            FirstCandle = _first.Value,
            LastCandle = _last.Value,
            InputHash = explicitHash ?? Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant()
        };
    }

    private long EstimateMissingSteps(DateTimeOffset previous, DateTimeOffset current)
    {
        TimeSpan span = current - previous;
        TimeSpan step = ApproximateStep();
        if (step <= TimeSpan.Zero)
            return 0;
        long steps = (long)(span.Ticks / step.Ticks) - 1;
        return Math.Max(0, steps);
    }

    private TimeSpan ApproximateStep() => _baseInterval.Unit switch
    {
        BarUnit.Second => TimeSpan.FromSeconds(_baseInterval.Value),
        BarUnit.Minute => TimeSpan.FromMinutes(_baseInterval.Value),
        BarUnit.Hour => TimeSpan.FromHours(_baseInterval.Value),
        BarUnit.Day => TimeSpan.FromDays(_baseInterval.Value),
        BarUnit.Week => TimeSpan.FromDays(7 * _baseInterval.Value),
        _ => TimeSpan.FromMinutes(1)
    };

    private static bool IsWeekendGap(DateTimeOffset previous, DateTimeOffset current)
    {
        // Forex typically closes Friday evening UTC and reopens Sunday/Monday.
        DayOfWeek from = previous.UtcDateTime.DayOfWeek;
        DayOfWeek to = current.UtcDateTime.DayOfWeek;
        return (from is DayOfWeek.Friday or DayOfWeek.Saturday) &&
               (to is DayOfWeek.Sunday or DayOfWeek.Monday) &&
               (current - previous) >= TimeSpan.FromHours(24);
    }
}
