using TrendStatistics.Segmentation;

namespace TrendStatistics.Detection;

public sealed record TrendDetectorUpdate(
    TrendState State,
    TrendRecord? CompletedTrend = null);
