using System.Security.Cryptography.X509Certificates;
using Brokers.Brokers;

namespace Utility.Indicators.Objects;

public class RSIObject: IndicatorObject
{
    public int _windowSize;
    public int _startIndex;
    public int _endIndex;
    public CircularLinkedList<CandleData>.Node _startCandle;
    public CircularLinkedList<CandleData>.Node _endCandle;
    public decimal _sigmaGain;
    public decimal _sigmaLoss;
    public decimal _averageGain;
    public decimal _averageLoss;
    public bool RSIFirstCalc;
    
    public int WindowSize { get { return _windowSize; } set { _windowSize = value; } }
    
    public RSIObject()
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

    public void updateObject(int startIndex,int endIndex, CircularLinkedList<CandleData>.Node startCandle,
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