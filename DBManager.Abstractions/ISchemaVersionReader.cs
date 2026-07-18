namespace DBManager.Abstractions;

/// <summary>One applied entry from <c>operations.schema_history</c> (section 26).</summary>
public sealed record SchemaVersionInfo
{
    public required string MigrationId { get; init; }
    public required string ProductVersion { get; init; }
}

/// <summary>Reads the applied migration history without depending on EF Core or Npgsql types.</summary>
public interface ISchemaVersionReader
{
    Task<IReadOnlyList<SchemaVersionInfo>> GetAppliedMigrationsAsync(CancellationToken cancellationToken);
}
