using ChartAnnotator.Collections;

namespace ChartAnnotator.Regime;

public sealed record MarketRegimeCalibrationSnapshot
{
    public static MarketRegimeCalibrationSnapshot Empty { get; } = new();

    public decimal? AdxPercentile { get; init; }
    public bool IsFrozen { get; init; }
    public int SampleCount { get; init; }
    public decimal? CalibratedTrendAdxThreshold { get; init; }
    public decimal? CalibratedRangeAdxThreshold { get; init; }
}

/// <summary>
/// Owns the one calibration series the regime classifier genuinely needs that has
/// no existing calibrated source elsewhere: raw ADX. Everything else the rule
/// hierarchy consumes (ATR percentile, Bollinger bandwidth percentile, Efficiency
/// Ratio percentile) is already calibrated upstream and reused as-is.
/// </summary>
public sealed class MarketRegimeCalibration
{
    private readonly RingBuffer<decimal> _adxHistory;
    private readonly int _minimumSamples;
    private readonly decimal _trendPercentile;
    private readonly decimal _rangePercentile;
    private decimal[]? _frozenSorted;

    public MarketRegimeCalibration(
        int historyPeriod,
        int minimumSamples,
        decimal trendPercentile,
        decimal rangePercentile)
    {
        if (historyPeriod < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(historyPeriod));
        }

        if (minimumSamples < 2 || minimumSamples > historyPeriod)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumSamples));
        }

        _adxHistory = new RingBuffer<decimal>(historyPeriod);
        _minimumSamples = minimumSamples;
        _trendPercentile = trendPercentile;
        _rangePercentile = rangePercentile;
    }

    public bool IsFrozen { get; private set; }
    public int SampleCount => _adxHistory.Count;

    public void FreezeCalibration(DateTimeOffset frozenAt)
    {
        if (IsFrozen)
        {
            return;
        }

        IsFrozen = true;
        _frozenSorted = SortedSnapshot();
    }

    public MarketRegimeCalibrationSnapshot Observe(decimal adx)
    {
        if (!IsFrozen)
        {
            _adxHistory.Add(adx);
        }

        decimal[] sorted = _frozenSorted ?? SortedSnapshot();
        bool hasContext = sorted.Length >= _minimumSamples;

        return new MarketRegimeCalibrationSnapshot
        {
            AdxPercentile = hasContext ? PercentileRank(sorted, adx) : null,
            IsFrozen = IsFrozen,
            SampleCount = sorted.Length,
            CalibratedTrendAdxThreshold = hasContext ? ValueAtPercentile(sorted, _trendPercentile) : null,
            CalibratedRangeAdxThreshold = hasContext ? ValueAtPercentile(sorted, _rangePercentile) : null
        };
    }

    private decimal[] SortedSnapshot()
    {
        decimal[] snapshot = _adxHistory.Snapshot();
        Array.Sort(snapshot);
        return snapshot;
    }

    private static decimal PercentileRank(decimal[] sortedValues, decimal current)
    {
        if (sortedValues.Length == 0)
        {
            return 0m;
        }

        int less = 0;
        int equal = 0;
        foreach (decimal value in sortedValues)
        {
            if (value < current)
            {
                less++;
            }
            else if (value == current)
            {
                equal++;
            }
        }

        return (less + equal / 2m) * 100m / sortedValues.Length;
    }

    private static decimal ValueAtPercentile(decimal[] sortedValues, decimal percentile)
    {
        if (sortedValues.Length == 0)
        {
            return 0m;
        }

        if (sortedValues.Length == 1)
        {
            return sortedValues[0];
        }

        decimal rank = percentile / 100m * (sortedValues.Length - 1);
        int lowerIndex = (int)Math.Floor(rank);
        int upperIndex = (int)Math.Ceiling(rank);
        if (lowerIndex == upperIndex)
        {
            return sortedValues[lowerIndex];
        }

        decimal fraction = rank - lowerIndex;
        return sortedValues[lowerIndex] + (sortedValues[upperIndex] - sortedValues[lowerIndex]) * fraction;
    }
}
