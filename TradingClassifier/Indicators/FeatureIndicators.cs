using ChartAnnotator.Collections;

namespace TradingClassifier.Indicators;

/// <summary>
/// Incremental indicator states for the feature engine.
/// <para>
/// Every one of these is strictly causal: <c>Update</c> folds in candle <c>t</c> and the resulting
/// value is what was knowable at the close of <c>t</c>. That is the whole of the blueprint's
/// section 25 requirement, enforced structurally - none of these types can see a later candle
/// because none of them is ever handed one.
/// </para>
/// <para>
/// These deliberately do not reuse <c>ChartAnnotator.Indicators</c>: those are single-period,
/// wired into the annotation engine's own options, whereas section 7 needs several periods of the
/// same indicator side by side (RSI 7/14/21, CCI 14/20/50, ATR 14/20, EMA 5/10/20/50).
/// </para>
/// </summary>
public sealed class EmaState
{
    private readonly decimal _multiplier;
    private readonly int _period;
    private decimal _sum;
    private int _count;

    public EmaState(int period)
    {
        if (period <= 0)
            throw new ArgumentOutOfRangeException(nameof(period));
        _period = period;
        _multiplier = 2m / (period + 1);
    }

    public bool IsReady { get; private set; }
    public decimal Current { get; private set; }

    public void Update(decimal close)
    {
        if (!IsReady)
        {
            // Seed with the SMA of the first `period` closes, the conventional EMA warm-up. Seeding
            // with the first close instead would make early values depend on where the data window
            // happens to start.
            _sum += close;
            _count++;
            if (_count < _period)
                return;
            Current = _sum / _period;
            IsReady = true;
            return;
        }

        Current = ((close - Current) * _multiplier) + Current;
    }
}

/// <summary>Wilder's RSI. Section 7 asks for periods 7, 14 and 21.</summary>
public sealed class RsiState
{
    private readonly int _period;
    private decimal _averageGain;
    private decimal _averageLoss;
    private decimal _previousClose;
    private int _count;
    private bool _seeded;

    public RsiState(int period)
    {
        if (period <= 0)
            throw new ArgumentOutOfRangeException(nameof(period));
        _period = period;
    }

    public bool IsReady { get; private set; }
    public decimal Current { get; private set; }

    public void Update(decimal close)
    {
        if (!_seeded)
        {
            _previousClose = close;
            _seeded = true;
            return;
        }

        decimal change = close - _previousClose;
        _previousClose = close;
        decimal gain = change > 0m ? change : 0m;
        decimal loss = change < 0m ? -change : 0m;

        if (!IsReady)
        {
            _averageGain += gain;
            _averageLoss += loss;
            _count++;
            if (_count < _period)
                return;
            _averageGain /= _period;
            _averageLoss /= _period;
            IsReady = true;
        }
        else
        {
            _averageGain = ((_averageGain * (_period - 1)) + gain) / _period;
            _averageLoss = ((_averageLoss * (_period - 1)) + loss) / _period;
        }

        // An all-gains window has no loss to divide by. RSI is 100 there by definition rather than
        // undefined, and returning 50 or throwing would both misrepresent a genuine strong trend.
        Current = _averageLoss == 0m
            ? (_averageGain == 0m ? 50m : 100m)
            : 100m - (100m / (1m + (_averageGain / _averageLoss)));
    }
}

/// <summary>Wilder's ATR. Also supplies the label threshold - see section 11.</summary>
public sealed class AtrState
{
    private readonly int _period;
    private decimal _sum;
    private decimal? _previousClose;
    private int _count;

    public AtrState(int period)
    {
        if (period <= 0)
            throw new ArgumentOutOfRangeException(nameof(period));
        _period = period;
    }

    public bool IsReady { get; private set; }
    public decimal Current { get; private set; }

    public void Update(decimal high, decimal low, decimal close)
    {
        decimal trueRange = _previousClose is decimal previous
            ? Math.Max(high - low, Math.Max(Math.Abs(high - previous), Math.Abs(low - previous)))
            : high - low;
        _previousClose = close;

        if (!IsReady)
        {
            _sum += trueRange;
            _count++;
            if (_count < _period)
                return;
            Current = _sum / _period;
            IsReady = true;
            return;
        }

        Current = ((Current * (_period - 1)) + trueRange) / _period;
    }
}

/// <summary>
/// Lambert's CCI over the typical price, with the mean-absolute-deviation denominator (not the
/// standard deviation - that is a common and silent substitution that shifts the 0.015 constant's
/// meaning).
/// </summary>
public sealed class CciState
{
    private const decimal Constant = 0.015m;
    private readonly RingBuffer<decimal> _window;
    private readonly int _period;
    private decimal _sum;

    public CciState(int period)
    {
        if (period <= 1)
            throw new ArgumentOutOfRangeException(nameof(period));
        _period = period;
        _window = new RingBuffer<decimal>(period);
    }

    public bool IsReady => _window.Count == _period;
    public decimal Current { get; private set; }

    public void Update(decimal high, decimal low, decimal close)
    {
        decimal typical = (high + low + close) / 3m;
        if (_window.Add(typical, out decimal removed))
            _sum -= removed;
        _sum += typical;

        if (!IsReady)
            return;

        decimal mean = _sum / _period;
        decimal deviation = 0m;
        for (int index = 0; index < _window.Count; index++)
            deviation += Math.Abs(_window[index] - mean);
        deviation /= _period;

        // A perfectly flat window has zero deviation; CCI is 0 there, not infinite.
        Current = deviation == 0m ? 0m : (typical - mean) / (Constant * deviation);
    }
}

/// <summary>MACD line, signal line and histogram - section 7 flags the histogram as most useful.</summary>
public sealed class MacdState
{
    private readonly EmaState _fast;
    private readonly EmaState _slow;
    private readonly EmaState _signal;

    public MacdState(int fastPeriod, int slowPeriod, int signalPeriod)
    {
        if (fastPeriod >= slowPeriod)
            throw new ArgumentException("MACD fast period must be shorter than the slow period.", nameof(fastPeriod));
        _fast = new EmaState(fastPeriod);
        _slow = new EmaState(slowPeriod);
        _signal = new EmaState(signalPeriod);
    }

    public bool IsReady { get; private set; }
    public decimal Macd { get; private set; }
    public decimal Signal { get; private set; }
    public decimal Histogram { get; private set; }

    public void Update(decimal close)
    {
        _fast.Update(close);
        _slow.Update(close);
        if (!_fast.IsReady || !_slow.IsReady)
            return;

        Macd = _fast.Current - _slow.Current;
        // The signal EMA is fed the MACD line, so it only starts once the MACD line itself exists.
        _signal.Update(Macd);
        if (!_signal.IsReady)
            return;

        Signal = _signal.Current;
        Histogram = Macd - Signal;
        IsReady = true;
    }
}

/// <summary>
/// Bollinger bands over closes, population standard deviation - the standard definition, matching
/// <c>ChartAnnotator.Indicators.BollingerState</c> so features and chart agree.
/// </summary>
public sealed class BollingerBandState
{
    private readonly RingBuffer<decimal> _window;
    private readonly decimal _standardDeviations;
    private readonly int _period;
    private decimal _sum;
    private decimal _sumSquares;

    public BollingerBandState(int period, decimal standardDeviations)
    {
        if (period <= 1)
            throw new ArgumentOutOfRangeException(nameof(period));
        if (standardDeviations <= 0m)
            throw new ArgumentOutOfRangeException(nameof(standardDeviations));
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
        if (_window.Add(close, out decimal removed))
        {
            _sum -= removed;
            _sumSquares -= removed * removed;
        }

        _sum += close;
        _sumSquares += close * close;

        int count = _window.Count;
        Middle = _sum / count;
        decimal variance = Math.Max(0m, (_sumSquares / count) - (Middle * Middle));
        decimal width = (decimal)Math.Sqrt((double)variance) * _standardDeviations;
        Upper = Middle + width;
        Lower = Middle - width;
    }
}

/// <summary>
/// Highest high and lowest low over a trailing window, for the section 6 range-position features.
/// </summary>
public sealed class RollingExtremeState
{
    private readonly RingBuffer<decimal> _highs;
    private readonly RingBuffer<decimal> _lows;
    private readonly int _period;

    public RollingExtremeState(int period)
    {
        if (period <= 0)
            throw new ArgumentOutOfRangeException(nameof(period));
        _period = period;
        _highs = new RingBuffer<decimal>(period);
        _lows = new RingBuffer<decimal>(period);
    }

    public bool IsReady => _highs.Count == _period;
    public decimal HighestHigh { get; private set; }
    public decimal LowestLow { get; private set; }

    public void Update(decimal high, decimal low)
    {
        _highs.Add(high, out _);
        _lows.Add(low, out _);

        // Rescanning the window is O(period) per bar. With the section 8 periods (max 50) that is
        // far cheaper than the monotonic-deque alternative is to get right, and the feature engine
        // is not the bottleneck in a backtest.
        decimal highest = decimal.MinValue;
        decimal lowest = decimal.MaxValue;
        for (int index = 0; index < _highs.Count; index++)
        {
            if (_highs[index] > highest) highest = _highs[index];
            if (_lows[index] < lowest) lowest = _lows[index];
        }

        HighestHigh = highest;
        LowestLow = lowest;
    }
}

/// <summary>
/// Remembers a value from N updates ago, for the "change" and "slope" features in section 7
/// (rsi14_change_5, ema20_slope_5, and so on).
/// </summary>
public sealed class LaggedValue
{
    private readonly RingBuffer<decimal> _history;
    private readonly int _lag;

    public LaggedValue(int lag)
    {
        if (lag <= 0)
            throw new ArgumentOutOfRangeException(nameof(lag));
        _lag = lag;
        // lag + 1 slots: the oldest is the value `lag` updates back once the buffer is full.
        _history = new RingBuffer<decimal>(lag + 1);
    }

    public bool IsReady => _history.Count == _lag + 1;
    public decimal Previous => IsReady
        ? _history.Oldest
        : throw new InvalidOperationException("No lagged value is available yet.");

    public void Update(decimal value) => _history.Add(value, out _);
}

/// <summary>
/// Percentile rank of the newest value within a trailing window: the fraction of the window at or
/// below it, in [0, 1].
/// <para>
/// Turns an absolute reading into "how unusual is this, lately". Unlike a raw level it is directly
/// comparable across instruments and across volatility eras, which is what section 17 asks of every
/// feature - an ATR of 6.7 means nothing on its own, but "higher than 95% of the last 100 bars"
/// means the same thing on gold and on EUR/USD.
/// </para>
/// </summary>
public sealed class RollingPercentileState
{
    private readonly RingBuffer<decimal> _window;
    private readonly int _period;

    public RollingPercentileState(int period)
    {
        if (period <= 1)
            throw new ArgumentOutOfRangeException(nameof(period));
        _period = period;
        _window = new RingBuffer<decimal>(period);
    }

    public bool IsReady => _window.Count == _period;
    public decimal Current { get; private set; }

    public void Update(decimal value)
    {
        _window.Add(value, out _);
        if (!IsReady)
            return;

        int atOrBelow = 0;
        for (int index = 0; index < _window.Count; index++)
        {
            if (_window[index] <= value)
                atOrBelow++;
        }

        Current = (decimal)atOrBelow / _period;
    }
}
