using Brokers.Brokers;

namespace Utility.Indicators;

public class AverageTrueRange : Indicator
{
    private int _windowSize;
    private bool _isFirst;
    private decimal _atr;
    public int GetWindow() => _windowSize;

    public AverageTrueRange(int window = 14)
    {
        _windowSize = window;
        _isFirst = true;
    }

    public decimal Calculate(CircularLinkedList<CandleData>.Node candle, bool isLive = false)
    {
        decimal atr = _atr;
        if (_isFirst)
        {
            decimal trSum = 0m;
            for (int i = 0; i < _windowSize; i++)
            {
                trSum += GetTR(candle);
            }
            atr = trSum / _windowSize;
            _isFirst = false;
        }
        else
        {
            decimal currentTR = GetTR(candle);
            atr = ((_atr * (_windowSize - 1)) + currentTR) / _windowSize;
        }

        if (!isLive)
        {
            _atr = atr;
        }
        return atr;
    }

    private decimal GetTR(CircularLinkedList<CandleData>.Node candle)
    {
        decimal tr = Math.Max(candle.Data.High - candle.Data.Low, Math.Max(
            Math.Abs(candle.Data.High - candle.Previous.Data.Close),
            Math.Abs(candle.Data.Low - candle.Previous.Data.Close)));
        return tr;
    }
}