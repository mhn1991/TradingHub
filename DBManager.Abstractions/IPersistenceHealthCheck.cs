namespace DBManager.Abstractions;

/// <summary>Whether critical persistence is currently able to accept durable writes.</summary>
public enum PersistenceHealthStatus
{
    Healthy,
    Degraded,
    Unavailable
}

/// <summary>
/// Snapshot of persistence health used to gate new entries per the core rule
/// "No new entry while critical persistence is unhealthy" (section 1.3).
/// </summary>
public sealed record PersistenceHealthReport
{
    public required PersistenceHealthStatus Status { get; init; }
    public required DateTimeOffset CheckedAt { get; init; }
    public TimeSpan? Latency { get; init; }
    public string? Detail { get; init; }
}

/// <summary>Checks whether the critical persistence lane is reachable and accepting writes.</summary>
public interface IPersistenceHealthCheck
{
    Task<PersistenceHealthReport> CheckAsync(CancellationToken cancellationToken);
}
