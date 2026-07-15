using Brokers.Models;
using ChartAnnotator.Collections;
using ChartAnnotator.Models;

namespace ChartAnnotator.Indicators;

/// <summary>
/// Rolling Donchian channel. Breakout flags and bars-since-break counters are
/// always evaluated against the channel boundary from before the current bar is
/// folded in, so a bar cannot create and then break its own boundary.
/// </summary>
public sealed class DonchianState
{
    private readonly int _period;
    private readonly RingBuffer<decimal> _highs;
    private readonly RingBuffer<decimal> _lows;
    private int _barsSinceUpperBreak = -1;
    private int _barsSinceLowerBreak = -1;

    public DonchianState(int period = 20)
    {
        if (period <= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(period));
        }

        _period = period;
        _highs = new RingBuffer<decimal>(period);
        _lows = new RingBuffer<decimal>(period);
    }

    public bool IsReady => _highs.Count == _period;
    public DonchianSnapshot Current { get; private set; } = DonchianSnapshot.Empty;

    public DonchianSnapshot Update(Candle candle, decimal? atr = null)
    {
        ArgumentNullException.ThrowIfNull(candle);

        decimal? previousUpper = IsReady ? _highs.Max() : null;
        decimal? previousLower = IsReady ? _lows.Min() : null;

        bool closedAboveUpper = false;
        bool closedBelowLower = false;

        if (previousUpper is decimal upperBoundary)
        {
            closedAboveUpper = candle.Prices.Close > upperBoundary;
            _barsSinceUpperBreak = closedAboveUpper
                ? 0
                : _barsSinceUpperBreak < 0 ? -1 : _barsSinceUpperBreak + 1;
        }

        if (previousLower is decimal lowerBoundary)
        {
            closedBelowLower = candle.Prices.Close < lowerBoundary;
            _barsSinceLowerBreak = closedBelowLower
                ? 0
                : _barsSinceLowerBreak < 0 ? -1 : _barsSinceLowerBreak + 1;
        }

        _highs.Add(candle.Prices.High);
        _lows.Add(candle.Prices.Low);

        if (!IsReady)
        {
            Current = DonchianSnapshot.Empty;
            return Current;
        }

        decimal upper = _highs.Max();
        decimal lower = _lows.Min();
        decimal middle = (upper + lower) / 2m;
        decimal width = upper - lower;
        decimal? widthAtr = atr is > 0m ? width / atr : null;

        Current = new DonchianSnapshot
        {
            Upper = upper,
            Lower = lower,
            Middle = middle,
            Width = width,
            WidthAtr = widthAtr,
            ClosedAbovePreviousUpper = closedAboveUpper,
            ClosedBelowPreviousLower = closedBelowLower,
            BarsSinceUpperBreak = _barsSinceUpperBreak,
            BarsSinceLowerBreak = _barsSinceLowerBreak
        };
        return Current;
    }
}
