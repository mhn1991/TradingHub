using TrendStatistics.Data;

namespace TrendStatistics.Segmentation;

public interface ITrendSegmenter
{
    IReadOnlyList<TrendRecord> Segment(IEnumerable<Candle> candles);
}
