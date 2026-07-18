using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DBManager.Postgres;

/// <summary>
/// Design-time factory so <c>dotnet ef migrations add</c> can run without a host. Never used at
/// runtime; the live host builds <see cref="TradingHubDbContext"/> through DI with the configured
/// <see cref="PostgresPersistenceOptions.MigratorConnectionString"/>.
/// </summary>
public sealed class TradingHubDbContextFactory : IDesignTimeDbContextFactory<TradingHubDbContext>
{
    public TradingHubDbContext CreateDbContext(string[] args)
    {
        string connectionString = Environment.GetEnvironmentVariable("TRADINGHUB_MIGRATOR_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=tradinghub;Username=trading_migrator;Password=placeholder";

        DbContextOptionsBuilder<TradingHubDbContext> builder = new();
        builder.UseNpgsql(connectionString, npgsql => npgsql
            .MigrationsHistoryTable(PostgresSchemaVersionReader.MigrationsHistoryTableName, PostgresSchemaVersionReader.MigrationsHistorySchema));
        builder.UseSnakeCaseNamingConvention();

        return new TradingHubDbContext(builder.Options);
    }
}
