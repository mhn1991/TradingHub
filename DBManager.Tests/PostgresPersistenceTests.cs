using DBManager.Abstractions;
using DBManager.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NUnit.Framework;
using Testcontainers.PostgreSql;

namespace DBManager.Tests;

/// <summary>
/// Phase 0 acceptance (section 30): connection pooling works and an integration-test database
/// can be created automatically. Uses a real, disposable PostgreSQL container rather than mocks.
/// </summary>
[TestFixture]
public sealed class PostgresPersistenceTests
{
    private PostgreSqlContainer _container = null!;
    private ServiceProvider _services = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUpAsync()
    {
        _container = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("tradinghub_test")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();
        await _container.StartAsync();

        string connectionString = _container.GetConnectionString();

        ServiceCollection collection = new();
        collection.AddSingleton(TimeProvider.System);
        collection.AddDBManagerPostgres(options =>
        {
            options.MigratorConnectionString = connectionString;
            options.CriticalConnectionString = connectionString;
            options.ReadConnectionString = connectionString;
            options.ResearchConnectionString = connectionString;
            options.LeaseConnectionString = connectionString;
        });
        _services = collection.BuildServiceProvider();

        IDbContextFactory<TradingHubDbContext> contextFactory =
            _services.GetRequiredService<IDbContextFactory<TradingHubDbContext>>();
        await using TradingHubDbContext context = await contextFactory.CreateDbContextAsync();
        await context.Database.MigrateAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDownAsync()
    {
        await _services.DisposeAsync();
        await _container.DisposeAsync();
    }

    [Test]
    public async Task Migration_creates_the_operations_schema_history_table()
    {
        ISchemaVersionReader reader = _services.GetRequiredService<ISchemaVersionReader>();

        IReadOnlyList<SchemaVersionInfo> applied = await reader.GetAppliedMigrationsAsync(CancellationToken.None);

        Assert.That(applied, Has.Count.GreaterThanOrEqualTo(1));
        Assert.That(applied[0].MigrationId, Does.EndWith("_InitialSchemaHistory"));
    }

    [Test]
    public async Task Health_check_reports_healthy_against_the_critical_lane()
    {
        IPersistenceHealthCheck healthCheck = _services.GetRequiredService<IPersistenceHealthCheck>();

        PersistenceHealthReport report = await healthCheck.CheckAsync(CancellationToken.None);

        Assert.That(report.Status, Is.EqualTo(PersistenceHealthStatus.Healthy));
        Assert.That(report.Latency, Is.Not.Null);
    }

    [TestCase(PersistenceLane.Critical)]
    [TestCase(PersistenceLane.Read)]
    [TestCase(PersistenceLane.Research)]
    [TestCase(PersistenceLane.Lease)]
    public async Task Each_lane_has_an_independent_pooled_data_source(PersistenceLane lane)
    {
        NpgsqlDataSource dataSource = _services.GetRequiredKeyedService<NpgsqlDataSource>(lane);

        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync();
        await using NpgsqlCommand command = new("SELECT 1", connection);
        object? result = await command.ExecuteScalarAsync();

        Assert.That(result, Is.EqualTo(1));
    }

    [Test]
    public void Lane_data_sources_are_distinct_instances()
    {
        NpgsqlDataSource critical = _services.GetRequiredKeyedService<NpgsqlDataSource>(PersistenceLane.Critical);
        NpgsqlDataSource read = _services.GetRequiredKeyedService<NpgsqlDataSource>(PersistenceLane.Read);
        NpgsqlDataSource research = _services.GetRequiredKeyedService<NpgsqlDataSource>(PersistenceLane.Research);
        NpgsqlDataSource lease = _services.GetRequiredKeyedService<NpgsqlDataSource>(PersistenceLane.Lease);

        Assert.That(new object[] { critical, read, research, lease }.Distinct(), Has.Exactly(4).Items);
    }
}
