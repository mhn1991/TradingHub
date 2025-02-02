using System.Net.Http.Json;
using Brokers.Brokers;
namespace API;

public class Rest
{
    private readonly HttpClient _httpClient;
    public Rest()
    { 
        _httpClient = new HttpClient();
    }
    public async Task Get(string url)
    {
        List<List<object>>? rawData = await _httpClient.GetFromJsonAsync<List<List<object>>>(url);
        if (rawData == null)
        {
            Console.WriteLine("No data received.");
            return;
        }

        // Step 2: Convert to List of CandleData
        List<CandleData> candles = new List<CandleData>();

        foreach (var item in rawData)
        {
            CandleData candle = new CandleData
            {
                OpenTime = Convert.ToInt64(item[0]),
                Open = Convert.ToDecimal(item[1]),
                High = Convert.ToDecimal(item[2]),
                Low = Convert.ToDecimal(item[3]),
                Close = Convert.ToDecimal(item[4]),
                Volume = Convert.ToDecimal(item[5]),
                CloseTime = Convert.ToInt64(item[6]),
                QuoteAssetVolume = Convert.ToDecimal(item[7]),
                NumberOfTrades = Convert.ToInt32(item[8]),
                TakerBuyBaseAssetVolume = Convert.ToDecimal(item[9]),
                TakerBuyQuoteAssetVolume = Convert.ToDecimal(item[10]),
                Ignore = item[11]?.ToString()
            };

            candles.Add(candle);
        }
        
        
    }
}