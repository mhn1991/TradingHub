using Brokers.Models;
using ChartAnnotator.Models;

namespace ChartAnnotator.Indicators;

/// <summary>
/// Incremental Wilder ADX/+DI/-DI state. It uses completed candles only and keeps
/// no unbounded history.
/// </summary>
public sealed class AdxState
{
    private readonly int _period;
    private Candle? _previous;
    private int _directionalSamples;
    private decimal _smoothedTrueRange;
    private decimal _smoothedPlusDm;
    private decimal _smoothedMinusDm;
    private decimal _dxSeed;
    private int _dxSamples;
    private decimal? _previousAdx;

    public AdxState(int period = 14)
    {
        if (period <= 1)
            throw new ArgumentOutOfRangeException(nameof(period));
        _period = period;
    }

    public bool IsReady { get; private set; }
    public decimal Adx { get; private set; }
    public decimal PlusDi { get; private set; }
    public decimal MinusDi { get; private set; }
    public MomentumDirection StrengthDirection { get; private set; }

    public void Update(Candle candle)
    {
        ArgumentNullException.ThrowIfNull(candle);
        if (_previous is null)
        {
            _previous = candle;
            return;
        }

        decimal upMove = candle.Prices.High - _previous.Prices.High;
        decimal downMove = _previous.Prices.Low - candle.Prices.Low;
        decimal plusDm = upMove > downMove && upMove > 0m ? upMove : 0m;
        decimal minusDm = downMove > upMove && downMove > 0m ? downMove : 0m;
        decimal trueRange = Math.Max(
            candle.Prices.High - candle.Prices.Low,
            Math.Max(
                Math.Abs(candle.Prices.High - _previous.Prices.Close),
                Math.Abs(candle.Prices.Low - _previous.Prices.Close)));

        _directionalSamples++;
        if (_directionalSamples <= _period)
        {
            _smoothedTrueRange += trueRange;
            _smoothedPlusDm += plusDm;
            _smoothedMinusDm += minusDm;
            if (_directionalSamples < _period)
            {
                _previous = candle;
                return;
            }
        }
        else
        {
            _smoothedTrueRange = _smoothedTrueRange - _smoothedTrueRange / _period + trueRange;
            _smoothedPlusDm = _smoothedPlusDm - _smoothedPlusDm / _period + plusDm;
            _smoothedMinusDm = _smoothedMinusDm - _smoothedMinusDm / _period + minusDm;
        }

        if (_smoothedTrueRange <= 0m)
        {
            PlusDi = 0m;
            MinusDi = 0m;
            _previous = candle;
            return;
        }

        PlusDi = 100m * _smoothedPlusDm / _smoothedTrueRange;
        MinusDi = 100m * _smoothedMinusDm / _smoothedTrueRange;
        decimal denominator = PlusDi + MinusDi;
        decimal dx = denominator <= 0m
            ? 0m
            : 100m * Math.Abs(PlusDi - MinusDi) / denominator;

        if (!IsReady)
        {
            _dxSeed += dx;
            _dxSamples++;
            if (_dxSamples >= _period)
            {
                Adx = _dxSeed / _period;
                IsReady = true;
                StrengthDirection = MomentumDirection.Stable;
            }
        }
        else
        {
            _previousAdx = Adx;
            Adx = (Adx * (_period - 1) + dx) / _period;
            decimal change = Adx - _previousAdx.Value;
            StrengthDirection = Math.Abs(change) < 0.05m
                ? MomentumDirection.Stable
                : change > 0m
                    ? MomentumDirection.Rising
                    : MomentumDirection.Falling;
        }

        _previous = candle;
    }

    public AdxAnalysisSnapshot Snapshot()
    {
        if (!IsReady)
            return AdxAnalysisSnapshot.Empty;

        PriceActionDirection bias = PlusDi > MinusDi
            ? PriceActionDirection.Bullish
            : MinusDi > PlusDi
                ? PriceActionDirection.Bearish
                : PriceActionDirection.Neutral;
        return new AdxAnalysisSnapshot
        {
            Adx = Adx,
            PlusDi = PlusDi,
            MinusDi = MinusDi,
            StrengthDirection = StrengthDirection,
            DirectionalBias = bias,
            IsTrendStrengthening = StrengthDirection == MomentumDirection.Rising
        };
    }
}
