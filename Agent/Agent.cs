using System.Text.Json;
using API;
using Brokers.Brokers;
using Utility;

namespace Agent;

public class Agent
{
    private readonly Rest _rest;
    private readonly Instrument _instrument;
    private CircularLinkedList<CandleData> _candles;

    public Agent()
    {
        _rest = new Rest();
        _instrument = new Instrument("BTCUSDT", "5m", "500");
        _candles = new CircularLinkedList<CandleData>(20, new CandleData());
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

            foreach (var item in data)
            {
                if (item is List<object> list && list.Count >= 12)
                {
                    CandleData candle = _candles.GetCurrent().Data;
                    // this part should move to the Broker 
                    // each broker should be able to convert its own response to the candle date
                    candle.OpenTime = ConvertToInt64(list[0]);
                    candle.CloseTime = ConvertToInt64(list[6]);
                    candle.High = ConvertToDecimal(list[2]);
                    candle.Low = ConvertToDecimal(list[3]);
                    candle.Close = ConvertToDecimal(list[4]);
                    candle.Volume = ConvertToDecimal(list[5]);
                    candle.QuoteAssetVolume = ConvertToDecimal(list[7]);
                    candle.NumberOfTrades = ConvertToInt32(list[8]);
                    candle.TakerBuyBaseAssetVolume = ConvertToDecimal(list[9]);
                    candle.TakerBuyQuoteAssetVolume = ConvertToDecimal(list[10]);
                    candle.Ignore = ConvertToString(list[11]);

                    _candles.MoveNext();
                }
            }
            Console.WriteLine("last data received time: "+data[data.Count-1][0]);
            Console.WriteLine("last data on the data structure: "+_candles.GetCurrent().Data.OpenTime);
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
}