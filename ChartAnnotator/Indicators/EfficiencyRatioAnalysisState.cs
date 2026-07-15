using ChartAnnotator.Collections;
using ChartAnnotator.Models;

namespace ChartAnnotator.Indicators;

/// <summary>
/// Classifies the incremental Efficiency Ratio into a market-efficiency state.
/// Unlike the Atr/Bollinger analysis states, this classifies immediately from
/// deterministic absolute cut points once the raw indicator is ready, then
/// switches to percentile-calibrated cut points once enough same-instrument
/// history has accumulated.
/// </summary>
public sealed class EfficiencyRatioAnalysisState
{
    private readonly RingBuffer<decimal> _history;
    private readonly int _changeLookback;
    private readonly int _minimumSamples;
    private readonly decimal _directionThresholdPercent;
    private readonly decimal _highlyChoppyMaximum;
    private readonly decimal _choppyMaximum;
    private readonly decimal _transitionalMaximum;
    private readonly decimal _efficientMaximum;
    private readonly decimal _choppyPercentile;
    private readonly decimal _transitionalPercentile;
    private readonly decimal _efficientPercentile;
    private readonly decimal _highlyEfficientPercentile;

    public EfficiencyRatioAnalysisState(
        int historyPeriod = 50,
        int changeLookback = 5,
        int minimumSamples = 20,
        decimal directionThresholdPercent = 10m,
        decimal highlyChoppyMaximum = 0.20m,
        decimal choppyMaximum = 0.40m,
        decimal transitionalMaximum = 0.60m,
        decimal efficientMaximum = 0.80m,
        decimal choppyPercentile = 20m,
        decimal transitionalPercentile = 40m,
        decimal efficientPercentile = 60m,
        decimal highlyEfficientPercentile = 80m)
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

        if (highlyChoppyMaximum is <= 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(highlyChoppyMaximum));
        }

        if (choppyMaximum <= highlyChoppyMaximum || choppyMaximum > 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(choppyMaximum));
        }

        if (transitionalMaximum <= choppyMaximum || transitionalMaximum > 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(transitionalMaximum));
        }

        if (efficientMaximum <= transitionalMaximum || efficientMaximum > 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(efficientMaximum));
        }

        if (choppyPercentile is < 0m or > 100m)
        {
            throw new ArgumentOutOfRangeException(nameof(choppyPercentile));
        }

        if (transitionalPercentile <= choppyPercentile || transitionalPercentile > 100m)
        {
            throw new ArgumentOutOfRangeException(nameof(transitionalPercentile));
        }

        if (efficientPercentile <= transitionalPercentile || efficientPercentile > 100m)
        {
            throw new ArgumentOutOfRangeException(nameof(efficientPercentile));
        }

        if (highlyEfficientPercentile <= efficientPercentile || highlyEfficientPercentile > 100m)
        {
            throw new ArgumentOutOfRangeException(nameof(highlyEfficientPercentile));
        }

        _history = new RingBuffer<decimal>(historyPeriod);
        _changeLookback = changeLookback;
        _minimumSamples = minimumSamples;
        _directionThresholdPercent = directionThresholdPercent;
        _highlyChoppyMaximum = highlyChoppyMaximum;
        _choppyMaximum = choppyMaximum;
        _transitionalMaximum = transitionalMaximum;
        _efficientMaximum = efficientMaximum;
        _choppyPercentile = choppyPercentile;
        _transitionalPercentile = transitionalPercentile;
        _efficientPercentile = efficientPercentile;
        _highlyEfficientPercentile = highlyEfficientPercentile;
    }

    public EfficiencyAnalysisSnapshot Current { get; private set; } = EfficiencyAnalysisSnapshot.Empty;

    public EfficiencyAnalysisSnapshot Update(decimal efficiencyRatio, bool isReady)
    {
        if (!isReady)
        {
            Current = EfficiencyAnalysisSnapshot.Empty;
            return Current;
        }

        decimal? changePercent = null;
        MomentumDirection direction = MomentumDirection.Unknown;
        if (_history.Count > _changeLookback)
        {
            decimal previous = _history[_history.Count - 1 - _changeLookback];
            if (previous != 0m)
            {
                changePercent = (efficiencyRatio - previous) / previous * 100m;
                direction = changePercent.Value > _directionThresholdPercent
                    ? MomentumDirection.Rising
                    : changePercent.Value < -_directionThresholdPercent
                        ? MomentumDirection.Falling
                        : MomentumDirection.Stable;
            }
        }

        _history.Add(efficiencyRatio);
        bool hasContext = _history.Count >= _minimumSamples;
        decimal? percentile = hasContext ? PercentileRank(_history, efficiencyRatio) : null;
        MarketEfficiencyState state = hasContext
            ? ClassifyByPercentile(percentile!.Value)
            : ClassifyByAbsolute(efficiencyRatio);

        Current = new EfficiencyAnalysisSnapshot
        {
            Percentile = percentile,
            Direction = direction,
            State = state,
            SampleCount = _history.Count
        };
        return Current;
    }

    private MarketEfficiencyState ClassifyByAbsolute(decimal efficiencyRatio) =>
        efficiencyRatio <= _highlyChoppyMaximum ? MarketEfficiencyState.HighlyChoppy
        : efficiencyRatio <= _choppyMaximum ? MarketEfficiencyState.Choppy
        : efficiencyRatio <= _transitionalMaximum ? MarketEfficiencyState.Transitional
        : efficiencyRatio <= _efficientMaximum ? MarketEfficiencyState.Efficient
        : MarketEfficiencyState.HighlyEfficient;

    private MarketEfficiencyState ClassifyByPercentile(decimal percentile) =>
        percentile <= _choppyPercentile ? MarketEfficiencyState.HighlyChoppy
        : percentile <= _transitionalPercentile ? MarketEfficiencyState.Choppy
        : percentile <= _efficientPercentile ? MarketEfficiencyState.Transitional
        : percentile <= _highlyEfficientPercentile ? MarketEfficiencyState.Efficient
        : MarketEfficiencyState.HighlyEfficient;

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
