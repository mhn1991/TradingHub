using System.Security.Cryptography.X509Certificates;
using Brokers.Brokers;
using Utility.Indicators.Objects;

namespace Utility.Indicators;

public class StochRSI: Indicator
{
    private StochRSIObject _obj;
    private CircularLinkedList<CandleData>.Node _currentCandel;
    
    public StochRSI()
    {
        _obj = makeObject();
    }
    public StochRSIObject makeObject()
    {
        return new StochRSIObject();
    }
    
    public decimal calculate(CircularLinkedList<CandleData>.Node candle)
    {
        decimal rsi = candle.Data.RSI;
        decimal minRSI = getMinRSI(rsi);
        decimal maxRSI = getMaxRSI(rsi);
        decimal stochRSI = 0m;
        if (_obj.numberOfRSI >= _obj.widowSize || _obj.calcStochRsi)
        {
            stochRSI = (rsi - minRSI) / (maxRSI - minRSI);
            _currentCandel = candle;
            if (!_obj.calcStochRsi)
            {
                _obj.calcStochRsi = true;
            }
        }

        if (!_obj.calcStochRsi)
        {
            _obj.numberOfRSI += 1;
        }

        return  stochRSI;
    }

    private decimal getMinRSI(decimal rsi)
    {
        if (_obj.minRSI > rsi)
        {
            _obj.minRSI = rsi;
        }
        return _obj.minRSI;
    }

    private decimal getMaxRSI(decimal rsi)
    {
        if (_obj.maxRSI < rsi)
        {
            _obj.maxRSI = rsi;
        }
        return _obj.maxRSI;
    }

    public decimal getKline()
    {
        decimal sum = 0m;
        CircularLinkedList<CandleData>.Node candle = _currentCandel;
        for (int i = 0; i < _obj.kPeriod; i++)
        {
            sum += candle.Data.StochRSI;
            candle = candle.Previous;
        }
        return sum/_obj.kPeriod;
    }

}