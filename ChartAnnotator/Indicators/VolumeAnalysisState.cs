using Brokers.Models;
using ChartAnnotator.Collections;
using ChartAnnotator.Models;

namespace ChartAnnotator.Indicators;

/// <summary>
/// Normalizes volume against a rolling median for the same instrument/timeframe.
/// Absolute volume is deliberately not compared across feeds. A change in the
/// declared volume kind resets the baseline so tick counts and quantities are never
/// mixed in one distribution.
/// </summary>
public sealed class VolumeAnalysisState
{
    private readonly RingBuffer<decimal> _history;
    private readonly int _minimumSamples;
    private readonly decimal _lowRelativeThreshold;
    private readonly decimal _highRelativeThreshold;
    private readonly decimal _spikeRelativeThreshold;
    private VolumeKind? _activeKind;

    public VolumeAnalysisState(
        int historyPeriod = 50,
        int minimumSamples = 20,
        decimal lowRelativeThreshold = 0.70m,
        decimal highRelativeThreshold = 1.25m,
        decimal spikeRelativeThreshold = 2.0m)
    {
        if (historyPeriod < 2 ||
            minimumSamples < 2 ||
            minimumSamples > historyPeriod ||
            lowRelativeThreshold is <= 0m or >= 1m ||
            highRelativeThreshold <= 1m ||
            spikeRelativeThreshold <= highRelativeThreshold)
        {
            throw new ArgumentOutOfRangeException(nameof(historyPeriod));
        }

        _history = new RingBuffer<decimal>(historyPeriod);
        _minimumSamples = minimumSamples;
        _lowRelativeThreshold = lowRelativeThreshold;
        _highRelativeThreshold = highRelativeThreshold;
        _spikeRelativeThreshold = spikeRelativeThreshold;
    }

    public VolumeAnalysisSnapshot Current { get; private set; } = VolumeAnalysisSnapshot.Empty;

    public VolumeAnalysisSnapshot Update(MarketVolume? volume)
    {
        if (volume is null || volume.Value <= 0m)
        {
            Current = VolumeAnalysisSnapshot.Empty;
            return Current;
        }

        if (_activeKind is null)
        {
            _activeKind = volume.Kind;
        }
        else if (_activeKind != volume.Kind)
        {
            _history.Clear(clearReferences: false);
            _activeKind = volume.Kind;
        }

        decimal? baseline = _history.Count == 0
            ? null
            : Median(_history);
        decimal? relative = baseline is > 0m
            ? volume.Value / baseline.Value
            : null;

        bool hasContext = _history.Count >= _minimumSamples;
        _history.Add(volume.Value);
        decimal percentile = PercentileRank(_history, volume.Value);
        VolumeRegime regime = !hasContext || relative is null
            ? VolumeRegime.Unknown
            // Percentile alone is unstable when the recent distribution is tight:
            // a 1% increase over twenty identical bars would otherwise look like a
            // spike.  Require a meaningful median-relative move before percentile
            // can promote a reading.
            : relative >= _spikeRelativeThreshold ||
              relative >= 1.50m && percentile >= 95m
                ? VolumeRegime.Spike
                : relative >= _highRelativeThreshold ||
                  relative >= 1.10m && percentile >= 75m
                    ? VolumeRegime.High
                    : relative <= _lowRelativeThreshold * 0.70m ||
                      relative <= _lowRelativeThreshold && percentile <= 10m
                        ? VolumeRegime.VeryLow
                        : relative <= _lowRelativeThreshold ||
                          relative <= 0.90m && percentile <= 25m
                            ? VolumeRegime.Low
                            : VolumeRegime.Normal;

        Current = new VolumeAnalysisSnapshot
        {
            Value = volume.Value,
            Kind = volume.Kind,
            BaselineMedian = baseline,
            RelativeToBaseline = relative,
            Percentile = percentile,
            Regime = regime,
            SampleCount = _history.Count,
            IsReliable = volume.Kind != VolumeKind.Unknown && hasContext
        };
        return Current;
    }

    private static decimal Median(IReadOnlyList<decimal> values)
    {
        decimal[] ordered = values.Order().ToArray();
        int middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2m
            : ordered[middle];
    }

    private static decimal PercentileRank(IReadOnlyList<decimal> values, decimal current)
    {
        int less = 0;
        int equal = 0;
        for (int index = 0; index < values.Count; index++)
        {
            if (values[index] < current)
                less++;
            else if (values[index] == current)
                equal++;
        }

        return values.Count == 0
            ? 0m
            : (less + equal / 2m) * 100m / values.Count;
    }
}
