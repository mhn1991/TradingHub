namespace DBManager.Postgres;

/// <summary>
/// Connection strings for each persistence lane (section 14.1). Lanes are independently
/// configurable because they differ by pool sizing and, in production, by PostgreSQL role
/// (section 25) rather than only by pool size.
/// </summary>
public sealed class PostgresPersistenceOptions
{
    /// <summary>Used only for applying EF Core migrations; not used on the live host startup path.</summary>
    public required string MigratorConnectionString { get; set; }

    /// <summary>Sequential-per-account critical writes (section 9.2, Lane A).</summary>
    public required string CriticalConnectionString { get; set; }

    /// <summary>Dashboard and moderate-volume reads.</summary>
    public required string ReadConnectionString { get; set; }

    /// <summary>Low-priority bulk research import/export.</summary>
    public required string ResearchConnectionString { get; set; }

    /// <summary>Dedicated connection backing an account ownership advisory lock/lease.</summary>
    public required string LeaseConnectionString { get; set; }

    public void Validate()
    {
        foreach ((string name, string? value) in new (string, string?)[]
                 {
                     (nameof(MigratorConnectionString), MigratorConnectionString),
                     (nameof(CriticalConnectionString), CriticalConnectionString),
                     (nameof(ReadConnectionString), ReadConnectionString),
                     (nameof(ResearchConnectionString), ResearchConnectionString),
                     (nameof(LeaseConnectionString), LeaseConnectionString)
                 })
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException($"{nameof(PostgresPersistenceOptions)}.{name} must be configured.");
        }
    }
}
