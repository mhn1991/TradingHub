namespace Brokers.Models;

/// <summary>
/// What purpose a timeframe serves in a strategy's decision or management logic. Orthogonal to
/// <see cref="TimeframeInfluence"/> (which controls how an interval assigned to a role behaves) -
/// this separation is what lets a role hold more than one interval (e.g. two Trigger timeframes)
/// without needing a new type or a new role: cardinality is just "how many assignments share a
/// (Role, Influence) pair," a data fact rather than a schema fact. See PROJECT_STATE.md §4b for
/// the full design rationale.
/// </summary>
public enum TimeframeRole
{
    Trigger,
    Setup,
    Context,
    Confirmation,
    Regime,
    NeoWave,
    ManagementThesis,
    ManagementMain,
    ManagementFast
}

/// <summary>
/// How a single <see cref="TimeframeAssignment"/> affects the decision its <see cref="TimeframeRole"/>
/// participates in. Multiple assignments sharing a role can mix influences (e.g. one Gate trigger
/// plus several Vote triggers) - the combination behavior is expressed entirely by which influence
/// each assignment carries, not by the role or by any list-shaped property.
/// </summary>
public enum TimeframeInfluence
{
    /// <summary>Must agree, else no trade - a hard requirement.</summary>
    Gate,
    /// <summary>Can block or downgrade a candidate but is not independently required.</summary>
    Veto,
    /// <summary>Contributes to an N-of-M tally; the threshold itself is a strategy-level concern, not part of the plan.</summary>
    Vote,
    /// <summary>Informational only - visible to decision/diagnostic logic but never gates, vetoes, or votes.</summary>
    Advisory,
    /// <summary>Used only if a higher-priority assignment for the same role is unavailable or invalid.</summary>
    Fallback
}

/// <summary>One timeframe's role assignment. <see cref="Priority"/> orders same-role entries (lower first); it drives <see cref="TimeframeInfluence.Fallback"/> resolution and breaks display/evaluation ties for other influences.</summary>
public sealed record TimeframeAssignment
{
    public required TimeframeRole Role { get; init; }
    public required BarInterval Interval { get; init; }
    public required TimeframeInfluence Influence { get; init; }
    public int Priority { get; init; }
}

/// <summary>
/// A strategy or manager's full set of timeframe role assignments, in the unified shape described
/// in PROJECT_STATE.md §4b. Phase 1: built only via read-only adapters projected from the existing
/// per-family option types (<c>ProgressiveStrategyOptions</c>, <c>StructuralConfluenceStrategyOptions</c>,
/// <c>PositionManagementOptions</c>) - nothing persisted or hashed changes shape yet.
/// </summary>
public sealed record TimeframePlan
{
    public required IReadOnlyList<TimeframeAssignment> Assignments { get; init; }

    public IReadOnlyList<TimeframeAssignment> For(TimeframeRole role, TimeframeInfluence? influence = null) =>
        [.. Assignments.Where(a => a.Role == role && (influence is null || a.Influence == influence))
                       .OrderBy(a => a.Priority)];

    /// <summary>The highest-priority (lowest <see cref="TimeframeAssignment.Priority"/>) interval for a role, or null if none assigned.</summary>
    public BarInterval? First(TimeframeRole role, TimeframeInfluence? influence = null)
    {
        IReadOnlyList<TimeframeAssignment> matches = For(role, influence);
        return matches.Count == 0 ? null : matches[0].Interval;
    }
}
