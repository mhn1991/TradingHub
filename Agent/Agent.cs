using System.Text.Json;
using API;
using Brokers.Brokers;
using Utility;
using Utility.Indicators;
using Utility.Indicators.Objects;

namespace Agent;

public class Agent
{
    private readonly Rest _rest;
    private readonly Instrument _instrument;
    private CircularLinkedList<CandleData> _candles;
    private Dictionary<IndicatorNames, Indicator> _indicators;

    public Agent()
    {
        _rest = new Rest();
        _instrument = new Instrument("BTCUSDT", "5m", "2000");
        _candles = new CircularLinkedList<CandleData>(21, () => new CandleData());
        _indicators = new Dictionary<IndicatorNames, Indicator>();
        _indicators.Add(IndicatorNames.RSI, new RSI());
        _indicators.Add(IndicatorNames.StochRSI, new StochRSI());
        _indicators.Add(IndicatorNames.BullingerBand, new BollingerBand());
    }

    public async Task InitAsync()
    {
        try
        {
            List<List<object>> data = await _rest.Get(_instrument.GetFinalUrl());

            if (data.Count == 0)
            {
                Console.WriteLine("No data received.");
                return;
            }
            var rsi = _indicators[IndicatorNames.RSI] as RSI;
            var stochRSI = _indicators[IndicatorNames.StochRSI] as StochRSI;
            var bollingerBand = _indicators[IndicatorNames.BullingerBand] as BollingerBand;
            int index = 1;
            foreach (var item in data)
            {
                if (item is List<object> list && list.Count >= 12)
                {
                    CandleData candle = _candles.GetCurrent().Data;
                    // this part should move to the Broker 
                    // each broker should be able to convert its own response to the candle date
                    candle.OpenTime = ConvertToInt64(list[0]);
                    candle.High = ConvertToDecimal(list[2]);
                    candle.Low = ConvertToDecimal(list[3]);
                    candle.Close = ConvertToDecimal(list[4]);
                    candle.Volume = ConvertToDecimal(list[5]);
                    if (index == 1)
                    {
                        candle.Gain = 0m;
                        candle.Loss = 0m;
                    }
                    else
                    {
                        candle.Gain = CalculateGain(candle.Close, _candles.GetPrevious().Data.Close);
                        candle.Loss = CalculateLoss(candle.Close, _candles.GetPrevious().Data.Close);
                    }
                   
                    if (rsi != null)
                    {
                        candle.RSI = rsi.calculate(index, _candles.GetCurrent());
                    }
                    if (stochRSI != null && rsi.getObject().WindowSize + 1 == index)
                    {
                        candle.StochRSI = stochRSI.calculate(_candles.GetCurrent());
                    }

                    if (bollingerBand != null && bollingerBand.windowSize+1 == index)
                    {
                        candle.BollingerBandLowerband = bollingerBand.LowerBand;
                        candle.BollingerBandUpperband = bollingerBand.UpperBand;
                        candle.BollingerBandMiddleband = bollingerBand.MiddleBand;
                    }

                    _candles.MoveNext();
                }

                index += 1;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error fetching data: {ex.Message}");
        }
    }
    
    // Safe conversion functions
    private static long ConvertToInt64(object value)
    {
        return value switch
        {
            JsonElement json when json.ValueKind == JsonValueKind.Number => json.GetInt64(),
            JsonElement json when json.ValueKind == JsonValueKind.String && long.TryParse(json.GetString(), out var result) => result,
            string str when long.TryParse(str, out var result) => result,
            _ => 0 // Default fallback
        };
    }

    private static int ConvertToInt32(object value)
    {
        return value switch
        {
            JsonElement json when json.ValueKind == JsonValueKind.Number => json.GetInt32(),
            JsonElement json when json.ValueKind == JsonValueKind.String && int.TryParse(json.GetString(), out var result) => result,
            string str when int.TryParse(str, out var result) => result,
            _ => 0
        };
    }

    private static decimal ConvertToDecimal(object value)
    {
        return value switch
        {
            JsonElement json when json.ValueKind == JsonValueKind.Number => json.GetDecimal(),
            JsonElement json when json.ValueKind == JsonValueKind.String && decimal.TryParse(json.GetString(), out var result) => result,
            string str when decimal.TryParse(str, out var result) => result,
            _ => 0m
        };
    }

    private static string ConvertToString(object value)
    {
        return value switch
        {
            JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString() ?? "",
            string str => str,
            _ => ""
        };
    }

    private static decimal CalculateGain(decimal currentPrice, decimal previousPrice)
    {
        decimal deltaP = currentPrice - previousPrice;
        return deltaP > 0 ? deltaP : 0m;   
    }
    
    private static decimal CalculateLoss(decimal currentPrice, decimal previousPrice)
    {
        // with this we don't need more operation 
        decimal deltaP = previousPrice - currentPrice;
        return deltaP > 0 ? deltaP : 0m;   
    }
}