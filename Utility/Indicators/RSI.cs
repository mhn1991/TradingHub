using Brokers.Brokers;

namespace Utility.Indicators;

public class RSI: Indicator
{
    private int _windowSize;
    private int _startIndex;
    private int _endIndex;
    private CircularLinkedList<CandleData>.Node _startCandle;
    private CircularLinkedList<CandleData>.Node _endCandle;
    private decimal _sigmaGain;
    private decimal _sigmaLoss;
    private decimal _averageGain;
    private decimal _averageLoss;
    private bool RSIFirstCalc;
    
    public int WindowSize { get { return _windowSize; } set { _windowSize = value; } }
    public RSI()
    {
        _windowSize = 14;
        _startIndex = 1;
        _endIndex = 1;
        _startCandle = new CircularLinkedList<CandleData>.Node(new CandleData());
        _endCandle = new CircularLinkedList<CandleData>.Node(new CandleData());
        _sigmaGain = 0m;
        _sigmaLoss = 0m;
        _averageGain = 0m;
        _averageLoss = 0m;
        RSIFirstCalc = true;
    }

    public decimal calculate(int currentIndex, CircularLinkedList<CandleData>.Node candle)
    {
        decimal RSI = 0m;
        if (currentIndex - _startIndex < _windowSize - 1 )
        {
            decimal sigmaGain = _sigmaGain + candle.Data.Gain;
            decimal SigmaLoss = _sigmaLoss + candle.Data.Loss;
            if (currentIndex == 1)
            {
                updateObject(_startIndex,currentIndex, candle, candle,sigmaGain, SigmaLoss);
            }
            updateObject(_startIndex,currentIndex, _startCandle, candle,sigmaGain, SigmaLoss);
        }

        if (currentIndex - _startIndex >= _windowSize - 1 )
        {
            RSI = getRSI(candle.Data.Gain, candle.Data.Loss);
        }
        return RSI;
    }

    private decimal getEMARSI(decimal gain, decimal loss)
    {
        decimal k = 2 / (_windowSize + 1);
        if (RSIFirstCalc)
        {
            _averageGain = (_sigmaGain + gain)/ _windowSize;
            _averageLoss = (_sigmaLoss + loss)/ _windowSize;
            RSIFirstCalc = false;
        }
        else
        {
            _averageGain = (gain * k) + (_averageGain * (1 - k));
            _averageLoss = (loss * k) + (_averageLoss * (1 - k));
        }
  
        decimal RS = (_averageLoss == 0) ? 100 : _averageGain / _averageLoss;
        decimal RSI = 100 - (100 / (1 +RS));
        return RSI;
    }
    
    private decimal getRSI(decimal gain, decimal loss)
    {
        decimal RSI = 0m;
        if (RSIFirstCalc)
        {
            _averageGain = (_sigmaGain + gain) / _windowSize;
            _averageLoss = (_sigmaLoss + loss) / _windowSize;
            decimal RS = _averageGain / _averageLoss; 
            RSI = 100m - (100m / (1m + RS));
            RSIFirstCalc = false;
        }
        else
        {
            _averageGain = (((_windowSize - 1 ) * _averageGain) + gain) / _windowSize;
            _averageLoss = (((_windowSize - 1 ) * _averageLoss) + loss) / _windowSize;
            decimal RS = (_averageLoss == 0) ? 100m : (_averageGain / _averageLoss);
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
    
    private void updateObject(int startIndex,int endIndex, CircularLinkedList<CandleData>.Node startCandle,
        CircularLinkedList<CandleData>.Node endCandle,decimal sigmaGain, decimal sigmaLoss)
    {
        _startIndex = startIndex;
        _endIndex = endIndex;
        _startCandle = startCandle;
        _endCandle = endCandle;
        _sigmaGain = sigmaGain;
        _sigmaLoss = sigmaLoss;
    }
    
}