namespace DBManager.Abstractions.Bootstrap;

public enum PersistenceMode
{
    [Obsolete("Temporary migration compatibility only. Production hosts must use PostgreSQL.")]
    FileOnly,
    [Obsolete("Temporary migration validation only. Production hosts must use PostgresOnly.")]
    DualWrite,
    PostgresPrimary,
    PostgresOnly
}

/// <summary>
/// Safe process-bootstrap settings. Business configuration and secrets are deliberately absent.
/// </summary>
public sealed record PersistenceBootstrapOptions
{
    public const string SectionName = "Persistence";

    public PersistenceMode Mode { get; init; } = PersistenceMode.PostgresOnly;
    public bool RequireCurrentSchema { get; init; } = true;
    public bool AllowDevelopmentMigrations { get; init; }
    public string EmergencySpoolDirectory { get; init; } = ".state/emergency-spool";
}
