using DBManager.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace DBManager.Postgres;

/// <summary>
/// Reads applied migrations from <c>operations.schema_history</c>, the EF Core migrations
/// history table redirected into the <c>operations</c> schema (section 26).
/// </summary>
public sealed class PostgresSchemaVersionReader(IDbContextFactory<TradingHubDbContext> contextFactory)
    : ISchemaVersionReader
{
    public const string MigrationsHistoryTableName = "schema_history";
    public const string MigrationsHistorySchema = "operations";

    public async Task<IReadOnlyList<SchemaVersionInfo>> GetAppliedMigrationsAsync(
        CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        List<HistoryRow> rows = await context.Database
            .SqlQueryRaw<HistoryRow>(
                $"SELECT migration_id AS \"MigrationId\", product_version AS \"ProductVersion\" " +
                $"FROM {MigrationsHistorySchema}.{MigrationsHistoryTableName} ORDER BY migration_id")
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .Select(row => new SchemaVersionInfo { MigrationId = row.MigrationId, ProductVersion = row.ProductVersion })
            .ToArray();
    }

    private sealed class HistoryRow
    {
        public string MigrationId { get; init; } = string.Empty;
        public string ProductVersion { get; init; } = string.Empty;
    }
}
