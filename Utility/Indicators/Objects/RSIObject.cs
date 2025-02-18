using System.Security.Cryptography.X509Certificates;

namespace Utility.Indicators.Objects;

public class RSIObject: IndicatorObject
{
    public int _windowSize;
    public int _startIndex;
    public int _endIndex;
    public decimal _startValue;
    public decimal _endGain;
    public decimal _endLoss;
    public decimal _sigmaGain;
    public decimal _sigmaLoss;
    
    public RSIObject()
    {
        _windowSize = 14;
        _startIndex = 0;
        _endIndex = 0;
        _startValue = 0m;
        _endGain = 0m;
        _endLoss = 0m;
        _sigmaGain = 0m;
        _sigmaLoss = 0m;
    }

    public void updateObject(int currentIndex, decimal currentGain, decimal CurrentLoss)
    {
        if (currentIndex - _startIndex < _windowSize)
        {
                        
        }
    }
    
    public decimal calcRSI()
    {
        decimal RSI = 0m;
        if (_endIndex - _startIndex >= _windowSize)
        {
            decimal averageGain = _sigmaGain / _windowSize;
            decimal averageLoss = _sigmaLoss / _windowSize;
            decimal RS = averageGain / averageLoss;
            RSI = 100 - (100 / (1 + RS));
            
        }
        return RSI; 
    }
}