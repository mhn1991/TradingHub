namespace LiveTrading.AccountLease;

public enum AccountLeaseOutcome
{
    Acquired,
    HeldByOther,
    HeldByOtherStale
}

public sealed record LeaseMetadata
{
    public required string Broker { get; init; }
    public required string AccountId { get; init; }
    public required string InstanceId { get; init; }
    public required string MachineName { get; init; }
    public required int ProcessId { get; init; }
    public required DateTimeOffset AcquiredAt { get; init; }
    public required DateTimeOffset RenewedAt { get; init; }
}

public sealed record AccountLeaseResult
{
    public required AccountLeaseOutcome Outcome { get; init; }
    public LeaseMetadata? CurrentHolder { get; init; }
}

/// <summary>Only one process may control one broker account at a time.</summary>
public interface ITradingAccountLease
{
    Task<AccountLeaseResult> TryAcquireAsync(
        string broker,
        string accountId,
        string instanceId,
        CancellationToken cancellationToken);

    Task RenewAsync(CancellationToken cancellationToken);

    Task ReleaseAsync(CancellationToken cancellationToken);
}
