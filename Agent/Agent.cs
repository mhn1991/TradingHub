using System.Text.Json;
using API;
using Brokers.Brokers;
using Utility;
using Utility.Indicators;
using System.IO;
using System.Text.RegularExpressions;
using DBManager;
using DBManager.Models;
using DBManager.Repositories;
using DBManager.Services;
using Strategy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using TradeManager;
using Utility.Services;

namespace Agent;

public class Agent
{
    private readonly Rest _rest;
    private readonly Instrument _instrument;
    private readonly Dictionary<string, string> _timeFrames;
    private Dictionary<string, CircularLinkedList<CandleData>> _charts;
    private string _filePath;
    private Dictionary<string, List<Indicator>> _indicators;
    private List<IStrategy> _strategies;
    private readonly TradeManagerService _tradeManagerService;
    private Dictionary<string, long> _signalsTimeFrames;
    private List<string> _tradeableTimeFrames;
    private CircularLinkedList<CandleData> _HighsLows;
    
    public Agent(TradeManagerService tradeManagerService)
    {
        _HighsLows = new CircularLinkedList<CandleData>(100, () => new CandleData());
        _signalsTimeFrames = new Dictionary<string, long>();
        _tradeManagerService = tradeManagerService;
        _rest = new Rest();
        _instrument = new Instrument("BTCUSDT", "1m", "1000");
        _filePath = "Logs/" + _instrument.BrokerName + "-" + _instrument.CoinName + ".log";
        EnsureLogDirectoryExists();
        DeleteLogFileIfExists();

        EnsureLiveLogFileExists();
        _tradeableTimeFrames = new List<string>() { "1h", "15m", "5m" };
        // this is just for the binance we have to get it from the config later
        _timeFrames = new Dictionary<string, string>()
        {
            {
                "1h", "15"
            },
            {
                "15m", "5m"
            },
            {
                "5m", "1m"
            },
            {
                "1m", "1s"
            }
        };
        _charts = new Dictionary<string, CircularLinkedList<CandleData>>()
        {
            {
                "1h", new CircularLinkedList<CandleData>(25, () => new CandleData())
            },
            {
                "15m", new CircularLinkedList<CandleData>(25, () => new CandleData())
            },
            {
                "5m", new CircularLinkedList<CandleData>(25, () => new CandleData())
            },
            {
                "1m", new CircularLinkedList<CandleData>(25, () => new CandleData())
            },
            {
                "1s", new CircularLinkedList<CandleData>(25, () => new CandleData())
            }
        };
        _indicators = new Dictionary<string, List<Indicator>>()
        {
            {
                "1h", new List<Indicator>()
                {
                    new RSI(),
                    new StochRSI(),
                    new BollingerBand(),
                    new AverageTrueRange(),
                }
            },
            {
                "15m", new List<Indicator>()
                {
                    new RSI(),
                    new StochRSI(),
                    new BollingerBand(),
                    new AverageTrueRange(),
                }
            },
            {
                "5m", new List<Indicator>()
                {
                    new RSI(),
                    new StochRSI(),
                    new BollingerBand(),
                    new AverageTrueRange(),
                }
            },
            {
                "1m", new List<Indicator>()
                {
                    new RSI(),
                    new StochRSI(),
                    new BollingerBand(),
                    new AverageTrueRange(),
                }
            },
            {
                "1s", new List<Indicator>()
                {
                    new RSI(),
                    new StochRSI(),
                    new BollingerBand(),
                    new AverageTrueRange(),
                }
            }
        };
        _strategies = new List<IStrategy>()
        {
            new Indicators(),
        };
        _HighsLows = new CircularLinkedList<CandleData>(100, () => new CandleData());
    }

    public void checkDB()
    {
        var serviceProvider = new ServiceCollection()
            .AddDbContext<ApplicationDbContext>(options =>
                options.UseNpgsql(
                    "Server=localhost;Port=54320;User Id=db;Password=mysecretpassword;Database=tradinghub;")) // Replace with your actual connection string
            .AddScoped<DbConnectionChecker>() // Ensure DbConnectionChecker is registered here
            .AddScoped<ITradeRepository, TradeRepository>()
            .AddScoped<IBrokerRepository, BrokerRepository>()
            .BuildServiceProvider();

        // Resolve the DbConnectionChecker from DbManager
        var dbConnectionChecker = serviceProvider.GetService<DbConnectionChecker>();

        if (dbConnectionChecker.CheckConnection())
        {
            Console.WriteLine("Database connection is successful!");
        }
        else
        {
            Console.WriteLine("Failed to connect to the database.");
        }

        // Optionally, you can use the repositories after this
        var tradeRepo = serviceProvider.GetService<ITradeRepository>();
        // Use tradeRepo to interact with the DB...
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
            foreach (string timeFrame in _timeFrames.Keys)
            {
                List<List<object>> data = await _rest.Get(_instrument.GetTheLastKlines(timeFrame, 1000));
                if (data.Count == 0)
                {
                    Console.WriteLine("No data received.");
                    return;
                }

                ProcessInitialData(data, timeFrame);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error fetching data: {ex.Message}");
        }
    }

    private async Task Strategy(CandleData candle, string timeFrame)
    {
        // we have to check if the candle's time is bigger than any of the records that record
        // should get removed
        _signalsTimeFrames = _signalsTimeFrames
            .Where(pair => candle.OpenTime < pair.Value) // Keep only items that don't match the condition
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        File.AppendAllText("Logs/Strategy.log", "==============================\n");
        File.AppendAllText("Logs/Strategy.log",_signalsTimeFrames.LastOrDefault().Value+"\n");
        File.AppendAllText("Logs/Strategy.log", "timeframe is: "+ timeFrame+"\n");
        File.AppendAllText("Logs/Strategy.log", "==============================\n");
        foreach (var strategy in _strategies)
        {
            if (strategy is Indicators indicators)
            {
                if (_tradeableTimeFrames.Contains(timeFrame) || _signalsTimeFrames.ContainsKey(timeFrame))
                {
                    File.AppendAllText("Logs/Strategy.log", "we are inside first if\n");
                    File.AppendAllText("Logs/Strategy.log", _tradeableTimeFrames.Contains(timeFrame)+"\n");
                    File.AppendAllText("Logs/Strategy.log", _signalsTimeFrames.ContainsKey(timeFrame)+"\n");
                    if ((!_signalsTimeFrames.Any() && _tradeableTimeFrames.Contains(timeFrame)) 
                        || _signalsTimeFrames.Last().Key.Equals(timeFrame))
                    {
                        File.AppendAllText("Logs/Strategy.log", "we are inside second if\n");
                        File.AppendAllText("Logs/Strategy.log", (!_signalsTimeFrames.Any() && _tradeableTimeFrames.Contains(timeFrame))+"\n");
                        if (_signalsTimeFrames.Any())  
                        {
                            File.AppendAllText("Logs/Strategy.log", _signalsTimeFrames.LastOrDefault().Key.Equals(timeFrame)+"\n");
                        }
                        
                        SignalType signal = indicators.AnaliseCandle(candle);
                        // if the signal was partial we have to keep checking until the next candle of
                        // the bigger timeframe
                        if (signal == SignalType.Partial)
                        {
                            _signalsTimeFrames.Add(_timeFrames[timeFrame],
                                getNextCandleTime(timeFrame,
                                    DateTimeOffset.FromUnixTimeMilliseconds(candle.OpenTime).UtcDateTime));
                            File.AppendAllText("Logs/Strategy.log", "we have a partial signal\n");
                            File.AppendAllText("Logs/Strategy.log", timeFrame+"\n");
                            File.AppendAllText("Logs/Strategy.log", candle.ToString()+"\n");
                            File.AppendAllText("Logs/Strategy.log", "time limit is:"+
                                                                    DateTimeOffset.FromUnixTimeMilliseconds(getNextCandleTime(timeFrame,
                                                                        DateTimeOffset.FromUnixTimeMilliseconds(candle.OpenTime).UtcDateTime)).DateTime.ToString("yyyy-MM-dd HH:mm:ss")+"\n");
                        }

                        if (signal == SignalType.Buy)
                        {
                            Trade trade = new Trade()
                            {
                                BrokerId = 1,
                                Symbol = _instrument.CoinName,
                                Quantity = 1,
                                EntryPrice = candle.Close,
                                ExitPrice = 0m,
                                StopPrice = 0m,
                                TakeProfit = 0m, // Ensure this has a value
                                Fee = 0m, // Ensure this has a value
                                OrderId = "", // Ensure this has a value (or make it nullable in DB)
                                TradeType = TradeType.BUY,
                                Timestamp = DateTime.UnixEpoch.AddMilliseconds(candle.OpenTime),
                                Status = TradeStatus.Open,
                                TimeFrame = (_signalsTimeFrames.Any() ? _timeFrames.FirstOrDefault( pair => pair.Value ==  _signalsTimeFrames.ElementAt(0).Key).Key : timeFrame),
                            };
                            try
                            {
                                await _tradeManagerService.CreateTradeAsync(trade);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Error saving trade: {ex.Message}");
                                if (ex.InnerException != null)
                                {
                                    Console.WriteLine($"Inner Exception: {ex.InnerException.Message}");
                                }
                            }
                        }

                        if (signal == SignalType.Sell)
                        {
                            Trade trade = new Trade()
                            {
                                BrokerId = 1,
                                Symbol = _instrument.CoinName,
                                Quantity = 1,
                                EntryPrice = candle.Close,
                                ExitPrice = 0m,
                                StopPrice = 0m,
                                TakeProfit = 0m, // Ensure this has a value
                                Fee = 0m, // Ensure this has a value
                                OrderId = "", // Ensure this has a value (or make it nullable in DB)
                                TradeType = TradeType.SELL,
                                Timestamp = DateTime.UnixEpoch.AddMilliseconds(candle.OpenTime),
                                Status = TradeStatus.Open,
                                TimeFrame =
                                    (_signalsTimeFrames.Any() ? _timeFrames.FirstOrDefault( pair => pair.Value ==  _signalsTimeFrames.ElementAt(0).Key).Key : timeFrame),
                            };
                            try
                            {
                                await _tradeManagerService.CreateTradeAsync(trade);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Error saving trade: {ex.Message}");
                                if (ex.InnerException != null)
                                {
                                    Console.WriteLine($"Inner Exception: {ex.InnerException.Message}");
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    private void ProcessInitialData(List<List<object>> data, string timeFrame)
    {
        CircularLinkedList<CandleData> chart = _charts[timeFrame];
        int index = 1;
        foreach (var item in data)
        {
            if (index < data.Count)
            {
                if (item is List<object> list && list.Count >= 12)
                {
                    CandleData candle = chart.GetCurrent().Data;
                    UpdateCandleData(candle, list, index, timeFrame);
                    File.AppendAllText(_filePath, candle.ToString());
                    chart.MoveNext();
                }
            }

            index += 1;
        }
    }

    private void UpdateCandleData(CandleData candle, List<object> list, int index, String timeFrame)
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
            candle.Gain = CalculateGain(candle.Close, _charts[timeFrame].GetPrevious().Data.Close);
            candle.Loss = CalculateLoss(candle.Close, _charts[timeFrame].GetPrevious().Data.Close);
        }

        UpdateIndicators(candle, index, timeFrame);
        candle.isComplete = true;
    }

    private void UpdateIndicators(CandleData candle, int index, string timeFrame, bool isLive = false)
    {
        List<Indicator> indicators = _indicators[timeFrame];
        foreach (Indicator indicator in indicators)
        {
            if (indicator is RSI rsi)
            {
                candle.RSI = rsi.Calculate(index, _charts[timeFrame].GetCurrent(), isLive);
            }
            else if (indicator is StochRSI stochRSI && stochRSI.WindowSize + 1 <= index)
            {
                stochRSI.Calculate(_charts[timeFrame].GetCurrent());
            }
            else if (indicator is BollingerBand bollingerBand && bollingerBand.WindowSize + 1 <= index)
            {
                bollingerBand.Calculate(_charts[timeFrame].GetCurrent());
            }
            else if (indicator is AverageTrueRange averageTrueRange && averageTrueRange.GetWindow() +1 <= index)
            {
                candle.ATR = averageTrueRange.Calculate(_charts[timeFrame].GetCurrent(), isLive);
            }
        }
    }

    public async Task Run()
    {
        await InitAsync();
        int index = 28;
        while (true)
        {
            List<BinanceKline> data = await _rest.Get<BinanceKline>(_instrument.GetTheLastKlines("1s", 2));
            if (data.Count == 0)
            {
                Console.WriteLine("No data received.");
                return;
            }

            if (await ProcessLiveData(data, index))
                break;
            await Task.Delay(500);
        }
    }

    private async Task<bool> ProcessLiveData(List<BinanceKline> data, int index)
    {
        string liveLogFilePath = "Logs/" + _instrument.BrokerName + "-" + _instrument.CoinName + "-live" + ".log";

        foreach (var timeFrame in _timeFrames.Keys)
        {
            _charts.TryGetValue(timeFrame, out var chart);
            // here we are on the current candle as on the init function last iteration it will move to the
            // next candle
            chart.MovePrevious();// we are on the current candle now 
            long prevCandleTime = chart.GetPrevious().Data.OpenTime;
            DateTimeOffset prevCandleTimeOffset = DateTimeOffset.FromUnixTimeMilliseconds(prevCandleTime);
            long currentCandleTime = getNextCandleTime(timeFrame, prevCandleTimeOffset);
            long nextCandleTime = getNextCandleTime(timeFrame, prevCandleTimeOffset, 2);
            if (data[1].OpenTime >= currentCandleTime && data[1].OpenTime < nextCandleTime)
            {
                UpdateCurrentCandle(chart.GetCurrent().Data, data.Last(), index, timeFrame, true);
                /*File.AppendAllText(liveLogFilePath, chart.GetCurrent().Data.ToString());
                Console.WriteLine("we are on the first if");
                Console.WriteLine("we updated the candle on time frame" + timeFrame);*/
            }

            if (data[0].OpenTime >= currentCandleTime && data[1].OpenTime >= nextCandleTime)
            {
                UpdateCurrentCandle(chart.GetCurrent().Data, data.First(), index, timeFrame, false);
                await Strategy(chart.GetCurrent().Data, timeFrame);
                /*Console.WriteLine("we are on the second if");
                Console.WriteLine("we updated the candle on time frame" + timeFrame);
                File.AppendAllText(_filePath, "=============================================\n" + timeFrame + "\n");
                File.AppendAllText(_filePath, chart.GetCurrent().Data.ToString());*/
                chart.MoveNext();
                //return true;
            }
        }

        return false;
    }

    private long getNextCandleTime(string timeFrame, DateTimeOffset time, int offset = 1)
    {
        var pattern = @"(\d+)([a-zA-Z])"; // Pattern to match numbers followed by a letter (e.g., 5m, 1y, 2m)
        var matches = Regex.Matches(timeFrame, pattern);
        string numberString = "";
        string unit = "";
        long newTime = 0;
        foreach (Match match in matches)
        {
            numberString = match.Groups[1].Value; // The number (e.g., 5, 1, 2)
            unit = match.Groups[2].Value; // The time unit (e.g., m, y)
        }

        if (int.TryParse(numberString, out int number))
        {
            Console.WriteLine($"Number: {number}, Unit: {unit}");
        }
        else
        {
            Console.WriteLine($"Failed to convert {numberString} to an integer.");
        }

        switch (unit)
        {
            case "h":
                newTime = time.AddHours(number * offset).ToUnixTimeMilliseconds();
                break;
            case "m":
                newTime = time.AddMinutes(number * offset).ToUnixTimeMilliseconds();
                break;
            case "s":
                newTime = time.AddSeconds(number * offset).ToUnixTimeMilliseconds();
                break;
            default:
                // we didn't have a correct time frame
                break;
        }

        return newTime;
    }

    private void UpdateCurrentCandle(CandleData candle, BinanceKline kline, int index, string timeFrame,
        bool isLive = false)
    {
        var currentCandle = candle;
        currentCandle.OpenTime = candle.OpenTime;
        currentCandle.Close = ConvertToDecimal(kline.Close);
        currentCandle.Open = ConvertToDecimal(kline.Open);
        currentCandle.High = ConvertToDecimal(kline.High);
        currentCandle.Low = ConvertToDecimal(kline.Low);
        currentCandle.Volume = ConvertToDecimal(kline.Volume);
        currentCandle.Gain = CalculateGain(currentCandle.Close, _charts[timeFrame].GetPrevious().Data.Close);
        currentCandle.Loss = CalculateLoss(currentCandle.Close, _charts[timeFrame].GetPrevious().Data.Close);

        UpdateIndicators(currentCandle, index, timeFrame, isLive);
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