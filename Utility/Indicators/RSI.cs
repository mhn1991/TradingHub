using System.Security.Cryptography.X509Certificates;
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

    public override RSIObject getObject()
    {
        return _obj;
    }

    public decimal calculate(int currentIndex, decimal gain,decimal loss)
    {
        decimal RSI = 0m;
        
        return RSI;
    }
}