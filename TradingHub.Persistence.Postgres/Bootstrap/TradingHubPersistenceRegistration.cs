using DBManager.Abstractions;
using DBManager.Abstractions.Bootstrap;
using DBManager.Postgres;
using DBManager.Postgres.Security;
using Agent.Factories;
using LiveTrading.AccountLease;
using LiveTrading.ManualApproval;
using LiveTrading.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PortfolioManager.Risk;
using Simulator.Calibration;
using Simulator.Experiments.Persistence;
using Simulator.Jobs;
using TradingHub.Persistence.Postgres.Calibration;
using TradingHub.Persistence.Postgres.Config;
using TradingHub.Persistence.Postgres.Live;
using TradingHub.Persistence.Postgres.Simulation;
using TradingPolicies;

namespace TradingHub.Persistence.Postgres.Bootstrap;

public sealed record TradingHubPersistenceRegistrationOptions
{
    public required BrokerCredentialDatabaseConfiguration Database { get; init; }
    public required string RepositoryRoot { get; init; }
    public PersistenceMode Mode { get; init; } = PersistenceMode.PostgresOnly;
    public string LegacySimulationJobsDirectory { get; init; } = ".cache/simulation-jobs";
    public string LegacyLiveDirectory { get; init; } = ".state/live-trading";
    public Guid DeploymentId { get; init; }
}

public static class TradingHubPersistenceRegistration
{
    public static IServiceCollection AddTradingHubPersistence(
        this IServiceCollection services,
        TradingHubPersistenceRegistrationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        string connectionString = options.Database.ConnectionString;
        services.AddDBManagerPostgres(postgres =>
        {
            postgres.MigratorConnectionString = connectionString;
            postgres.CriticalConnectionString = connectionString;
            postgres.ReadConnectionString = connectionString;
            postgres.ResearchConnectionString = connectionString;
            postgres.LeaseConnectionString = connectionString;
        });
        services.AddEncryptedDatabaseSecretProvider(options.Database.ResolveKeyFile(options.RepositoryRoot));

        services.AddSingleton<PostgresSimulationJobRepository>();
        services.AddSingleton<PostgresSimulationStrategyProfileStore>();
        services.AddSingleton<ISimulationStrategyProfileStore>(provider =>
            provider.GetRequiredService<PostgresSimulationStrategyProfileStore>());
        services.AddSingleton<PostgresSimulationExperimentRepository>();
        services.AddSingleton<ISimulationExperimentRepository>(provider =>
            provider.GetRequiredService<PostgresSimulationExperimentRepository>());
        services.AddSingleton<PostgresCalibrationArtifactRepository>();
        services.AddSingleton<ICalibrationArtifactRepository>(provider =>
            provider.GetRequiredService<PostgresCalibrationArtifactRepository>());
        services.AddSingleton<PostgresTradingPolicyProfileStore>();
        services.AddSingleton<ITradingPolicyProfileStore>(provider =>
            provider.GetRequiredService<PostgresTradingPolicyProfileStore>());
        services.AddSingleton<ITradingAgentCatalog>(_ => TradingAgentFactory.SharedCatalog);
        services.AddSingleton<PostgresAgentPackageResolver>();
        services.AddSingleton<IAgentPackageResolver>(provider =>
            provider.GetRequiredService<PostgresAgentPackageResolver>());
        services.AddSingleton<PostgresCalibrationBundleApprovalStore>();
        services.AddSingleton<ICalibrationBundleApprovalStore>(provider =>
            provider.GetRequiredService<PostgresCalibrationBundleApprovalStore>());
        services.AddSingleton<PostgresLiveTradingPersistence>(provider => new PostgresLiveTradingPersistence(
            provider.GetRequiredService<IDbContextFactory<TradingHubDbContext>>(),
            provider.GetRequiredService<TimeProvider>(),
            options.DeploymentId));
        services.AddSingleton<PostgresTradingAccountLease>();
        // Dashboard and other non-execution hosts still resolve the manual-approval store.
        // LiveTradingHost pre-registers its policy-configured reservation book, which wins here.
        services.TryAddSingleton<IPortfolioReservationBook>(_ => new PortfolioReservationBook());
        services.AddSingleton<PostgresManualApprovalStore>();

#pragma warning disable CS0618
        switch (options.Mode)
        {
            case PersistenceMode.FileOnly:
                services.AddSingleton<ISimulationJobRepository>(_ =>
                    new FileSimulationJobRepository(Path.GetFullPath(options.LegacySimulationJobsDirectory)));
                services.AddSingleton<ITradingAccountLease>(provider => new FileTradingAccountLease(
                    new AccountLeaseOptions
                    {
                        Directory = Path.Combine(Path.GetFullPath(options.LegacyLiveDirectory), "leases")
                    },
                    provider.GetRequiredService<TimeProvider>()));
                services.AddSingleton<IManualApprovalStore, ManualApprovalStore>();
                services.AddSingleton<ILiveTradingPersistence>(provider => new FileLiveTradingPersistence(
                    new LiveTradingPersistenceOptions
                    {
                        RootDirectory = Path.GetFullPath(options.LegacyLiveDirectory)
                    },
                    provider.GetRequiredService<TimeProvider>()));
                break;
            case PersistenceMode.DualWrite:
                services.AddSingleton<ISimulationJobRepository>(provider => new DualWriteSimulationJobRepository(
                    provider.GetRequiredService<PostgresSimulationJobRepository>(),
                    new FileSimulationJobRepository(Path.GetFullPath(options.LegacySimulationJobsDirectory)),
                    postgresReads: false));
                services.AddSingleton<ILiveTradingPersistence>(provider => new DualWriteLiveTradingPersistence(
                    provider.GetRequiredService<PostgresLiveTradingPersistence>(),
                    new FileLiveTradingPersistence(
                        new LiveTradingPersistenceOptions { RootDirectory = Path.GetFullPath(options.LegacyLiveDirectory) },
                        provider.GetRequiredService<TimeProvider>()),
                    postgresReads: false));
                services.AddSingleton<ITradingAccountLease>(provider =>
                    provider.GetRequiredService<PostgresTradingAccountLease>());
                services.AddSingleton<IManualApprovalStore>(provider =>
                    provider.GetRequiredService<PostgresManualApprovalStore>());
                break;
            case PersistenceMode.PostgresPrimary:
            case PersistenceMode.PostgresOnly:
                services.AddSingleton<ISimulationJobRepository>(provider =>
                    provider.GetRequiredService<PostgresSimulationJobRepository>());
                services.AddSingleton<ILiveTradingPersistence>(provider =>
                    provider.GetRequiredService<PostgresLiveTradingPersistence>());
                services.AddSingleton<ITradingAccountLease>(provider =>
                    provider.GetRequiredService<PostgresTradingAccountLease>());
                services.AddSingleton<IManualApprovalStore>(provider =>
                    provider.GetRequiredService<PostgresManualApprovalStore>());
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(options.Mode));
        }
#pragma warning restore CS0618
        return services;
    }

    public static PersistenceMode ParseMode(string? value) =>
        Enum.TryParse(value, ignoreCase: true, out PersistenceMode mode)
            ? mode
            : PersistenceMode.PostgresOnly;

    public static async Task ValidatePostgresStartupAsync(
        IServiceProvider services,
        bool requireCurrentSchema,
        CancellationToken cancellationToken = default)
    {
        using IServiceScope scope = services.CreateScope();
        IPersistenceHealthCheck healthCheck = scope.ServiceProvider.GetRequiredService<IPersistenceHealthCheck>();
        PersistenceHealthReport report = await healthCheck.CheckAsync(cancellationToken).ConfigureAwait(false);
        if (report.Status != PersistenceHealthStatus.Healthy)
            throw new InvalidOperationException("Critical PostgreSQL persistence is unavailable at startup.");
        if (!requireCurrentSchema) return;

        IDbContextFactory<TradingHubDbContext> factory = scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<TradingHubDbContext>>();
        await using TradingHubDbContext context = await factory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        string[] pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToArray();
        if (pending.Length > 0)
            throw new InvalidOperationException("The PostgreSQL schema is not current. Run DBManager.Cli migrate first.");
    }
}
