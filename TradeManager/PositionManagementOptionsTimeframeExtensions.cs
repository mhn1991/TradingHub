using Brokers.Models;

namespace TradeManager;

/// <summary>
/// Read-only projection of <see cref="PositionManagementOptions"/>'s timeframe fields into the
/// unified <see cref="TimeframePlan"/> shape (PROJECT_STATE.md §4b, Phase 1).
/// <see cref="PositionManagementOptions.ManagementInterval"/> is intentionally omitted: it is
/// already documented as a pure legacy alias of <see cref="PositionManagementOptions.MainStructureInterval"/>,
/// not a distinct role, so including it would double-count the ManagementMain assignment.
/// </summary>
public static class PositionManagementOptionsTimeframeExtensions
{
    public static TimeframePlan ToTimeframePlan(this PositionManagementOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var assignments = new List<TimeframeAssignment>();

        if (options.ThesisInterval is BarInterval thesis)
            assignments.Add(new() { Role = TimeframeRole.ManagementThesis, Interval = thesis, Influence = TimeframeInfluence.Gate });

        if (options.MainStructureInterval is BarInterval main)
            assignments.Add(new() { Role = TimeframeRole.ManagementMain, Interval = main, Influence = TimeframeInfluence.Gate });

        if (options.FastStructureInterval is BarInterval fast)
            assignments.Add(new() { Role = TimeframeRole.ManagementFast, Interval = fast, Influence = TimeframeInfluence.Gate });

        return new TimeframePlan { Assignments = assignments };
    }

    /// <summary>
    /// Write-side complement of <see cref="ToTimeframePlan"/> (PROJECT_STATE.md §4b, "Phase 3,
    /// revised"): applies a plan back onto <paramref name="options"/> role by role, leaving roles
    /// absent from <paramref name="plan"/> untouched. <see cref="PositionManagementOptions.ManagementInterval"/>
    /// (the legacy alias) is never written by this method.
    /// </summary>
    public static PositionManagementOptions ApplyTimeframePlan(this PositionManagementOptions options, TimeframePlan plan)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(plan);

        return options with
        {
            ThesisInterval = plan.First(TimeframeRole.ManagementThesis, TimeframeInfluence.Gate) ?? options.ThesisInterval,
            MainStructureInterval = plan.First(TimeframeRole.ManagementMain, TimeframeInfluence.Gate) ?? options.MainStructureInterval,
            FastStructureInterval = plan.First(TimeframeRole.ManagementFast, TimeframeInfluence.Gate) ?? options.FastStructureInterval
        };
    }
}
