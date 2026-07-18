using Brokers.Models;
using ChartAnnotator.Collections;

namespace ChartAnnotator.Indicators;

/// <summary>
/// Commodity Channel Index (Lambert). Uses typical price (H+L+C)/3 over a
/// fixed window: CCI = (TP - SMA(TP)) / (0.015 * mean deviation).
/// </summary>
public sealed class CciState
{
    private const decimal LambertConstant = 0.015m;

    private readonly int _period;
    private readonly RingBuffer<decimal> _typicalPrices;

    public CciState(int period = 20)
    {
        if (period < 2)
            throw new ArgumentOutOfRangeException(nameof(period));

        _period = period;
        _typicalPrices = new RingBuffer<decimal>(period);
    }

    public int Period => _period;
    public bool IsReady => _typicalPrices.Count == _period;
    public decimal Current { get; private set; }

    public void Update(Candle candle)
    {
        ArgumentNullException.ThrowIfNull(candle);

        decimal typicalPrice = (candle.Prices.High + candle.Prices.Low + candle.Prices.Close) / 3m;
        _typicalPrices.Add(typicalPrice);

        if (!IsReady)
            return;

        decimal sum = 0m;
        for (int index = 0; index < _typicalPrices.Count; index++)
            sum += _typicalPrices[index];
        decimal average = sum / _period;

        decimal deviationSum = 0m;
        for (int index = 0; index < _typicalPrices.Count; index++)
            deviationSum += Math.Abs(_typicalPrices[index] - average);
        decimal meanDeviation = deviationSum / _period;

        decimal denominator = LambertConstant * meanDeviation;
        if (denominator == 0m)
        {
            Current = 0m;
            return;
        }

        Current = (typicalPrice - average) / denominator;
    }
}
