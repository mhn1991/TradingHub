namespace LiveTrading.AccountLease;

public sealed record AccountLeaseOptions
{
    public const string SectionName = "AccountLease";

    /// <summary>Resolved relative to the host's content root when not rooted.</summary>
    public string Directory { get; init; } = ".state/live-trading";

    public TimeSpan RenewInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A lease is considered stale once <c>RenewedAt</c> is older than this. Default is
    /// 3x <see cref="RenewInterval"/>.</summary>
    public TimeSpan StaleThreshold { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>When true, a stale lease is deleted and re-acquired on startup. Never applies to
    /// a lease that is not stale - a live, healthy holder is never displaced.</summary>
    public bool Force { get; init; }
}
