namespace DBManager.Abstractions.Management;

/// <summary>
/// A held account ownership lease. Disposing releases the underlying session-level advisory lock
/// and marks the visible projection <see cref="AccountLeaseStatus.Released"/>. The dedicated
/// connection backing the lock is held open for the lease's lifetime (section 17: "A dedicated
/// account-lease connection stays open and is not returned to the normal pool while ownership is
/// held").
/// </summary>
public interface IAccountLease : IAsyncDisposable
{
    Guid BrokerAccountId { get; }
    long LeaseGeneration { get; }
}

public interface IAccountLeaseStore
{
    /// <summary>
    /// Acquires exclusive ownership of a broker account via a PostgreSQL session-level advisory
    /// lock (section 7.8: "Use a dedicated session-level PostgreSQL advisory lock as the actual
    /// process-exclusion mechanism and this table as the visible lease projection"). Returns
    /// <c>null</c> if another host instance already holds it.
    /// </summary>
    Task<IAccountLease?> TryAcquireLeaseAsync(
        Guid brokerAccountId, string hostInstanceId, CancellationToken cancellationToken);
}
