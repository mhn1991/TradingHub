namespace Brokers.Models;

/// <summary>
/// Shared text encoding for a <see cref="TimeframePlan"/>, used by every opt-in unified surface
/// (PROJECT_STATE.md §4b, Phase 2): <c>BacktestRunner</c>'s <c>--timeframes</c> CLI flag and the
/// Dashboard's equivalent request field both parse this same syntax, so the DSL only needs to be
/// learned once. Comma-separated <c>role=interval[:influence[:priority]]</c> entries, e.g.
/// <c>trigger=5m,context=1h:gate:0,context=4h:vote:1,confirmation=15m:vote:0,confirmation=1h:vote:1</c>.
/// <c>influence</c> defaults to <c>gate</c>; <c>priority</c> defaults to entry order within the role
/// (0-based).
/// </summary>
public static class TimeframePlanTextFormat
{
    public static TimeframePlan Parse(string spec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spec);

        var assignments = new List<TimeframeAssignment>();
        var nextPriorityByRole = new Dictionary<TimeframeRole, int>();

        foreach (string entry in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = entry.Split(':', StringSplitOptions.TrimEntries);
            string[] roleAndInterval = parts[0].Split('=', 2, StringSplitOptions.TrimEntries);
            if (roleAndInterval.Length != 2)
                throw new ArgumentException($"Invalid timeframes entry '{entry}'. Expected role=interval[:influence[:priority]].");

            TimeframeRole role = ParseRole(roleAndInterval[0]);
            BarInterval interval = BarIntervalParser.Parse(roleAndInterval[1]);
            TimeframeInfluence influence = parts.Length > 1 ? ParseInfluence(parts[1]) : TimeframeInfluence.Gate;
            int priority = parts.Length > 2
                ? ParsePriority(parts[2])
                : nextPriorityByRole.GetValueOrDefault(role);
            nextPriorityByRole[role] = priority + 1;

            assignments.Add(new TimeframeAssignment { Role = role, Interval = interval, Influence = influence, Priority = priority });
        }

        return new TimeframePlan { Assignments = assignments };
    }

    private static int ParsePriority(string value) =>
        int.TryParse(value, out int priority) && priority >= 0
            ? priority
            : throw new ArgumentException($"Invalid timeframes priority '{value}'. Expected a non-negative integer.");

    private static TimeframeRole ParseRole(string value) => value.Trim().ToLowerInvariant() switch
    {
        "trigger" => TimeframeRole.Trigger,
        "setup" => TimeframeRole.Setup,
        "context" => TimeframeRole.Context,
        "confirmation" => TimeframeRole.Confirmation,
        "regime" => TimeframeRole.Regime,
        "neowave" or "neo-wave" => TimeframeRole.NeoWave,
        "managementthesis" or "management-thesis" => TimeframeRole.ManagementThesis,
        "managementmain" or "management-main" => TimeframeRole.ManagementMain,
        "managementfast" or "management-fast" => TimeframeRole.ManagementFast,
        _ => throw new ArgumentException($"Unknown timeframes role '{value}'.")
    };

    private static TimeframeInfluence ParseInfluence(string value) => value.Trim().ToLowerInvariant() switch
    {
        "gate" => TimeframeInfluence.Gate,
        "veto" => TimeframeInfluence.Veto,
        "vote" => TimeframeInfluence.Vote,
        "advisory" => TimeframeInfluence.Advisory,
        "fallback" => TimeframeInfluence.Fallback,
        _ => throw new ArgumentException($"Unknown timeframes influence '{value}'.")
    };
}
