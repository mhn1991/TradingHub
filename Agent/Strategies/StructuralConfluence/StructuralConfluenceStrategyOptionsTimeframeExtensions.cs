using Brokers.Models;

namespace Agent.Strategies.StructuralConfluence;

/// <summary>
/// Read-only projection of <see cref="StructuralConfluenceStrategyOptions"/>'s timeframe fields
/// into the unified <see cref="TimeframePlan"/> shape (PROJECT_STATE.md §4b, Phase 1). All context
/// intervals (primary + additional) map to <see cref="TimeframeInfluence.Gate"/>: unlike
/// Progressive's alignment-vote context, StructuralEvidencePacketFactory (in the Evidence
/// namespace) requires every configured context interval's snapshot to be ready before a packet
/// can build at all.
/// </summary>
public static class StructuralConfluenceStrategyOptionsTimeframeExtensions
{
    public static TimeframePlan ToTimeframePlan(this StructuralConfluenceStrategyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var assignments = new List<TimeframeAssignment>
        {
            new() { Role = TimeframeRole.Trigger, Interval = options.TriggerInterval, Influence = TimeframeInfluence.Gate },
            new() { Role = TimeframeRole.Setup, Interval = options.SetupInterval, Influence = TimeframeInfluence.Gate },
            new() { Role = TimeframeRole.Context, Interval = options.ContextInterval, Influence = TimeframeInfluence.Gate, Priority = 0 }
        };

        int priority = 1;
        foreach (BarInterval interval in options.AdditionalContextIntervals)
            assignments.Add(new() { Role = TimeframeRole.Context, Interval = interval, Influence = TimeframeInfluence.Gate, Priority = priority++ });

        return new TimeframePlan { Assignments = assignments };
    }

    /// <summary>
    /// Write-side complement of <see cref="ToTimeframePlan"/> (PROJECT_STATE.md §4b, "Phase 3,
    /// revised"): applies a plan back onto <paramref name="options"/> role by role, leaving roles
    /// absent from <paramref name="plan"/> untouched. Never changes
    /// <see cref="StructuralConfluenceStrategyOptions"/>'s own persisted property shape.
    /// </summary>
    public static StructuralConfluenceStrategyOptions ApplyTimeframePlan(this StructuralConfluenceStrategyOptions options, TimeframePlan plan)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(plan);

        IReadOnlyList<TimeframeAssignment> contextGates = plan.For(TimeframeRole.Context, TimeframeInfluence.Gate);

        return options with
        {
            TriggerInterval = plan.First(TimeframeRole.Trigger, TimeframeInfluence.Gate) ?? options.TriggerInterval,
            SetupInterval = plan.First(TimeframeRole.Setup, TimeframeInfluence.Gate) ?? options.SetupInterval,
            ContextInterval = contextGates.Count > 0 ? contextGates[0].Interval : options.ContextInterval,
            AdditionalContextIntervals = contextGates.Count > 0
                ? [.. contextGates.Skip(1).Select(a => a.Interval)]
                : options.AdditionalContextIntervals
        };
    }
}
