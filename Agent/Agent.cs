using System.Text.Json;
using API;
using Brokers.Brokers;
using Utility;
using Utility.Indicators;
using System.IO;

namespace Agent;

public class Agent
{
    private readonly Rest _rest;
    private readonly Instrument _instrument;
    private CircularLinkedList<CandleData> _candles;
    private RSI _rsi;
    private StochRSI _stochRSI;
    private BollingerBand _bollingerBand;
    private string _filePath;

    public Agent()
    {
        _rest = new Rest();
        _instrument = new Instrument("BTCUSDT", "1m", "1000");
        _candles = new CircularLinkedList<CandleData>(25, () => new CandleData());
        _rsi = new RSI();
        _stochRSI = new StochRSI();
        _bollingerBand = new BollingerBand();
        _filePath = "Logs/" + _instrument.BrokerName + "-" + _instrument.CoinName + ".log";
        EnsureLogDirectoryExists();
        DeleteLogFileIfExists();
    }

    private void EnsureLogDirectoryExists()
    {
        try
        {
            string directoryPath = Path.GetDirectoryName(_filePath);
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
    
    private void EnsureLiveLogFileExists()
    {
        string liveLogFilePath = "Logs/" + _instrument.BrokerName + "-" + _instrument.CoinName + "-live" + ".log";
    
        if (File.Exists(liveLogFilePath))
        {
            File.Delete(liveLogFilePath);
            Console.WriteLine($"Deleted existing log file: {liveLogFilePath}");
        }
        
        // Check if live log file exists, if not, create it
        if (!File.Exists(liveLogFilePath))
        {
            // Create an empty log file if it doesn't exist
            File.Create(liveLogFilePath).Close();
            Console.WriteLine($"Created live log file: {liveLogFilePath}");
        }
    }

    private void DeleteLogFileIfExists()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
                Console.WriteLine($"Deleted existing log file: {_filePath}");
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"Error deleting log file: {e.Message}");
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

            ProcessInitialData(data);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error fetching data: {ex.Message}");
        }
    }

    private void ProcessInitialData(List<List<object>> data)
    {
        int index = 1;
        foreach (var item in data)
        {
            if (index < data.Count)
            {
                if (item is List<object> list && list.Count >= 12)
                {
                    CandleData candle = _candles.GetCurrent().Data;
                    UpdateCandleData(candle, list, index);
                    File.AppendAllText(_filePath, candle.ToString());
                    _candles.MoveNext();
                }
            }
            index += 1;
        }
    }

    private void UpdateCandleData(CandleData candle, List<object> list, int index)
    {
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

        UpdateIndicators(candle, index);
        candle.isComplete = true;
    }

    private void UpdateIndicators(CandleData candle, int index, bool isLive = false)
    {
        if (_rsi != null)
        {
            candle.RSI = _rsi.Calculate(index, _candles.GetCurrent(), isLive);
        }

        if (_stochRSI != null && _rsi.WindowSize + 1 <= index)
        {
            _stochRSI.Calculate(_candles.GetCurrent());
        }

        if (_bollingerBand != null && _bollingerBand.WindowSize + 1 <= index)
        {
            _bollingerBand.Calculate(_candles.GetCurrent());
        }
    }

    public async Task Run()
    {
        await InitAsync();
        int index = 28;
        while (true)
        {
            List<BinanceKline> data = await _rest.Get<BinanceKline>(_instrument.GetTheLastKlines("1m", 2));
            if (data.Count == 0)
            {
                Console.WriteLine("No data received.");
                return;
            }

            if(ProcessLiveData(data, index))
                break;
            await Task.Delay(500);
        }
    }

    private bool ProcessLiveData(List<BinanceKline> data, int index)
    {
        string liveLogFilePath = "Logs/" + _instrument.BrokerName + "-" + _instrument.CoinName + "-live" + ".log";

        long prevCandleTime = _candles.GetPrevious().Data.OpenTime;
        DateTimeOffset prevCandleTimeOffset = DateTimeOffset.FromUnixTimeMilliseconds(prevCandleTime);
        long currentCandleTime = prevCandleTimeOffset.AddMinutes(1).ToUnixTimeMilliseconds();
        long nextCandleTime = prevCandleTimeOffset.AddMinutes(2).ToUnixTimeMilliseconds();

        if (data[1].OpenTime >= currentCandleTime && data[1].OpenTime < nextCandleTime)
        {
            UpdateCurrentCandle(data.Last(), index, true);
            File.AppendAllText(liveLogFilePath, _candles.GetCurrent().Data.ToString());

        }

        if (data[0].OpenTime == currentCandleTime && data[1].OpenTime >= nextCandleTime)
        {
            UpdateCurrentCandle(data.First(), index, false);
            Console.WriteLine(_candles.GetCurrent().Data.ToString());
            File.AppendAllText(_filePath, _candles.GetCurrent().Data.ToString());
            _candles.MoveNext();
            return true;
        }

        return false;
    }

    private void UpdateCurrentCandle(BinanceKline kline, int index, bool isLive = false)
    {
        var currentCandle = _candles.GetCurrent().Data;
        currentCandle.OpenTime = kline.OpenTime;
        currentCandle.Close = ConvertToDecimal(kline.Close);
        currentCandle.Open = ConvertToDecimal(kline.Open);
        currentCandle.High = ConvertToDecimal(kline.High);
        currentCandle.Low = ConvertToDecimal(kline.Low);
        currentCandle.Volume = ConvertToDecimal(kline.Volume);
        currentCandle.Gain = CalculateGain(currentCandle.Close, _candles.GetPrevious().Data.Close);
        currentCandle.Loss = CalculateLoss(currentCandle.Close, _candles.GetPrevious().Data.Close);

        UpdateIndicators(currentCandle, index, isLive);
    }

    // Safe conversion functions
    private static long ConvertToInt64(object value)
    {
        return value switch
        {
            JsonElement json when json.ValueKind == JsonValueKind.Number => json.GetInt64(),
            JsonElement json when json.ValueKind == JsonValueKind.String &&
                                  long.TryParse(json.GetString(), out var result) => result,
            string str when long.TryParse(str, out var result) => result,
            _ => 0
        };
    }

    private static decimal ConvertToDecimal(object value)
    {
        return value switch
        {
            JsonElement json when json.ValueKind == JsonValueKind.Number => json.GetDecimal(),
            JsonElement json when json.ValueKind == JsonValueKind.String &&
                                  decimal.TryParse(json.GetString(), out var result) => result,
            string str when decimal.TryParse(str, out var result) => result,
            _ => 0m
        };
    }

    private static decimal CalculateGain(decimal currentPrice, decimal previousPrice)
    {
        decimal deltaP = currentPrice - previousPrice;
        return deltaP > 0 ? deltaP : 0m;
    }

    private static decimal CalculateLoss(decimal currentPrice, decimal previousPrice)
    {
        decimal deltaP = previousPrice - currentPrice;
        return deltaP > 0 ? deltaP : 0m;
    }
    
    
}
