using TrendStatistics.Data;

namespace TrendStatistics.Detection;

public interface ITrendDetector
{
    TrendState CurrentState { get; }

    TrendDetectorUpdate Apply(Candle candle);
}
