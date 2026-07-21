using Brokers.Models;
using ChartAnnotator.MarketData;

namespace Simulator.Calibration;

/// <summary>
/// The multi-timeframe shape indicator-calibration tooling builds for a given execution interval -
/// factored out so <c>generate-request</c> and <see cref="BestApprovedCalibrationArtifactResolver"/>
/// always compute the same <see cref="TimeframeTopology.ComputeHash"/> for the same
/// execution/setup/context combination. Setup/context default to 15m/1h (matching
/// <c>generate-request</c>'s own defaults and every existing artifact's topology hash) when a caller
/// doesn't need to test a different decision cadence.
/// </summary>
public static class StandardTimeframeTopologyFactory
{
    public const int DefaultWarmupDays = 10;
    public static readonly BarInterval DefaultSetupInterval = BarInterval.Minutes(15);
    public static readonly BarInterval DefaultContextInterval = BarInterval.Hours(1);

    /// <param name="executionInterval">The strategy's trigger/decision interval and simulation fill precision.</param>
    /// <param name="warmupDays">Minimum warmup window before the evaluation period begins.</param>
    /// <param name="setupInterval">Defaults to <see cref="DefaultSetupInterval"/> (15m) when null.</param>
    /// <param name="contextInterval">Defaults to <see cref="DefaultContextInterval"/> (1h) when null.</param>
    public static TimeframeTopology Build(
        BarInterval executionInterval,
        int warmupDays = DefaultWarmupDays,
        BarInterval? setupInterval = null,
        BarInterval? contextInterval = null) => new()
    {
        ExecutionInterval = executionInterval,
        AnalysisBaseInterval = executionInterval,
        SetupInterval = setupInterval ?? DefaultSetupInterval,
        ConfirmationIntervals = [],
        TrendIntervals = [contextInterval ?? DefaultContextInterval],
        ManagementIntervals = [],
        AlignmentPolicy = BaseCandleGapPolicy.ResetIncompleteBuckets,
        WarmupMinimumDays = warmupDays
    };
}
