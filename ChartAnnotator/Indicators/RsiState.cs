using ChartAnnotator.Collections;

namespace ChartAnnotator.Indicators;

public sealed class RsiState
{
    private readonly int _period;
    private readonly RingBuffer<decimal> _seedGains;
    private readonly RingBuffer<decimal> _seedLosses;
    private decimal? _previousClose;
    private decimal _averageGain;
    private decimal _averageLoss;

    public RsiState(int period = 14)
    {
        if (period <= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(period));
        }

        _period = period;
        _seedGains = new RingBuffer<decimal>(period);
        _seedLosses = new RingBuffer<decimal>(period);
    }

    public bool IsReady { get; private set; }
    public decimal Current { get; private set; }

    public void Update(decimal close)
    {
        if (_previousClose is null)
        {
            _previousClose = close;
            return;
        }

        decimal change = close - _previousClose.Value;
        _previousClose = close;
        decimal gain = Math.Max(change, 0m);
        decimal loss = Math.Max(-change, 0m);

        if (!IsReady)
        {
            _seedGains.Add(gain);
            _seedLosses.Add(loss);
            if (_seedGains.Count == _period)
            {
                _averageGain = _seedGains.Sum() / _period;
                _averageLoss = _seedLosses.Sum() / _period;
                IsReady = true;
                Current = Calculate(_averageGain, _averageLoss);
            }

            return;
        }

        _averageGain = ((_averageGain * (_period - 1)) + gain) / _period;
        _averageLoss = ((_averageLoss * (_period - 1)) + loss) / _period;
        Current = Calculate(_averageGain, _averageLoss);
    }

    private static decimal Calculate(decimal averageGain, decimal averageLoss)
    {
        if (averageLoss == 0m)
        {
            return averageGain == 0m ? 50m : 100m;
        }

        decimal relativeStrength = averageGain / averageLoss;
        return 100m - (100m / (1m + relativeStrength));
    }
}
