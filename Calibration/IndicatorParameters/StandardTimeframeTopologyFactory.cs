using Brokers.Models;
using ChartAnnotator.MarketData;

namespace Simulator.Calibration;

/// <summary>
/// The single fixed multi-timeframe shape indicator-calibration tooling assumes for any execution
/// interval (Setup 15m / Trend 1h, matching <c>generate-request</c>'s own defaults) - factored out
/// so <c>generate-request</c> and <see cref="BestApprovedCalibrationArtifactResolver"/> always
/// compute the same <see cref="TimeframeTopology.ComputeHash"/> for the same execution interval.
/// </summary>
public static class StandardTimeframeTopologyFactory
{
    public const int DefaultWarmupDays = 10;

    public static TimeframeTopology Build(BarInterval executionInterval, int warmupDays = DefaultWarmupDays) => new()
    {
        ExecutionInterval = executionInterval,
        AnalysisBaseInterval = executionInterval,
        SetupInterval = BarInterval.Minutes(15),
        ConfirmationIntervals = [],
        TrendIntervals = [BarInterval.Hours(1)],
        ManagementIntervals = [],
        AlignmentPolicy = BaseCandleGapPolicy.ResetIncompleteBuckets,
        WarmupMinimumDays = warmupDays
    };
}
