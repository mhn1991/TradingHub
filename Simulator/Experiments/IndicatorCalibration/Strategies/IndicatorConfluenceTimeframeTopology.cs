using Agent.Strategies.StructuralConfluence;
using Simulator.Calibration;
using Simulator.Models;

namespace Simulator.Experiments.IndicatorCalibration.Strategies;

/// <summary>
/// Derives the <see cref="TimeframeTopology"/> a real, running structural-confluence assignment
/// actually uses, from its <see cref="BacktestRuntimeOptions"/> and effective
/// <see cref="StructuralConfluenceStrategyOptions"/>. This is the consumption-side counterpart to
/// whatever topology an <c>IndicatorCalibrationRequest</c> for the same instrument was authored
/// with - the two must agree, or <see cref="IndicatorCalibrationOverlayCompatibilityValidator"/>
/// will (correctly) reject every overlay for this assignment on a topology-hash mismatch. When
/// authoring a calibration request for a real assignment, build its <c>TimeframeTopology</c> using
/// this exact function (with the same runtime/strategy options the assignment will actually run
/// with) so the two never drift apart.
/// </summary>
public static class IndicatorConfluenceTimeframeTopology
{
    public static TimeframeTopology Resolve(
        BacktestRuntimeOptions runtime, StructuralConfluenceStrategyOptions strategyOptions)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(strategyOptions);
        return new TimeframeTopology
        {
            ExecutionInterval = runtime.ExecutionInterval,
            AnalysisBaseInterval = runtime.AnalysisBaseInterval,
            SetupInterval = strategyOptions.SetupInterval,
            ConfirmationIntervals = [],
            TrendIntervals = [strategyOptions.ContextInterval, .. strategyOptions.AdditionalContextIntervals],
            ManagementIntervals = [],
            AlignmentPolicy = runtime.BaseCandleGapPolicy,
            WarmupMinimumDays = runtime.WarmupDays
        };
    }
}
