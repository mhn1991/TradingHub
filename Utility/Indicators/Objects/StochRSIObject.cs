namespace Utility.Indicators.Objects;

public class StochRSIObject : IndicatorObject
{
    public decimal minRSI { get; set; } = decimal.MaxValue;
    public decimal maxRSI { get; set; } = decimal.MinValue;
    public decimal widowSize { get; set; } = 14;
    public long numberOfRSI { get; set; } = 0;
    public bool calcStochRsi { get; set; } = false;
    public int kPeriod { get; set; } = 3;
    public int dPeriod { get; set; } = 3;
}