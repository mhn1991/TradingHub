using DBManager.Abstractions;
using DBManager.Abstractions.Analytics;
using DBManager.Abstractions.Config;
using DBManager.Abstractions.Decision;
using DBManager.Abstractions.Execution;
using DBManager.Abstractions.Management;
using DBManager.Abstractions.Operations;
using DBManager.Abstractions.Research;
using DBManager.Abstractions.Risk;
using DBManager.Postgres.Analytics;
using DBManager.Postgres.Config;
using DBManager.Postgres.Decision;
using DBManager.Postgres.Execution;
using DBManager.Postgres.Management;
using DBManager.Postgres.Operations;
using DBManager.Postgres.Operations.Backup;
using DBManager.Postgres.Reference;
using DBManager.Postgres.Research;
using DBManager.Postgres.Risk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DBManager.Postgres;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers one <see cref="NpgsqlDataSource"/> singleton per <see cref="PersistenceLane"/>
    /// (section 14.1), the pooled EF Core migrations context, and the health/schema-version
    /// abstractions. Each data source owns its own pool and application name so lanes never
    /// share a connection (section 9.1).
    /// </summary>
    public static IServiceCollection AddDBManagerPostgres(
        this IServiceCollection services,
        Action<PostgresPersistenceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        PostgresPersistenceOptions options = new()
        {
            MigratorConnectionString = string.Empty,
            CriticalConnectionString = string.Empty,
            ReadConnectionString = string.Empty,
            ResearchConnectionString = string.Empty,
            LeaseConnectionString = string.Empty
        };
        configure(options);
        options.Validate();

        services.AddSingleton(options);

        services.AddKeyedSingleton(PersistenceLane.Critical,
            (_, _) => CreateDataSource(options.CriticalConnectionString, "TradingCritical"));
        services.AddKeyedSingleton(PersistenceLane.Read,
            (_, _) => CreateDataSource(options.ReadConnectionString, "TradingRead"));
        services.AddKeyedSingleton(PersistenceLane.Research,
            (_, _) => CreateDataSource(options.ResearchConnectionString, "ResearchBulk"));
        services.AddKeyedSingleton(PersistenceLane.Lease,
            (_, _) => CreateDataSource(options.LeaseConnectionString, "LeaseConnection"));

        services.AddPooledDbContextFactory<TradingHubDbContext>(builder =>
        {
            builder.UseNpgsql(
                options.MigratorConnectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    PostgresSchemaVersionReader.MigrationsHistoryTableName,
                    PostgresSchemaVersionReader.MigrationsHistorySchema));
            builder.UseSnakeCaseNamingConvention();
        });

        services.AddSingleton<IPersistenceHealthCheck, PostgresPersistenceHealthCheck>();
        services.AddSingleton<ISchemaVersionReader, PostgresSchemaVersionReader>();
        services.AddScoped<IPolicyConfigurationStore, PolicyConfigurationStore>();
        services.AddScoped<IReferenceDataStore, ReferenceDataStore>();

        services.AddSingleton<OperationalBatchWriter>();
        services.AddSingleton<IOperationalDiagnosticsWriter>(sp => sp.GetRequiredService<OperationalBatchWriter>());
        services.AddSingleton<IOperationalBatchWriterDiagnostics>(sp => sp.GetRequiredService<OperationalBatchWriter>());
        services.AddScoped<IAgentDecisionReadStore, PostgresAgentDecisionReadStore>();
        services.AddScoped<IRiskStore, RiskStore>();
        services.AddScoped<IExecutionStore, ExecutionStore>();
        services.AddScoped<IReconciliationStore, ReconciliationStore>();
        services.AddScoped<IManagementStore, ManagementStore>();
        services.AddScoped<IAccountLeaseStore, AccountLeaseStore>();
        services.AddScoped<IResearchStore, ResearchStore>();
        services.AddScoped<IAnalyticsStore, AnalyticsStore>();
        services.AddScoped<IResearchBulkStore, ResearchBulkStore>();
        services.AddScoped<IBackupStore, BackupStore>();
        services.AddScoped<BackupRunner>();

        return services;
    }

    private static NpgsqlDataSource CreateDataSource(string connectionString, string applicationName)
    {
        NpgsqlDataSourceBuilder builder = new(connectionString)
        {
            ConnectionStringBuilder = { ApplicationName = applicationName }
        };
        return builder.Build();
    }
}
