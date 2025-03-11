using System.Text.Json.Serialization;

namespace Brokers.Brokers;

public class BinanceKline
{
    [JsonPropertyName("0")] public long OpenTime { get; set; }
    [JsonPropertyName("1")] public string Open { get; set; }
    [JsonPropertyName("2")] public string High { get; set; }
    [JsonPropertyName("3")] public string Low { get; set; }
    [JsonPropertyName("4")] public string Close { get; set; }
    [JsonPropertyName("5")] public string Volume { get; set; }
}

public class CandleData
{
    public long OpenTime { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public decimal Volume { get; set; }
    public decimal Gain { get; set; }
    public decimal Loss { get; set; }
    public decimal RSI { get; set; }
    public decimal StochRSI { get; set; }
    public decimal StochRSIK { get; set; }
    public decimal BollingerBandUpperband  { get; set; }
    public decimal BollingerBandLowerband  { get; set; }
    public decimal BollingerBandMiddleband  { get; set; }
    public bool isComplete { get; set; }
    
    public interface ICandleMapper<T>
    {
        CandleData Map(T dto);
    }

    public class BinanceCandleMapper : ICandleMapper<BinanceKline>
    {
        public CandleData Map(BinanceKline kline)
        {
            return new CandleData
            {
                OpenTime = kline.OpenTime,
                Open = decimal.Parse(kline.Open),
                High = decimal.Parse(kline.High),
                Low = decimal.Parse(kline.Low),
                Close = decimal.Parse(kline.Close),
                Volume = decimal.Parse(kline.Volume),
                isComplete = false // REST API candles are closed
            };
        }
    }
    
    public override string ToString()
    {
        DateTime openDateTime = DateTime.UnixEpoch.AddMilliseconds(OpenTime);
        return $"CandleData [OpenTime: {openDateTime:yyyy-MM-dd HH:mm:ss}, StampedTime: {OpenTime}, Open: {Open:F2}, High: {High:F2}, " +
               $"Low: {Low:F2}, Close: {Close:F2}, Volume: {Volume:F2}, " +
               $"Gain: {Gain:F2}, Loss: {Loss:F2}, RSI: {RSI:F2}, " +
               $"StochRSIK: {StochRSIK:F4}, BB Upper: {BollingerBandUpperband:F2}, " +
               $"BB Lower: {BollingerBandLowerband:F2}, BB Middle: {BollingerBandMiddleband:F2}]\n";
    }
}