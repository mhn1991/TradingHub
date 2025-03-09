using System.Security.Cryptography.X509Certificates;
using Brokers.Brokers;
using Utility.Indicators.Objects;

namespace Utility.Indicators;

public class RSI: Indicator
{
    private RSIObject _obj;
    public RSI()
    {
        _obj = makeObject();
    }
    public RSIObject makeObject()
    {
        return new RSIObject();
    }

    public RSIObject getObject()
    {
        return _obj;
    }

    public decimal calculate(int currentIndex, CircularLinkedList<CandleData>.Node candle)
    {
        decimal RSI = 0m;
        if (currentIndex - _obj._startIndex < _obj._windowSize - 1 )
        {
            decimal sigmaGain = _obj._sigmaGain + candle.Data.Gain;
            decimal SigmaLoss = _obj._sigmaLoss + candle.Data.Loss;
            if (currentIndex == 1)
            {
                _obj.updateObject(_obj._startIndex,currentIndex, candle, candle,sigmaGain, SigmaLoss);
            }
            _obj.updateObject(_obj._startIndex,currentIndex, _obj._startCandle, candle,sigmaGain, SigmaLoss);
        }

        if (currentIndex - _obj._startIndex >= _obj._windowSize - 1 )
        {
            RSI = getRSI(candle.Data.Gain, candle.Data.Loss);
        }
        return RSI;
    }

    private decimal getEMARSI(decimal gain, decimal loss)
    {
        decimal k = 2 / (_obj._windowSize + 1);
        if (_obj.RSIFirstCalc)
        {
            _obj._averageGain = (_obj._sigmaGain + gain)/ _obj._windowSize;
            _obj._averageLoss = (_obj._sigmaLoss + loss)/ _obj._windowSize;
            _obj.RSIFirstCalc = false;
        }
        else
        {
            _obj._averageGain = (gain * k) + (_obj._averageGain * (1 - k));
            _obj._averageLoss = (loss * k) + (_obj._averageLoss * (1 - k));
        }
  
        decimal RS = (_obj._averageLoss == 0) ? 100 : _obj._averageGain / _obj._averageLoss;
        decimal RSI = 100 - (100 / (1 +RS));
        return RSI;
    }
    
    private decimal getRSI(decimal gain, decimal loss)
    {
        decimal RSI = 0m;
        if (_obj.RSIFirstCalc)
        {
            _obj._averageGain = (_obj._sigmaGain + gain) / _obj._windowSize;
            _obj._averageLoss = (_obj._sigmaLoss + loss) / _obj._windowSize;
            decimal RS = _obj._averageGain / _obj._averageLoss; 
            RSI = 100m - (100m / (1m + RS));
            _obj.RSIFirstCalc = false;
        }
        else
        {
            _obj._averageGain = (((_obj._windowSize - 1 ) * _obj._averageGain) + gain) / _obj._windowSize;
            _obj._averageLoss = (((_obj._windowSize - 1 ) * _obj._averageLoss) + loss) / _obj._windowSize;
            decimal RS = (_obj._averageLoss == 0) ? 100m : (_obj._averageGain / _obj._averageLoss);
            RSI = 100m - (100m / (1m + RS));
        }
        return Math.Round(RSI, 2);
    }
    
    public decimal CalculateRSI(List<decimal> prices, int period)
    {
        if (prices.Count < period)
            //throw new ArgumentException("Not enough data points for the specified period.");
            return 0m;

        // Step 1: Calculate price differences (delta)
        List<decimal> delta = new List<decimal> { 0m }; // First delta is 0 (no previous value), 'm' for decimal literal
        for (int i = 1; i < prices.Count; i++)
        {
            delta.Add(prices[i] - prices[i - 1]);
        }

        // Step 2: Separate gains and losses
        List<decimal> gain = new List<decimal>();
        List<decimal> loss = new List<decimal>();
        foreach (decimal d in delta)
        {
            gain.Add(Math.Max(d, 0m)); // Clip lower at 0
            loss.Add(Math.Max(-d, 0m)); // Clip upper at 0, negate delta for loss
        }

        // Step 3: Calculate exponentially smoothed gain and loss
        List<decimal> smoothedGain = ExponentialMovingAverage(gain, period);
        List<decimal> smoothedLoss = ExponentialMovingAverage(loss, period);

        // Step 4: Calculate RS and RSI
        //List<decimal> rsi = new List<decimal>();
        decimal rsi = 0m;
        for (int i = 0; i < smoothedGain.Count; i++)
        {
            decimal rs = smoothedGain[i] / smoothedLoss[i];
            decimal rsiValue = 100m - (100m / (1m + rs));
            //rsi.Add(decimal.Round(rsiValue, 4));
            rsi = rsiValue;
        }

        return rsi;
    }

    private static List<decimal> ExponentialMovingAverage(List<decimal> values, int period)
    {
        List<decimal> ema = new List<decimal>();
        decimal alpha = 2m / (period + 1); // Smoothing factor, equivalent to com=period-1 in pandas

        // First value is a simple average of the first 'period' values
        if (values.Count >= period)
        {
            decimal initialAverage = values.Take(period).Average();
            ema.Add(initialAverage);
        }

        // Subsequent values use EMA formula
        for (int i = period; i < values.Count; i++)
        {
            decimal prevEma = ema.Last();
            decimal currentEma = alpha * values[i] + (1m - alpha) * prevEma;
            ema.Add(currentEma);
        }

        // Pad the beginning with NaN or repeat the first EMA to match length
        while (ema.Count < values.Count)
        {
            ema.Insert(0, decimal.MinValue); // Using MinValue as a NaN substitute since decimal has no NaN
        }

        return ema;
    }
    
}