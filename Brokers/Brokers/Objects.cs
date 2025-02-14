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
}