namespace Brokers.Brokers;

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