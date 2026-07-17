using System.Net;
using System.Security.Cryptography;
using System.Text;
using Brokers.Abstractions;
using Brokers.Oanda;
using ExecutionManager;
using LiveTrading.Account;
using LiveTrading.AccountLease;
using LiveTrading.Agents;
using LiveTrading.Calibration;
using LiveTrading.Configuration;
using LiveTrading.Execution;
using LiveTrading.Management;
using LiveTrading.ManualApproval;
using LiveTrading.MarketData;
using LiveTrading.Persistence;
using LiveTrading.Portfolio;
using LiveTrading.Reconciliation;
using LiveTrading.Registry;
using LiveTrading.Runtime;
using LiveTrading.Shadow;
using LiveTradingHost;
using LiveTradingHost.Api;
using LiveTradingHost.Configuration;
using LiveTradingHost.Status;
using LiveTrading.Oanda;
using Microsoft.Extensions.Options;
using PortfolioManager.Risk;
using QuantResearch.Training.Pipeline;
using RiskManager.Safety;
using Simulator.Calibration;
using Simulator.Jobs;
using Simulator.Services;
using TradingCore.Pipeline;
using TradingPolicies;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
string configuredUrls = builder.Configuration[$"{LiveHostRuntimeOptions.SectionName}:Urls"] ??
    "http://127.0.0.1:5088";
builder.WebHost.UseUrls(configuredUrls);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<LiveOandaOptions>(
    builder.Configuration.GetSection(LiveOandaOptions.SectionName));
builder.Services.Configure<LiveMarketUniverseOptions>(
    builder.Configuration.GetSection(LiveMarketUniverseOptions.SectionName));
builder.Services.Configure<AccountLeaseOptions>(
    builder.Configuration.GetSection(AccountLeaseOptions.SectionName));
builder.Services.Configure<LiveHostRuntimeOptions>(
    builder.Configuration.GetSection(LiveHostRuntimeOptions.SectionName));
builder.Services.Configure<LiveExecutionRuntimeOptions>(
    builder.Configuration.GetSection("LiveExecution"));
builder.Services.Configure<LiveAccountStateOptions>(
    builder.Configuration.GetSection("LiveAccount"));
builder.Services.Configure<LiveTradingPersistenceOptions>(
    builder.Configuration.GetSection("LivePersistence"));

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
    builder.Configuration.GetSection("LiveCalibration"));
builder.Services.Configure<Dictionary<string, LivePolicyBundleOptions>>(
    builder.Configuration.GetSection("LivePolicyBundles"));

builder.Services.AddSingleton<ICalibrationArtifactRepository>(services =>
{
    LiveCalibrationRepositoryOptions options = services.GetRequiredService<IOptions<LiveCalibrationRepositoryOptions>>().Value;
    return new FileCalibrationArtifactRepository(options.RootDirectory);
});
builder.Services.AddSingleton<LivePolicyBundleFactory>();
builder.Services.AddSingleton<ILivePolicyRegistry, LivePolicyRegistry>();

// Automated calibration training (Phase 4): an isolated background job, never part of the live
// decision loop. It only ever produces a PendingReview CalibrationBundleCandidate - promotion
// and activation stay separate, explicit, human actions.
builder.Services.Configure<List<CalibrationRetrainingPolicy>>(
    builder.Configuration.GetSection("CalibrationRetraining"));
builder.Services.AddSingleton<ITradingPolicyProfileStore>(services =>
{
    LiveCalibrationRepositoryOptions options = services.GetRequiredService<IOptions<LiveCalibrationRepositoryOptions>>().Value;
    return new FileTradingPolicyProfileStore(Path.Combine(options.RootDirectory, "..", "trading-policy-profiles"));
});
builder.Services.AddSingleton<ICalibrationBundleApprovalStore>(services =>
{
    LiveCalibrationRepositoryOptions options = services.GetRequiredService<IOptions<LiveCalibrationRepositoryOptions>>().Value;
    return new FileCalibrationBundleApprovalStore(
        Path.Combine(options.RootDirectory, "..", "calibration-bundle-candidates"),
        services.GetRequiredService<ICalibrationArtifactRepository>(),
        services.GetRequiredService<ITradingPolicyProfileStore>(),
        services.GetRequiredService<TimeProvider>());
});
builder.Services.AddSingleton<ISimulationJobRepository>(services =>
{
    LiveCalibrationRepositoryOptions options = services.GetRequiredService<IOptions<LiveCalibrationRepositoryOptions>>().Value;
    return new FileSimulationJobRepository(Path.Combine(options.RootDirectory, "..", "calibration-training-jobs"));
});
builder.Services.AddSingleton(services => new BacktestApplicationService(
    services.GetRequiredService<ISimulationJobRepository>(),
    new BacktestApplicationServiceOptions { MaxConcurrentJobs = 1, QueueCapacity = 4 }));
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
    builder.Configuration.GetSection("LiveDecisionEpoch"));
builder.Services.AddSingleton(services =>
{
    LiveMarketUniverseOptions marketOptions = services.GetRequiredService<IOptions<LiveMarketUniverseOptions>>().Value;
    LiveDecisionEpochCoordinatorOptions epochOptions =
        services.GetRequiredService<IOptions<LiveDecisionEpochCoordinatorOptions>>().Value;
    var instruments = marketOptions.Markets
        .Where(market => market.Enabled && market.Strategies.Any(strategy => strategy.Enabled))
        .Select(market => market.Instrument)
        .ToList();
    if (instruments.Count == 0)
    {
        // Preserve observe-only startup diagnostics when no Agent is configured while avoiding an
        // invalid empty barrier. The host will not receive executable candidates in this state.
        instruments = marketOptions.Markets.Where(market => market.Enabled)
            .Select(market => market.Instrument)
            .Take(1)
            .ToList();
    }
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

builder.Services.AddSingleton(services => new AgentSupervisor(
    services.GetRequiredService<IBrokerClient>(),
    services.GetRequiredService<LiveDecisionEpochCoordinator>(),
    services.GetRequiredService<TimeProvider>(),
    services.GetRequiredService<ILoggerFactory>().CreateLogger<AgentSupervisor>()));

builder.Services.AddSingleton<ITradingAccountLease, FileTradingAccountLease>();
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
builder.Services.AddSingleton<ILiveOpportunityCoordinator, LiveOpportunityCoordinator>();
builder.Services.AddSingleton<IManualApprovalStore, ManualApprovalStore>();
builder.Services.AddSingleton<ILiveOrderPositionRegistry, LiveOrderPositionRegistry>();
builder.Services.AddSingleton<IExecutionCoordinator>(services =>
    new ExecutionCoordinator(safety: services.GetRequiredService<ITradingSafetyController>()));
builder.Services.AddSingleton<ILiveExecutionGateway, LiveExecutionGateway>();
builder.Services.AddSingleton<ILiveBrokerReconciler, LiveBrokerReconciler>();

builder.Services.AddSingleton<ILiveTradingPersistence>(services =>
{
    LiveTradingPersistenceOptions configured =
        services.GetRequiredService<IOptions<LiveTradingPersistenceOptions>>().Value;
    LiveOandaOptions oanda = services.GetRequiredService<IOptions<LiveOandaOptions>>().Value;
    string account = string.Concat(oanda.AccountId.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_'));
    if (string.IsNullOrWhiteSpace(account))
        account = "unconfigured";
    return new FileLiveTradingPersistence(
        configured with { RootDirectory = Path.Combine(configured.RootDirectory, account) },
        services.GetRequiredService<TimeProvider>());
});
builder.Services.AddSingleton<ILiveAccountStateService, LiveAccountStateService>();
builder.Services.AddSingleton<ILiveTradingRuntimeCoordinator, LiveTradingRuntimeCoordinator>();
builder.Services.AddSingleton<ILivePositionManagementService, LivePositionManagementService>();
builder.Services.AddSingleton<ILiveBrokerEventProcessor, LiveBrokerEventProcessor>();

builder.Services.AddSingleton<LiveEngineState>();
builder.Services.AddSingleton<LiveStatusRealtimePublisher>();
builder.Services.AddSingleton<LiveEngineHostedService>();
builder.Services.AddHostedService(services => services.GetRequiredService<LiveEngineHostedService>());

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
LiveHostRuntimeOptions hostOptions = app.Services.GetRequiredService<LiveHostRuntimeOptions>();
app.UseCors();
app.Use(async (context, next) =>
{
    bool controlRequest = context.Request.Path.StartsWithSegments("/api/live/control") ||
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
app.MapHub<LiveStatusHub>("/hubs/live");
app.Run();
