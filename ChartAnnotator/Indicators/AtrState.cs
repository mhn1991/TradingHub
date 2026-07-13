using Brokers.Models;
using ChartAnnotator.Collections;

namespace ChartAnnotator.Indicators;

public sealed class AtrState
{
    private readonly int _period;
    private readonly RingBuffer<decimal> _seedValues;
    private decimal? _previousClose;

    public AtrState(int period = 14)
    {
        if (period <= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(period));
        }

        _period = period;
        _seedValues = new RingBuffer<decimal>(period);
    }

    public bool IsReady { get; private set; }
    public decimal Current { get; private set; }

    public void Update(Candle candle)
    {
        ArgumentNullException.ThrowIfNull(candle);

        decimal trueRange = _previousClose is null
            ? candle.Prices.High - candle.Prices.Low
            : Math.Max(
                candle.Prices.High - candle.Prices.Low,
                Math.Max(
                    Math.Abs(candle.Prices.High - _previousClose.Value),
                    Math.Abs(candle.Prices.Low - _previousClose.Value)));

        _previousClose = candle.Prices.Close;

        if (!IsReady)
        {
            _seedValues.Add(trueRange);
            if (_seedValues.Count == _period)
            {
                Current = _seedValues.Sum() / _period;
                IsReady = true;
            }

            return;
        }

        Current = ((Current * (_period - 1)) + trueRange) / _period;
    }
}
