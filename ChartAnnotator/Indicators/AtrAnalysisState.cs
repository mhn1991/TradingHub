using ChartAnnotator.Collections;
using ChartAnnotator.Models;

namespace ChartAnnotator.Indicators;

/// <summary>
/// Normalizes ATR by price and classifies whether volatility is contracting,
/// stable or expanding relative to the recent history of the same instrument.
/// </summary>
public sealed class AtrAnalysisState
{
    private readonly RingBuffer<decimal> _normalizedHistory;
    private readonly int _changeLookback;
    private readonly int _minimumSamples;
    private readonly decimal _directionThresholdPercent;

    public AtrAnalysisState(
        int historyPeriod = 50,
        int changeLookback = 5,
        int minimumSamples = 20,
        decimal directionThresholdPercent = 5m)
    {
        if (historyPeriod < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(historyPeriod));
        }

        if (changeLookback < 1 || changeLookback >= historyPeriod)
        {
            throw new ArgumentOutOfRangeException(nameof(changeLookback));
        }

        if (minimumSamples < 2 || minimumSamples > historyPeriod)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumSamples));
        }

        if (directionThresholdPercent < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(directionThresholdPercent));
        }

        _normalizedHistory = new RingBuffer<decimal>(historyPeriod);
        _changeLookback = changeLookback;
        _minimumSamples = minimumSamples;
        _directionThresholdPercent = directionThresholdPercent;
    }

    public AtrAnalysisSnapshot Current { get; private set; } = AtrAnalysisSnapshot.Empty;

    public AtrAnalysisSnapshot Update(decimal atr, decimal close)
    {
        if (atr < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(atr));
        }

        if (close <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(close));
        }

        decimal normalizedPercent = atr / close * 100m;
        decimal? changePercent = null;
        VolatilityDirection direction = VolatilityDirection.Unknown;
        if (_normalizedHistory.Count > _changeLookback)
        {
            decimal previous = _normalizedHistory[_normalizedHistory.Count - 1 - _changeLookback];
            if (previous > 0m)
            {
                changePercent = (normalizedPercent - previous) / previous * 100m;
                direction = changePercent.Value > _directionThresholdPercent
                    ? VolatilityDirection.Expanding
                    : changePercent.Value < -_directionThresholdPercent
                        ? VolatilityDirection.Contracting
                        : VolatilityDirection.Stable;
            }
        }

        _normalizedHistory.Add(normalizedPercent);
        decimal percentile = PercentileRank(_normalizedHistory, normalizedPercent);
        AtrVolatilityRegime regime = _normalizedHistory.Count < _minimumSamples
            ? AtrVolatilityRegime.Unknown
            : percentile <= 10m
                ? AtrVolatilityRegime.VeryLow
                : percentile <= 30m
                    ? AtrVolatilityRegime.Low
                    : percentile >= 90m
                        ? AtrVolatilityRegime.VeryHigh
                        : percentile >= 70m
                            ? AtrVolatilityRegime.High
                            : AtrVolatilityRegime.Normal;

        Current = new AtrAnalysisSnapshot
        {
            NormalizedPercent = normalizedPercent,
            ChangePercent = changePercent,
            Percentile = percentile,
            Direction = direction,
            Regime = regime,
            SampleCount = _normalizedHistory.Count
        };
        return Current;
    }

    private static decimal PercentileRank(IReadOnlyList<decimal> values, decimal current)
    {
        if (values.Count == 0)
        {
            return 0m;
        }

        int less = 0;
        int equal = 0;
        for (int index = 0; index < values.Count; index++)
        {
            if (values[index] < current)
            {
                less++;
            }
            else if (values[index] == current)
            {
                equal++;
            }
        }

        return (less + equal / 2m) * 100m / values.Count;
    }
}
