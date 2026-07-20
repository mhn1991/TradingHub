using DBManager.Postgres;
using Microsoft.EntityFrameworkCore;

namespace TradingHub.Persistence.Postgres.Bootstrap;

public sealed class StandaloneTradingHubContextFactory(string connectionString)
    : IDbContextFactory<TradingHubDbContext>
{
    private readonly DbContextOptions<TradingHubDbContext> _options =
        new DbContextOptionsBuilder<TradingHubDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(
                PostgresSchemaVersionReader.MigrationsHistoryTableName,
                PostgresSchemaVersionReader.MigrationsHistorySchema))
            .UseSnakeCaseNamingConvention()
            .Options;

    public TradingHubDbContext CreateDbContext() => new(_options);

    public Task<TradingHubDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateDbContext());
}
