using Brokers.Models;
using ChartAnnotator.Collections;
using ChartAnnotator.Models;

namespace ChartAnnotator.Structure;

/// <summary>
/// Confirms a pivot only after the configured right-side candles have closed.
/// This preserves the difference between PivotTime and ConfirmedAt and avoids
/// repainting an unconfirmed local high or low.
/// </summary>
public sealed class SwingDetector
{
    private readonly int _left;
    private readonly int _right;
    private readonly RingBuffer<Candle> _window;

    public SwingDetector(int left = 2, int right = 2)
    {
        if (left < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(left));
        }

        if (right < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(right));
        }

        _left = left;
        _right = right;
        _window = new RingBuffer<Candle>(left + right + 1);
    }

    public IReadOnlyList<SwingPoint> Update(Candle candle)
    {
        ArgumentNullException.ThrowIfNull(candle);
        _window.Add(candle);
        if (!_window.IsFull)
        {
            return [];
        }

        Candle candidate = _window[_left];
        bool isHigh = true;
        bool isLow = true;

        for (int index = 0; index < _window.Count; index++)
        {
            if (index == _left)
            {
                continue;
            }

            Candle other = _window[index];
            if (index < _left)
            {
                // Equality is allowed on the left and forbidden on the right. This
                // gives a flat double-top/bottom to its most recent candle rather
                // than dropping the swing entirely or emitting duplicate pivots.
                isHigh &= candidate.Prices.High >= other.Prices.High;
                isLow &= candidate.Prices.Low <= other.Prices.Low;
            }
            else
            {
                isHigh &= candidate.Prices.High > other.Prices.High;
                isLow &= candidate.Prices.Low < other.Prices.Low;
            }
        }

        DateTimeOffset confirmedAt = candle.CloseTime ?? candle.OpenTime;
        var result = new List<SwingPoint>(2);

        if (isHigh)
        {
            result.Add(new SwingPoint
            {
                PivotTime = candidate.OpenTime,
                ConfirmedAt = confirmedAt,
                Price = candidate.Prices.High,
                Type = SwingType.High,
                Strength = Math.Min(_left, _right)
            });
        }

        if (isLow)
        {
            result.Add(new SwingPoint
            {
                PivotTime = candidate.OpenTime,
                ConfirmedAt = confirmedAt,
                Price = candidate.Prices.Low,
                Type = SwingType.Low,
                Strength = Math.Min(_left, _right)
            });
        }

        return result;
    }
}
