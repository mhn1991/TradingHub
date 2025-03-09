using System.Security.Cryptography.X509Certificates;
using Brokers.Brokers;

namespace Utility.Indicators;

public class BollingerBand : Indicator
{
    public int windowSize { get; set; } = 20;
    private CircularLinkedList<CandleData>.Node _currentCandel;
    private decimal _sma { get; set; } =0m;
    private decimal _std { get; set; } = 0m;
    private int _stdDev { get; set; } = 2;
    private decimal _upperBand { get; set; } = 0m;
    private decimal _lowerBand { get; set; } = 0m;

    public decimal UpperBand => _upperBand;
    public decimal LowerBand => _lowerBand;
    public decimal MiddleBand => _sma;
    
    public void calculate(CandleData candle)
    {
        calcSMA();
        calcSTD();
        _upperBand = _sma + (_stdDev * _std);
        _lowerBand = _sma - (_stdDev * _std);
    }

    private void calcSMA()
    {
        CircularLinkedList<CandleData>.Node candle = _currentCandel;
        decimal sum = 0m;
        for (int i = 0; i < windowSize; i++)
        {
            sum += candle.Data.Close;
            candle = candle.Previous;
        }
        _sma =  sum / windowSize;
        
    }

    private void calcSTD()
    {
        CircularLinkedList<CandleData>.Node candle = _currentCandel;
        decimal std = 0m;
        decimal sumSquaredDiff = 0m;
        for (int i = 0; i < windowSize; i++)
        {
            sumSquaredDiff += (candle.Data.Close - _sma) * (candle.Data.Close - _sma);
            candle = candle.Previous;
        }
        _std = (decimal)Math.Sqrt((double)(sumSquaredDiff / windowSize));;
    }

    private void calcUpperBand()
    {
        
    }

    private void calcLowerBand()
    {
        
    }
}