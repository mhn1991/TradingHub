using System.Text.Json;
using API;
using Brokers.Brokers;
using Utility;
using Utility.Indicators;

namespace Agent;

public class Agent
{
    private readonly Rest _rest;
    private readonly Instrument _instrument;
    private CircularLinkedList<CandleData> _candles;
    private RSI _rsi;
    private StochRSI _stochRSI;
    private BollingerBand _bollingerBand;
    private String filePath;

    public Agent()
    {
        _rest = new Rest();
        _instrument = new Instrument("BTCUSDT", "1m", "1000");
        _candles = new CircularLinkedList<CandleData>(25, () => new CandleData());
        _rsi = new RSI();
        _stochRSI = new StochRSI();
        _bollingerBand = new BollingerBand();
        filePath = "Logs/"+_instrument.BrokerName +"-"+ _instrument.CoinName+".log";
        try
        {
            // Get the directory path from the file path
            string directoryPath = Path.GetDirectoryName(filePath);
            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
                Console.WriteLine($"Created directory: {directoryPath}");
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"Error: {e.Message}");
        }
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
                   
                    if (_rsi != null)
                    {
                        candle.RSI = _rsi.calculate(index, _candles.GetCurrent());
                    }
                    
                    if (_stochRSI != null && _rsi.WindowSize + 1 <= index)
                    {
                        _stochRSI.Calculate(_candles.GetCurrent());
                    }

                    if (_bollingerBand != null && _bollingerBand.WindowSize+1 <= index)
                    {
                        _bollingerBand.Calculate(_candles.GetCurrent());
                    }
                    File.AppendAllText(filePath,candle.ToString());
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

    public async Task run()
    {
        await InitAsync();
        _candles.MovePrevious();
        while (true)
        {
            
            long currentCanleTime = _candles.GetCurrent().Data.OpenTime;
            DateTimeOffset currentCandleTime = DateTimeOffset.FromUnixTimeMilliseconds(currentCanleTime);
            long nextCandleTime = currentCandleTime.AddMinutes(1).ToUnixTimeMilliseconds();
            long currentUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // we have to update the current candle 
            if (currentUnixMs >= currentCanleTime && currentUnixMs < nextCandleTime)
            {
                Console.WriteLine("we are updating the current candle");
                Console.WriteLine($"Current candle: {currentCanleTime}");
                Console.WriteLine($"{currentCandleTime:yyyy-MM-dd HH:mm:ss}");
                Console.WriteLine($"{DateTime.UnixEpoch.AddMilliseconds(nextCandleTime):yyyy-MM-dd HH:mm:ss}");
            }
            // we have to move to the next candle 
            if (currentUnixMs >= nextCandleTime)
            {
                Console.WriteLine("we are updating the current candle");
                Console.WriteLine($"Current candle: {currentCanleTime}");
                Console.WriteLine("we move to the next candle");
                Console.WriteLine($"{currentCandleTime:yyyy-MM-dd HH:mm:ss}");
                Console.WriteLine($"{DateTime.UnixEpoch.AddMilliseconds(nextCandleTime):yyyy-MM-dd HH:mm:ss}");
                break;
            }
            await Task.Delay(500);
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