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

    public decimal Calculate(int currentIndex, CircularLinkedList<CandleData>.Node candle, bool isLiveMarket = false)
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
            RSI = getRSI(candle.Data.Gain, candle.Data.Loss, isLiveMarket);
        }
        return RSI;
    }
   
    private decimal getRSI(decimal gain, decimal loss, bool isLiveMarket)
    {
        decimal RSI = 0m;
        decimal averageGain = _averageGain;
        decimal averageLoss = _averageLoss;
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
            if (isLiveMarket)
            {
                averageGain = (((_windowSize - 1 ) * averageGain) + gain) / _windowSize;
                averageLoss = (((_windowSize - 1 ) * averageLoss) + loss) / _windowSize;
                decimal RS = (averageLoss == 0) ? 100m : (averageGain / averageLoss);
                RSI = 100m - (100m / (1m + RS));
            }
            else
            {
                _averageGain = (((_windowSize - 1 ) * _averageGain) + gain) / _windowSize;
                _averageLoss = (((_windowSize - 1 ) * _averageLoss) + loss) / _windowSize;
                decimal RS = (_averageLoss == 0) ? 100m : (_averageGain / _averageLoss);
                RSI = 100m - (100m / (1m + RS));
            }
           
        }
        return Math.Round(RSI, 2);
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


