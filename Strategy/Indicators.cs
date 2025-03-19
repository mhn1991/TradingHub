using Brokers.Brokers;

namespace Strategy;

public class Indicators
{
    public SignalType AnaliseCandle(CandleData candle)
    {
        if (candle.Close >= candle.BollingerBandUpperband && candle.StochRSIK == 100m && candle.RSI > 75)
        {
            return SignalType.Sell;
        }

        if (candle.Close <= candle.BollingerBandLowerband && candle.StochRSIK == 0 && candle.RSI < 25)
        {
            return SignalType.Buy;
        }
        
        if (candle.High >= candle.BollingerBandUpperband || candle.StochRSIK > 85 || 
            candle.Low <= candle.BollingerBandLowerband || candle.StochRSIK < 15)
        {
            return SignalType.Partial;
        }
        return SignalType.Noting;
    }

    public bool isSignal(SignalType signal, CandleData candle)
    {
        return false;
    }
}