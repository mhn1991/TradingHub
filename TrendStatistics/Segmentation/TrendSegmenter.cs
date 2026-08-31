using TrendStatistics.Data;
using TrendStatistics.Detection;

namespace TrendStatistics.Segmentation;

/// <summary>
/// Streams candles through a fresh detector and returns only causally completed trends.
/// An active trend at the dataset boundary is intentionally not force-closed.
/// </summary>
public sealed class TrendSegmenter(TrendDetectorConfig? config = null) : ITrendSegmenter
{
    private readonly TrendDetectorConfig _config = config ?? new TrendDetectorConfig();

    public IReadOnlyList<TrendRecord> Segment(IEnumerable<Candle> candles)
    {
        ArgumentNullException.ThrowIfNull(candles);

        var detector = new TrendDetector(_config);
        var records = new List<TrendRecord>();

        foreach (Candle candle in candles)
        {
            TrendDetectorUpdate update = detector.Apply(candle);
            if (update.CompletedTrend is not null)
            {
                records.Add(update.CompletedTrend);
            }
        }

        return records;
    }
}
