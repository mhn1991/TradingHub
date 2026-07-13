using ChartAnnotator.Collections;

namespace ChartAnnotator.Indicators;

public sealed class BollingerState
{
    private readonly int _period;
    private readonly decimal _standardDeviations;
    private readonly RingBuffer<decimal> _window;
    private decimal _sum;
    private decimal _sumSquares;

    public BollingerState(int period = 20, decimal standardDeviations = 2m)
    {
        if (period <= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(period));
        }

        if (standardDeviations <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(standardDeviations));
        }

        _period = period;
        _standardDeviations = standardDeviations;
        _window = new RingBuffer<decimal>(period);
    }

    public bool IsReady => _window.Count == _period;
    public decimal Middle { get; private set; }
    public decimal Upper { get; private set; }
    public decimal Lower { get; private set; }

    public void Update(decimal close)
    {
        bool replaced = _window.Add(close, out decimal removed);
        if (replaced)
        {
            _sum -= removed;
            _sumSquares -= removed * removed;
        }

        _sum += close;
        _sumSquares += close * close;

        int count = _window.Count;
        Middle = _sum / count;
        decimal variance = Math.Max(0m, (_sumSquares / count) - (Middle * Middle));
        decimal standardDeviation = (decimal)Math.Sqrt((double)variance);
        decimal width = standardDeviation * _standardDeviations;
        Upper = Middle + width;
        Lower = Middle - width;
    }
}
