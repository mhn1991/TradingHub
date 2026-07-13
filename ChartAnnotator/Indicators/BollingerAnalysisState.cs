using ChartAnnotator.Collections;
using ChartAnnotator.Models;

namespace ChartAnnotator.Indicators;

/// <summary>
/// Adds normalized width, percentile and transition information to raw Bollinger bands.
/// The analysis is incremental and bounded, so it is suitable for live charts.
/// </summary>
public sealed class BollingerAnalysisState
{
    private readonly RingBuffer<decimal> _bandwidthHistory;
    private readonly int _changeLookback;
    private readonly int _minimumSamples;
    private readonly decimal _directionThresholdPercent;
    private readonly decimal _squeezePercentile;
    private readonly decimal _widePercentile;
    private bool _previousWasSqueeze;

    public BollingerAnalysisState(
        int historyPeriod = 50,
        int changeLookback = 5,
        int minimumSamples = 20,
        decimal directionThresholdPercent = 5m,
        decimal squeezePercentile = 20m,
        decimal widePercentile = 80m)
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

        if (squeezePercentile is < 0m or > 100m)
        {
            throw new ArgumentOutOfRangeException(nameof(squeezePercentile));
        }

        if (widePercentile is < 0m or > 100m || widePercentile <= squeezePercentile)
        {
            throw new ArgumentOutOfRangeException(nameof(widePercentile));
        }

        _bandwidthHistory = new RingBuffer<decimal>(historyPeriod);
        _changeLookback = changeLookback;
        _minimumSamples = minimumSamples;
        _directionThresholdPercent = directionThresholdPercent;
        _squeezePercentile = squeezePercentile;
        _widePercentile = widePercentile;
    }

    public BollingerAnalysisSnapshot Current { get; private set; } = BollingerAnalysisSnapshot.Empty;

    public BollingerAnalysisSnapshot Update(decimal close, BollingerState bands)
    {
        ArgumentNullException.ThrowIfNull(bands);
        if (!bands.IsReady || bands.Middle == 0m)
        {
            Current = BollingerAnalysisSnapshot.Empty;
            return Current;
        }

        decimal absoluteWidth = Math.Max(0m, bands.Upper - bands.Lower);
        decimal bandwidthPercent = absoluteWidth / Math.Abs(bands.Middle) * 100m;
        decimal? percentB = absoluteWidth == 0m
            ? null
            : (close - bands.Lower) / absoluteWidth * 100m;

        decimal? changePercent = null;
        VolatilityDirection direction = VolatilityDirection.Unknown;
        if (_bandwidthHistory.Count > _changeLookback)
        {
            decimal previous = _bandwidthHistory[_bandwidthHistory.Count - 1 - _changeLookback];
            if (previous > 0m)
            {
                changePercent = (bandwidthPercent - previous) / previous * 100m;
                direction = changePercent.Value > _directionThresholdPercent
                    ? VolatilityDirection.Expanding
                    : changePercent.Value < -_directionThresholdPercent
                        ? VolatilityDirection.Contracting
                        : VolatilityDirection.Stable;
            }
            else if (bandwidthPercent > 0m)
            {
                direction = VolatilityDirection.Expanding;
            }
            else
            {
                direction = VolatilityDirection.Stable;
            }
        }

        _bandwidthHistory.Add(bandwidthPercent);
        decimal percentile = PercentileRank(_bandwidthHistory, bandwidthPercent);
        bool hasContext = _bandwidthHistory.Count >= _minimumSamples;
        bool isSqueeze = hasContext && percentile <= _squeezePercentile;
        bool isWide = hasContext && percentile >= _widePercentile;
        bool squeezeReleased = _previousWasSqueeze && !isSqueeze &&
            direction == VolatilityDirection.Expanding;

        BollingerWidthRegime regime = !hasContext
            ? BollingerWidthRegime.Unknown
            : isSqueeze
                ? BollingerWidthRegime.Squeeze
                : squeezeReleased || (isWide && direction == VolatilityDirection.Expanding)
                    ? BollingerWidthRegime.Expansion
                    : isWide
                        ? BollingerWidthRegime.Wide
                        : direction == VolatilityDirection.Contracting && percentile <= 40m
                            ? BollingerWidthRegime.Narrow
                            : BollingerWidthRegime.Normal;

        _previousWasSqueeze = isSqueeze;
        Current = new BollingerAnalysisSnapshot
        {
            BandwidthPercent = bandwidthPercent,
            BandwidthChangePercent = changePercent,
            PercentB = percentB,
            WidthPercentile = percentile,
            WidthDirection = direction,
            WidthRegime = regime,
            IsSqueeze = isSqueeze,
            IsExpansion = regime == BollingerWidthRegime.Expansion,
            SqueezeReleased = squeezeReleased,
            SampleCount = _bandwidthHistory.Count
        };
        return Current;
    }

    private static decimal PercentileRank(IReadOnlyList<decimal> values, decimal current)
    {
        if (values.Count == 0)
        {
            return 0m;
        }

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
