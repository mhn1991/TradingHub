namespace DBManager.Abstractions;

/// <summary>
/// Distinguishes how a durable write against the critical persistence lane concluded, per the
/// completion semantics in the PostgreSQL persistence architecture (section 10.4).
/// </summary>
public enum DurableOutcome
{
    Committed,
    Duplicate,
    RejectedByConcurrency,
    RetryableFailure,
    PermanentFailure
}

/// <summary>Result of a durable critical-lane write.</summary>
public sealed record DurableResult
{
    public required DurableOutcome Outcome { get; init; }
    public string? ReasonCode { get; init; }
    public string? Detail { get; init; }

    public static DurableResult Committed() => new() { Outcome = DurableOutcome.Committed };

    public static DurableResult Duplicate(string reasonCode) =>
        new() { Outcome = DurableOutcome.Duplicate, ReasonCode = reasonCode };

    public static DurableResult RejectedByConcurrency(string reasonCode) =>
        new() { Outcome = DurableOutcome.RejectedByConcurrency, ReasonCode = reasonCode };

    public static DurableResult RetryableFailure(string reasonCode, string? detail = null) =>
        new() { Outcome = DurableOutcome.RetryableFailure, ReasonCode = reasonCode, Detail = detail };

    public static DurableResult PermanentFailure(string reasonCode, string? detail = null) =>
        new() { Outcome = DurableOutcome.PermanentFailure, ReasonCode = reasonCode, Detail = detail };
}
