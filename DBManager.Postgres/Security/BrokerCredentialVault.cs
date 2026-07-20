using DBManager.Abstractions.Credentials;
using Microsoft.EntityFrameworkCore;

namespace DBManager.Postgres.Security;

public static class BrokerCredentialVault
{
    public static IBrokerCredentialStore Open(string connectionString, string keyFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        DbContextOptionsBuilder<TradingHubDbContext> builder = new();
        builder.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(
            PostgresSchemaVersionReader.MigrationsHistoryTableName,
            PostgresSchemaVersionReader.MigrationsHistorySchema));
        builder.UseSnakeCaseNamingConvention();
        return new BrokerCredentialStore(new StandaloneDbContextFactory(builder.Options), keyFile);
    }

    public static async Task MigrateAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        DbContextOptionsBuilder<TradingHubDbContext> builder = new();
        builder.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(
            PostgresSchemaVersionReader.MigrationsHistoryTableName,
            PostgresSchemaVersionReader.MigrationsHistorySchema));
        builder.UseSnakeCaseNamingConvention();
        await using TradingHubDbContext context = new(builder.Options);
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class StandaloneDbContextFactory(DbContextOptions<TradingHubDbContext> options)
        : IDbContextFactory<TradingHubDbContext>
    {
        public TradingHubDbContext CreateDbContext() => new(options);
    }
}
