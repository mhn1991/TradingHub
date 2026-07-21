using System.Linq;
using Agent.Strategies.StructuralConfluence;
using Brokers.Models;
using Simulator.Calibration;
using TradeManager;

namespace Simulator.Experiments.IndicatorCalibration.Strategies;

/// <summary>
/// Threads a calibration request's chosen timeframe stack (<see cref="TimeframeTopology.ExecutionInterval"/>
/// as the strategy's trigger interval, <see cref="TimeframeTopology.SetupInterval"/>, and
/// <see cref="TimeframeTopology.TrendIntervals"/>'s first entry as the context interval) into both
/// the traded agent's own options and the position-management stacks
/// <c>BacktestRuntimeOptions.Validate()</c> unconditionally checks. Every calibration strategy
/// adapter uses this instead of taking <c>_resolveBaselineOptions</c>'s result or the static
/// <see cref="PositionManagementOptions"/> presets as-is, so a request that asks for a non-default
/// decision cadence (e.g. a 15m trigger instead of the default 5m) actually changes what the agent
/// decides on, rather than only affecting simulation fill precision as it did before.
/// </summary>
public static class CalibrationTimeframeStack
{
    /// <summary>
    /// Overlays the request's chosen trigger/setup/context onto <paramref name="baseline"/>. Every
    /// other field (which playbooks are enabled, their thresholds, geometry, etc.) comes from
    /// <paramref name="baseline"/> unchanged - this only ever touches the three interval fields.
    /// </summary>
    public static StructuralConfluenceStrategyOptions ApplyTo(
        StructuralConfluenceStrategyOptions baseline, TimeframeTopology topology)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(topology);
        return baseline with
        {
            TriggerInterval = topology.ExecutionInterval,
            SetupInterval = topology.SetupInterval,
            ContextInterval = topology.TrendIntervals[0],
            AdditionalContextIntervals = topology.TrendIntervals.Skip(1).ToArray()
        };
    }

    /// <summary>
    /// Re-pins <see cref="PositionManagementOptions.FastStructureInterval"/>/
    /// <see cref="PositionManagementOptions.MainStructureInterval"/>/
    /// <see cref="PositionManagementOptions.ThesisInterval"/> on top of each of the three static
    /// presets to match the given trigger/setup/context, keeping every other tuned field (scale-out
    /// rules, activation thresholds, etc.) on each preset untouched.
    /// <c>BacktestRuntimeOptions.Validate()</c> checks all three stacks (Legacy/Improved/Structural)
    /// regardless of which playbook is actually active in this run, so all three need to agree with
    /// whatever timeframe stack this request targets - at the 5m/15m/1h default this reproduces each
    /// preset's existing static values exactly (zero behavior change); at any other trigger interval
    /// it scales the stack so validation passes instead of failing the way a mismatched fixed 5m/15m/1h
    /// stack did for the old 15m/1h execution-interval sweep. Takes raw intervals (not a
    /// <see cref="TimeframeTopology"/>) so non-calibration callers - e.g. the plain
    /// <c>BacktestRunner</c> CLI's structural-confluence support - can reuse it too.
    /// </summary>
    public static (PositionManagementOptions Legacy, PositionManagementOptions Improved, PositionManagementOptions Structural)
        BuildPositionManagementOverrides(BarInterval triggerInterval, BarInterval setupInterval, BarInterval contextInterval)
    {
        PositionManagementOptions Rescale(PositionManagementOptions preset) => preset with
        {
            FastStructureInterval = triggerInterval,
            MainStructureInterval = setupInterval,
            ThesisInterval = contextInterval
        };

        return (
            Rescale(PositionManagementOptions.LegacyDefaults),
            Rescale(PositionManagementOptions.ImprovedDefaults),
            Rescale(PositionManagementOptions.StructuralDefaults));
    }
}
