using ChartAnnotator.Collections;

namespace ChartAnnotator.Indicators;

/// <summary>
/// Stochastic applied to the RSI series rather than price - reacts to a momentum shift several
/// bars before raw RSI would, at the cost of more noise. Fast (%K) is the smoothed raw stochastic
/// of RSI; Slow (%D) is a further smoothing of Fast, matching the conventional (period, %K, %D)
/// shape every other stochastic implementation uses.
/// </summary>
public sealed class StochRsiState
{
    private readonly int _period;
    private readonly RingBuffer<decimal> _rsiWindow;
    private readonly RingBuffer<decimal> _fastSmoothing;
    private readonly RingBuffer<decimal> _slowSmoothing;

    public StochRsiState(int period = 14, int fastSmoothing = 3, int slowSmoothing = 3)
    {
        if (period <= 1)
            throw new ArgumentOutOfRangeException(nameof(period));
        if (fastSmoothing < 1)
            throw new ArgumentOutOfRangeException(nameof(fastSmoothing));
        if (slowSmoothing < 1)
            throw new ArgumentOutOfRangeException(nameof(slowSmoothing));

        _period = period;
        _rsiWindow = new RingBuffer<decimal>(period);
        _fastSmoothing = new RingBuffer<decimal>(fastSmoothing);
        _slowSmoothing = new RingBuffer<decimal>(slowSmoothing);
    }

    public bool IsReady { get; private set; }
    /// <summary>%K - the raw stochastic-of-RSI, lightly smoothed. 0-100 scale.</summary>
    public decimal Fast { get; private set; }
    /// <summary>%D - a further smoothing of Fast. 0-100 scale.</summary>
    public decimal Slow { get; private set; }

    /// <summary>No-op when RSI itself is not yet ready (null) - mirrors every other indicator
    /// state in this namespace, which simply skips updates it cannot yet compute.</summary>
    public void Update(decimal? rsi)
    {
        if (rsi is not decimal value)
            return;

        _rsiWindow.Add(value);
        if (_rsiWindow.Count < _period)
            return;

        decimal min = _rsiWindow.Min();
        decimal max = _rsiWindow.Max();
        // A flat RSI window (max == min) has no stochastic to compute - 50 (neutral) matches
        // RsiState.Calculate's own fallback for the analogous degenerate case.
        decimal raw = max - min <= 0m ? 50m : (value - min) / (max - min) * 100m;

        _fastSmoothing.Add(raw);
        decimal fast = _fastSmoothing.Average();
        _slowSmoothing.Add(fast);

        if (_fastSmoothing.Count < _fastSmoothing.Capacity || _slowSmoothing.Count < _slowSmoothing.Capacity)
            return;

        Fast = fast;
        Slow = _slowSmoothing.Average();
        IsReady = true;
    }
}
