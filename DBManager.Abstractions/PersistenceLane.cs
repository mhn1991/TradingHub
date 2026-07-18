namespace DBManager.Abstractions;

/// <summary>
/// Identifies which named connection/data-source lane a persistence operation belongs to
/// (section 14.1 and section 17 of the PostgreSQL persistence architecture). Each lane maps to
/// its own <c>NpgsqlDataSource</c> with independent pool sizing and priority.
/// </summary>
public enum PersistenceLane
{
    /// <summary>Sequential-per-account critical writes: reservations, orders, broker events.</summary>
    Critical,

    /// <summary>Dashboard and moderate-volume read queries.</summary>
    Read,

    /// <summary>Low-priority bulk research import/export (COPY workers).</summary>
    Research,

    /// <summary>Dedicated long-lived connection backing an account ownership lease.</summary>
    Lease
}
