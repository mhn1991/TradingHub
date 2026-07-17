using ChartAnnotator.Collections;

namespace ChartAnnotator.Indicators;

/// <summary>
/// Simple moving average of close prices over a fixed lookback window.
/// </summary>
public sealed class SmaState
{
    private readonly int _period;
    private readonly RingBuffer<decimal> _window;
    private decimal _sum;

    public SmaState(int period)
    {
        if (period < 1)
            throw new ArgumentOutOfRangeException(nameof(period));

        _period = period;
        _window = new RingBuffer<decimal>(period);
    }

    public int Period => _period;
    public bool IsReady => _window.Count == _period;
    public decimal Current { get; private set; }

    public void Update(decimal close)
    {
        bool replaced = _window.Add(close, out decimal removed);
        if (replaced)
            _sum -= removed;

        _sum += close;
        Current = _sum / _window.Count;
    }
}
