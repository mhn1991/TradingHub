using System.Net;
using System.Security.Cryptography;
using System.Text;
using Serilog;
using Brokers.Abstractions;
using Brokers.Oanda;
using Agent.Factories;
using ExecutionManager;
using LiveTrading.Account;
using LiveTrading.AccountLease;
using LiveTrading.Agents;
using LiveTrading.Calibration;
using LiveTrading.Configuration;
using LiveTrading.Execution;
using LiveTrading.Deployment;
using LiveTrading.Management;
using LiveTrading.ManualApproval;
using LiveTrading.MarketData;
using LiveTrading.Persistence;
using LiveTrading.Portfolio;
using LiveTrading.Reconciliation;
using LiveTrading.Registry;
using LiveTrading.Runtime;
using LiveTrading.Shadow;
using LiveTrading.Shadow.Outcomes;
using LiveTradingHost;
using LiveTradingHost.Api;
using LiveTradingHost.Configuration;
using LiveTradingHost.Observability;
using LiveTradingHost.Deployment;
using LiveTradingHost.Status;
using DBManager.Abstractions.Bootstrap;
using DBManager.Abstractions.Config;
using DBManager.Abstractions.Credentials;
using DBManager.Postgres.Config;
using DBManager.Postgres.Reference;
using DBManager.Postgres.Security;
using LiveTrading.Oanda;
using Microsoft.Extensions.Options;
using PortfolioManager.Risk;
using QuantResearch.Training.Pipeline;
using RiskManager.Safety;
using Simulator.Calibration;
using Simulator.Jobs;
using Simulator.Services;
using TradingCore.Pipeline;
using TradingHub.Persistence.Postgres.Bootstrap;
using TradingPolicies;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
// Durable logging: the default host-provided console logger only survives as long as the
// console session (or whatever process manager captures its stdout). File defaults live here,
// not just in appsettings.json's Logging section, so a normal checkout still gets a durable log
// even before an operator configures anything - matches how safety-critical defaults elsewhere
// in this host (e.g. LiveExecution:BrokerWritesEnabled) are meant to be safe out of the box.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .ReadFrom.Configuration(builder.Configuration)
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(".state", "live-trading", "logs", "live-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        shared: true)
    .CreateLogger();
builder.Host.UseSerilog();
BrokerCredentialDatabaseConfiguration? databaseConfiguration =
    BrokerCredentialDatabaseConfiguration.TryLoad(builder.Environment.ContentRootPath, out string repositoryRoot);
if (databaseConfiguration is null)
    throw new InvalidOperationException("PostgreSQL runtime configuration is required. Run DBManager.Cli migrate and seed-reference first.");

IBrokerCredentialStore brokerCredentialStore = databaseConfiguration.OpenStore(repositoryRoot);
var contextFactory = new StandaloneTradingHubContextFactory(databaseConfiguration.ConnectionString);
var runtimeProfileStore = new RuntimeProfileStore(contextFactory, TimeProvider.System);
RuntimeProfileRevisionDetail liveHostProfile = await runtimeProfileStore.GetApprovedRevisionAsync(
    RuntimeProfileKind.LiveHost,
    "default",
    CancellationToken.None) ?? throw new InvalidOperationException(
    "No approved LiveHost/default runtime profile exists in PostgreSQL. Run DBManager.Cli seed-reference first.");
await using var liveHostProfileStream = new MemoryStream(Encoding.UTF8.GetBytes(liveHostProfile.SettingsJson));
IConfiguration liveHostConfiguration = new ConfigurationBuilder()
    .AddJsonStream(liveHostProfileStream)
    .Build();
var brokerConfigurationStore = new BrokerRuntimeConfigurationStore(contextFactory);
ResolvedBrokerRuntimeConfiguration oandaRuntime =
    await brokerConfigurationStore.GetActiveAsync("OANDA", "DEMO", CancellationToken.None) ??
    await brokerConfigurationStore.GetActiveAsync("OANDA", "LIVE", CancellationToken.None) ??
    throw new InvalidOperationException("No enabled OANDA runtime configuration exists in PostgreSQL.");
SecretReference accessTokenReference = oandaRuntime.CredentialReferences.Single(reference =>
    reference.Purpose == "access_token");
using SecretValue accessToken = await new EncryptedDatabaseSecretResolver(brokerCredentialStore)
    .ResolveAsync(accessTokenReference, CancellationToken.None);
LiveOandaOptions configuredOanda =
    liveHostConfiguration.GetSection(LiveOandaOptions.SectionName).Get<LiveOandaOptions>() ?? new();
LiveOandaOptions oandaOptions = configuredOanda with
{
    Enabled = true,
    Environment = oandaRuntime.Environment.IsLive
        ? BrokerEnvironment.Live
        : BrokerEnvironment.Demo,
    AccountId = oandaRuntime.Account?.ExternalAccountId
        ?? throw new InvalidOperationException("The active OANDA account setting has no external account id."),
    AccessToken = accessToken.Reveal(),
    RestBaseAddress = oandaRuntime.Endpoints.Single(endpoint => endpoint.Kind == BrokerEndpointKind.Rest).BaseAddress,
    StreamBaseAddress = oandaRuntime.Endpoints.Single(endpoint => endpoint.Kind == BrokerEndpointKind.Streaming).BaseAddress
};
string configuredUrls = liveHostConfiguration[$"{LiveHostRuntimeOptions.SectionName}:Urls"] ??
    "http://127.0.0.1:5088";
builder.WebHost.UseUrls(configuredUrls);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IOptions<LiveOandaOptions>>(Options.Create(oandaOptions));
builder.Services.Configure<LiveMarketUniverseOptions>(
    liveHostConfiguration.GetSection(LiveMarketUniverseOptions.SectionName));
builder.Services.Configure<AccountLeaseOptions>(
    liveHostConfiguration.GetSection(AccountLeaseOptions.SectionName));
builder.Services.Configure<LiveHostRuntimeOptions>(
    liveHostConfiguration.GetSection(LiveHostRuntimeOptions.SectionName));
builder.Services.Configure<LiveExecutionRuntimeOptions>(
    liveHostConfiguration.GetSection("LiveExecution"));
builder.Services.Configure<LiveAccountStateOptions>(
    liveHostConfiguration.GetSection("LiveAccount"));
builder.Services.Configure<LiveTradingPersistenceOptions>(
    liveHostConfiguration.GetSection("LivePersistence"));
builder.Services.Configure<LiveShadowOutcomeOptions>(
    liveHostConfiguration.GetSection("LiveShadowOutcomes"));

builder.Services.AddSingleton(services =>
    services.GetRequiredService<IOptions<AccountLeaseOptions>>().Value);
builder.Services.AddSingleton(services =>
{
    LiveHostRuntimeOptions options = services.GetRequiredService<IOptions<LiveHostRuntimeOptions>>().Value;
    options.Validate();
    return options;
});
builder.Services.AddSingleton(services =>
{
    LiveShadowOutcomeOptions options = services.GetRequiredService<IOptions<LiveShadowOutcomeOptions>>().Value;
    options.Validate();
    return options;
});
builder.Services.AddSingleton(services =>
{
    LiveExecutionRuntimeOptions options = services.GetRequiredService<IOptions<LiveExecutionRuntimeOptions>>().Value;
    options.Validate();
    return options;
});
builder.Services.AddSingleton(services =>
{
    LiveAccountStateOptions options = services.GetRequiredService<IOptions<LiveAccountStateOptions>>().Value;
    options.Validate();
    return options;
});

builder.Services.Configure<LiveCalibrationRepositoryOptions>(
    liveHostConfiguration.GetSection("LiveCalibration"));
builder.Services.Configure<Dictionary<string, LivePolicyBundleOptions>>(
    liveHostConfiguration.GetSection("LivePolicyBundles"));

builder.Services.AddSingleton<LivePolicyBundleFactory>();
builder.Services.AddSingleton<ILivePolicyRegistry, LivePolicyRegistry>();
builder.Services.AddSingleton<LiveAnalysisProfileRegistry>();
builder.Services.AddSingleton<ITradingAgentCatalog>(_ => TradingAgentCatalog.CreateDefault());
builder.Services.AddSingleton<MarketSubscriptionRegistry>();
builder.Services.AddSingleton<AnalysisRuntimeRegistry>();
builder.Services.AddSingleton<AgentRuntimeRegistry>();
builder.Services.AddSingleton<ExecutionOwnershipRegistry>();
builder.Services.AddSingleton<PositionManagementRegistry>();
builder.Services.AddSingleton(services => new HostInstanceIdentity(
    services.GetRequiredService<LiveHostRuntimeOptions>().HostInstanceId));

// Automated calibration training (Phase 4): an isolated background job, never part of the live
// decision loop. It only ever produces a PendingReview CalibrationBundleCandidate - promotion
// and activation stay separate, explicit, human actions.
builder.Services.Configure<List<CalibrationRetrainingPolicy>>(
    liveHostConfiguration.GetSection("CalibrationRetraining"));
builder.Services.AddSingleton(services => new BacktestApplicationService(
    services.GetRequiredService<ISimulationJobRepository>(),
    new BacktestApplicationServiceOptions
    {
        MaxConcurrentJobs = 1,
        QueueCapacity = 4,
        CredentialResolver = async (request, cancellationToken) =>
            {
                string environment = request.Environment == Brokers.Abstractions.BrokerEnvironment.Live
                    ? "LIVE"
                    : "DEMO";
                string broker = request.Runtime.SourceKind == Simulator.MarketData.HistoricalDataSourceKind.BinanceCandles
                    ? "BINANCE"
                    : "OANDA";
                BrokerCredential? credential = await brokerCredentialStore.GetAsync(
                    broker,
                    broker == "BINANCE" && environment == "DEMO" ? "TESTNET" : environment,
                    cancellationToken: cancellationToken);
                return credential is null
                    ? null
                    : new HistoricalBrokerCredentials(
                        credential.AccountId,
                        credential.AccessToken,
                        credential.ApiKey,
                        credential.SecretKey);
            }
    }));
builder.Services.AddSingleton<IBacktestApplicationService>(services =>
    services.GetRequiredService<BacktestApplicationService>());
builder.Services.AddSingleton(services => new CalibrationTrainingPipeline(
    services.GetRequiredService<IBacktestApplicationService>(),
    services.GetRequiredService<ICalibrationArtifactRepository>()));
builder.Services.AddSingleton(services => new CalibrationBundleWorkflow(
    services.GetRequiredService<CalibrationTrainingPipeline>(),
    services.GetRequiredService<ICalibrationArtifactRepository>(),
    services.GetRequiredService<ICalibrationBundleApprovalStore>()));
builder.Services.AddSingleton<ILiveCalibrationTrainingScheduler>(services => new LiveCalibrationTrainingScheduler(
    services.GetRequiredService<IOptions<List<CalibrationRetrainingPolicy>>>().Value,
    services.GetRequiredService<CalibrationBundleWorkflow>(),
    services.GetRequiredService<ITradingPolicyProfileStore>(),
    services.GetRequiredService<TimeProvider>(),
    services.GetRequiredService<ILogger<LiveCalibrationTrainingScheduler>>()));

// Agents produce intent only. Even executable Agent assignments are built through the shadow
// execution coordinator; the account-wide runtime is the sole component allowed to write OANDA.
builder.Services.AddSingleton<IStrategyDecisionPipelineFactory>(
    _ => new StrategyDecisionPipelineFactory(new ShadowExecutionCoordinator()));

builder.Services.Configure<LiveDecisionEpochCoordinatorOptions>(
    liveHostConfiguration.GetSection("LiveDecisionEpoch"));
builder.Services.AddSingleton(services =>
{
    LiveMarketUniverseOptions marketOptions = services.GetRequiredService<IOptions<LiveMarketUniverseOptions>>().Value;
    LiveDecisionEpochCoordinatorOptions epochOptions =
        services.GetRequiredService<IOptions<LiveDecisionEpochCoordinatorOptions>>().Value;
    var instruments = marketOptions.Markets
        .Where(market => market.Enabled)
        .Select(market => market.Instrument)
        .ToList();
    return new LiveDecisionEpochCoordinator(
        instruments,
        services.GetRequiredService<TimeProvider>(),
        epochOptions,
        services.GetRequiredService<ILoggerFactory>().CreateLogger<LiveDecisionEpochCoordinator>());
});

builder.Services.AddSingleton<OandaBrokerClient>(services =>
{
    LiveOandaOptions oanda = services.GetRequiredService<IOptions<LiveOandaOptions>>().Value;
    LiveExecutionRuntimeOptions execution = services.GetRequiredService<LiveExecutionRuntimeOptions>();
    oanda.Validate();
    if (execution.BrokerWritesEnabled && oanda.Environment != Brokers.Abstractions.BrokerEnvironment.Demo)
    {
        throw new InvalidOperationException(
            "Broker writes are permitted only for OANDA Practice/Demo in this release.");
    }
    return new OandaBrokerClient(oanda.ToOandaOptions());
});
builder.Services.AddSingleton<IBrokerClient>(services => services.GetRequiredService<OandaBrokerClient>());
builder.Services.AddSingleton<ITradingBrokerClient>(services => services.GetRequiredService<OandaBrokerClient>());

builder.Services.AddSingleton<OandaInstrumentMap>(services =>
{
    LiveOandaOptions options = services.GetRequiredService<IOptions<LiveOandaOptions>>().Value;
    return new OandaInstrumentMap(options.InstrumentMappings);
});
builder.Services.AddSingleton<ICompletedCandleProvider>(services =>
    new OandaCompletedCandleProvider(services.GetRequiredService<IBrokerClient>()));
builder.Services.AddSingleton<ILiveQuoteStream>(services =>
    new OandaLiveQuoteStream(
        services.GetRequiredService<OandaBrokerClient>(),
        services.GetRequiredService<OandaInstrumentMap>(),
        services.GetRequiredService<TimeProvider>()));

builder.Services.AddSingleton(services =>
{
    LiveHostRuntimeOptions hostOptions = services.GetRequiredService<LiveHostRuntimeOptions>();
    return new AgentSupervisor(
        services.GetRequiredService<IBrokerClient>(),
        services.GetRequiredService<LiveDecisionEpochCoordinator>(),
        services.GetRequiredService<TimeProvider>(),
        services.GetRequiredService<ILoggerFactory>().CreateLogger<AgentSupervisor>(),
        hostOptions.MaxConcurrentAgentEvaluations,
        hostOptions.AgentEvaluationTimeout);
});

builder.Services.AddSingleton<ITradingSafetyController>(services =>
{
    LiveMarketUniverseOptions marketOptions = services.GetRequiredService<IOptions<LiveMarketUniverseOptions>>().Value;
    Dictionary<string, LivePolicyBundleOptions> bundles =
        services.GetRequiredService<IOptions<Dictionary<string, LivePolicyBundleOptions>>>().Value;
    return new TradingSafetyController(LiveSharedPolicyOptionsResolver.ResolveSafety(marketOptions, bundles));
});
builder.Services.AddSingleton<IPortfolioReservationBook>(services =>
{
    LiveMarketUniverseOptions marketOptions = services.GetRequiredService<IOptions<LiveMarketUniverseOptions>>().Value;
    Dictionary<string, LivePolicyBundleOptions> bundles =
        services.GetRequiredService<IOptions<Dictionary<string, LivePolicyBundleOptions>>>().Value;
    return new PortfolioReservationBook(
        LiveSharedPolicyOptionsResolver.ResolvePortfolioRisk(marketOptions, bundles));
});
byte[] deploymentHash = SHA256.HashData(Encoding.UTF8.GetBytes(
    $"OANDA/{oandaRuntime.Environment.EnvironmentCode}/{oandaOptions.AccountId}"));
Guid deploymentId = new(deploymentHash.AsSpan(0, 16));
builder.Services.AddSingleton(new LiveRuntimeIdentity(deploymentId, Guid.NewGuid()));
builder.Services.AddTradingHubPersistence(new TradingHubPersistenceRegistrationOptions
{
    Database = databaseConfiguration,
    RepositoryRoot = repositoryRoot,
    Mode = PersistenceMode.PostgresOnly,
    LegacySimulationJobsDirectory = Path.Combine(repositoryRoot, ".cache", "calibration-training-jobs"),
    LegacyLiveDirectory = Path.Combine(repositoryRoot, ".state", "live-trading"),
    DeploymentId = deploymentId
});
builder.Services.AddSingleton<ILiveOpportunityCoordinator, LiveOpportunityCoordinator>();
builder.Services.AddSingleton<ILiveOrderPositionRegistry, LiveOrderPositionRegistry>();
builder.Services.AddSingleton<IExecutionCoordinator>(services =>
    new ExecutionCoordinator(safety: services.GetRequiredService<ITradingSafetyController>()));
builder.Services.AddSingleton<ILiveExecutionGateway, LiveExecutionGateway>();
builder.Services.AddSingleton<ILiveBrokerReconciler, LiveBrokerReconciler>();

builder.Services.AddSingleton<ILiveAccountStateService, LiveAccountStateService>();
builder.Services.AddSingleton<ILiveShadowOutcomeService, LiveShadowOutcomeService>();
builder.Services.AddSingleton<ILiveTradingRuntimeCoordinator, LiveTradingRuntimeCoordinator>();
builder.Services.AddSingleton<ILivePositionManagementService, LivePositionManagementService>();
builder.Services.AddSingleton<ILiveBrokerEventProcessor, LiveBrokerEventProcessor>();

builder.Services.AddSingleton<LiveEngineState>();
builder.Services.AddSingleton<LiveStatusRealtimePublisher>();
builder.Services.AddSingleton<LiveRuntimeObservability>();
builder.Services.AddSingleton<LiveEngineHostedService>();
builder.Services.AddHostedService(services => services.GetRequiredService<LiveEngineHostedService>());
builder.Services.AddScoped<ILiveDeploymentPreflightService, LiveDeploymentPreflightService>();
builder.Services.AddScoped<ILiveDeploymentOrchestrator, LiveDeploymentOrchestrator>();
builder.Services.AddSingleton<ILiveDynamicAgentRuntimeService, LiveDynamicAgentRuntimeService>();
builder.Services.AddHostedService<LiveDeploymentCommandProcessor>();

builder.Services.AddSignalR();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .SetIsOriginAllowed(origin =>
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri))
            return false;
        return (uri.Scheme is "http" or "https") &&
            (uri.Host is "localhost" or "127.0.0.1");
    })
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

WebApplication app = builder.Build();
await TradingHubPersistenceRegistration.ValidatePostgresStartupAsync(
    app.Services,
    requireCurrentSchema: true);
LiveHostRuntimeOptions hostOptions = app.Services.GetRequiredService<LiveHostRuntimeOptions>();
app.UseCors();
app.Use(async (context, next) =>
{
    bool lifecycleMutation = !HttpMethods.IsGet(context.Request.Method)
        && !HttpMethods.IsHead(context.Request.Method)
        && (context.Request.Path.StartsWithSegments("/api/live/deployments")
            || context.Request.Path.StartsWithSegments("/api/live/deployment-agents"))
        && context.Request.Path.Value?.EndsWith("/preflight", StringComparison.OrdinalIgnoreCase) != true;
    bool controlRequest = lifecycleMutation || context.Request.Path.StartsWithSegments("/api/live/control") ||
        context.Request.Path.Value?.Contains("/approve", StringComparison.OrdinalIgnoreCase) == true ||
        context.Request.Path.Value?.Contains("/reject", StringComparison.OrdinalIgnoreCase) == true ||
        context.Request.Path.Value?.Contains("/reduce", StringComparison.OrdinalIgnoreCase) == true ||
        context.Request.Path.Value?.Contains("/close", StringComparison.OrdinalIgnoreCase) == true ||
        context.Request.Path.Value?.Contains("/stop", StringComparison.OrdinalIgnoreCase) == true;
    if (!controlRequest)
    {
        await next().ConfigureAwait(false);
        return;
    }

    IPAddress? remote = context.Connection.RemoteIpAddress;
    if (hostOptions.RequireLoopback && (remote is null || !IPAddress.IsLoopback(remote)))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("Live trading control is restricted to loopback.").ConfigureAwait(false);
        return;
    }
    if (!hostOptions.RequireLoopback && !context.Request.IsHttps)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("Remote live trading control requires HTTPS.").ConfigureAwait(false);
        return;
    }
    if (!string.IsNullOrWhiteSpace(hostOptions.ControlToken))
    {
        string supplied = context.Request.Headers["X-Live-Control-Token"].ToString();
        byte[] expected = Encoding.UTF8.GetBytes(hostOptions.ControlToken);
        byte[] actual = Encoding.UTF8.GetBytes(supplied);
        if (expected.Length != actual.Length || !CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("A valid live control token is required.").ConfigureAwait(false);
            return;
        }
    }
    await next().ConfigureAwait(false);
});

app.MapLiveStatusEndpoints();
app.MapLiveDeploymentEndpoints();
app.MapTradingReportEndpoints();
app.MapHub<LiveStatusHub>("/hubs/live");
try
{
    app.Run();
}
finally
{
    Log.CloseAndFlush();
}
