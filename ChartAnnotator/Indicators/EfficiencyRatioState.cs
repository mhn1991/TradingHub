using ChartAnnotator.Collections;

namespace ChartAnnotator.Indicators;

/// <summary>
/// Incremental Kaufman-style Efficiency Ratio over a bounded window of closes:
/// directional movement (net displacement) divided by path movement (sum of
/// absolute bar-to-bar changes). Not related to
/// <see cref="ChartAnnotator.Models.PriceLegMetrics.EfficiencyRatio"/>, which is a
/// separate, swing-leg-scoped value computed by <c>PriceActionAnalyzer</c>.
/// </summary>
public sealed class EfficiencyRatioState
{
    private readonly int _period;
    private readonly RingBuffer<decimal> _closes;
    private readonly RingBuffer<decimal> _diffs;
    private decimal _pathSum;

    public EfficiencyRatioState(int period = 14)
    {
        if (period <= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(period));
        }

        _period = period;
        _closes = new RingBuffer<decimal>(period + 1);
        _diffs = new RingBuffer<decimal>(period);
    }

    public bool IsReady { get; private set; }
    public decimal Current { get; private set; }

    public decimal Update(decimal close)
    {
        if (_closes.Count > 0)
        {
            decimal diff = Math.Abs(close - _closes.Latest);
            bool replaced = _diffs.Add(diff, out decimal removed);
            if (replaced)
            {
                _pathSum -= removed;
            }

            _pathSum += diff;
        }

        _closes.Add(close);

        if (!IsReady)
        {
            IsReady = _diffs.Count == _period;
            if (!IsReady)
            {
                Current = 0m;
                return Current;
            }
        }

        decimal directionalMovement = Math.Abs(_closes.Latest - _closes.Oldest);
        Current = _pathSum == 0m ? 0m : Math.Clamp(directionalMovement / _pathSum, 0m, 1m);
        return Current;
    }
}
