using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dashboard.Live;
using DBManager.Abstractions.Bootstrap;
using DBManager.Abstractions.Config;
using DBManager.Abstractions.Credentials;
using DBManager.Postgres.Config;
using DBManager.Postgres.Reference;
using DBManager.Postgres.Security;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using QuantResearch.Training.Pipeline;
using Simulator.Calibration;
using Simulator.Experiments;
using Simulator.Experiments.IndicatorCalibration;
using Simulator.Experiments.IndicatorCalibration.Persistence;
using Simulator.Experiments.IndicatorCalibration.Strategies;
using Simulator.Experiments.Persistence;
using Simulator.Jobs;
using Simulator.Services;
using TradingHub.Persistence.Postgres.Bootstrap;
using TradingPolicies;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
BrokerCredentialDatabaseConfiguration? databaseConfiguration =
    BrokerCredentialDatabaseConfiguration.TryLoad(builder.Environment.ContentRootPath, out string repositoryRoot);
if (databaseConfiguration is null)
    throw new InvalidOperationException("PostgreSQL runtime configuration is required. Run DBManager.Cli migrate and seed-reference first.");

IBrokerCredentialStore brokerCredentialStore = databaseConfiguration.OpenStore(repositoryRoot);
var contextFactory = new StandaloneTradingHubContextFactory(databaseConfiguration.ConnectionString);
var runtimeProfileStore = new RuntimeProfileStore(contextFactory, TimeProvider.System);
RuntimeProfileRevisionDetail dashboardProfile = await runtimeProfileStore.GetApprovedRevisionAsync(
    RuntimeProfileKind.Dashboard,
    "default",
    CancellationToken.None) ?? throw new InvalidOperationException(
    "No approved Dashboard/default runtime profile exists in PostgreSQL. Run DBManager.Cli seed-reference first.");
await using var dashboardProfileStream = new MemoryStream(Encoding.UTF8.GetBytes(dashboardProfile.SettingsJson));
IConfiguration dashboardConfiguration = new ConfigurationBuilder()
    .AddJsonStream(dashboardProfileStream)
    .Build();
var brokerConfigurationStore = new BrokerRuntimeConfigurationStore(contextFactory);
ResolvedBrokerRuntimeConfiguration oandaRuntime =
    await brokerConfigurationStore.GetActiveAsync("OANDA", "DEMO", CancellationToken.None) ??
    await brokerConfigurationStore.GetActiveAsync("OANDA", "LIVE", CancellationToken.None) ??
    throw new InvalidOperationException("No enabled OANDA runtime configuration exists in PostgreSQL.");
ResolvedBrokerRuntimeConfiguration binanceRuntime =
    await brokerConfigurationStore.GetActiveAsync("BINANCE", "TESTNET", CancellationToken.None) ??
    await brokerConfigurationStore.GetActiveAsync("BINANCE", "LIVE", CancellationToken.None) ??
    throw new InvalidOperationException("No enabled Binance runtime configuration exists in PostgreSQL.");
SecretReference accessTokenReference = oandaRuntime.CredentialReferences.Single(reference =>
    reference.Purpose == "access_token");
using SecretValue accessToken = await new EncryptedDatabaseSecretResolver(brokerCredentialStore)
    .ResolveAsync(accessTokenReference, CancellationToken.None);
OandaWorkspaceOptions configuredOanda =
    dashboardConfiguration.GetSection(OandaWorkspaceOptions.SectionName).Get<OandaWorkspaceOptions>() ?? new();
OandaWorkspaceOptions oandaOptions = configuredOanda with
{
    Enabled = true,
    Environment = oandaRuntime.Environment.IsLive
        ? Brokers.Abstractions.BrokerEnvironment.Live
        : Brokers.Abstractions.BrokerEnvironment.Demo,
    AccountId = oandaRuntime.Account?.ExternalAccountId
        ?? throw new InvalidOperationException("The active OANDA account setting has no external account id."),
    AccessToken = accessToken.Reveal(),
    RestBaseAddress = oandaRuntime.Endpoints.Single(endpoint => endpoint.Kind == BrokerEndpointKind.Rest).BaseAddress,
    StreamBaseAddress = oandaRuntime.Endpoints.Single(endpoint => endpoint.Kind == BrokerEndpointKind.Streaming).BaseAddress
};
LiveFeedOptions configuredLiveFeed =
    dashboardConfiguration.GetSection(LiveFeedOptions.SectionName).Get<LiveFeedOptions>() ?? new();
LiveFeedOptions liveFeedOptions = configuredLiveFeed with
{
    RestBaseAddress = binanceRuntime.Endpoints.Single(endpoint => endpoint.Kind == BrokerEndpointKind.Rest).BaseAddress,
    WebSocketBaseAddress = new Uri(
        binanceRuntime.Endpoints.Single(endpoint => endpoint.Kind == BrokerEndpointKind.MarketData)
            .BaseAddress.AbsoluteUri.TrimEnd('/') + "/")
};
builder.WebHost.ConfigureKestrel(options =>
    options.Limits.MaxRequestBodySize = 2L * 1024 * 1024 * 1024);
builder.Services.Configure<FormOptions>(options =>
    options.MultipartBodyLengthLimit = 2L * 1024 * 1024 * 1024);
builder.Services.AddSingleton<IOptions<LiveFeedOptions>>(Options.Create(liveFeedOptions));
builder.Services.AddSingleton<IOptions<OandaWorkspaceOptions>>(Options.Create(oandaOptions));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ILiveWebSocketFactory, LiveWebSocketFactory>();
builder.Services.AddHttpClient("BinancePublicMarketData", (services, client) =>
{
    LiveFeedOptions options = services.GetRequiredService<IOptions<LiveFeedOptions>>().Value;
    options.Validate();
    client.BaseAddress = options.RestBaseAddress;
    client.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("TradingHub-LiveDashboard/1.0");
});
builder.Services.AddSingleton(services => new BinanceLiveAnalysisService(
    services.GetRequiredService<IHttpClientFactory>().CreateClient("BinancePublicMarketData"),
    services.GetRequiredService<ILiveWebSocketFactory>(),
    services.GetRequiredService<TimeProvider>(),
    services.GetRequiredService<IOptions<LiveFeedOptions>>(),
    services.GetRequiredService<ILogger<BinanceLiveAnalysisService>>()));
builder.Services.AddSingleton<OandaWorkspaceService>();
builder.Services.AddSingleton(services => new BinanceWorkspaceMarketData(
    services.GetRequiredService<IHttpClientFactory>().CreateClient("BinancePublicMarketData"),
    services.GetRequiredService<TimeProvider>(),
    services.GetRequiredService<IOptions<LiveFeedOptions>>(),
    services.GetRequiredService<OandaWorkspaceService>()));
builder.Services.AddSingleton<SimulationBrokerCatalogService>();
builder.Services.AddHostedService(services =>
    services.GetRequiredService<BinanceLiveAnalysisService>());
builder.Services.AddHostedService(services =>
    services.GetRequiredService<OandaWorkspaceService>());

// Shared backtest application service (Dashboard + CLI).
string jobsDirectory = Path.GetFullPath(Path.Combine(
    builder.Environment.ContentRootPath,
    "..",
    ".cache",
    "simulation-jobs"));
builder.Services.AddTradingHubPersistence(new TradingHubPersistenceRegistrationOptions
{
    Database = databaseConfiguration,
    RepositoryRoot = repositoryRoot,
    Mode = PersistenceMode.PostgresOnly,
    LegacySimulationJobsDirectory = jobsDirectory,
    LegacyLiveDirectory = Path.Combine(repositoryRoot, ".state", "live-trading"),
    DeploymentId = Guid.Empty
});
builder.Services.AddSingleton<BacktestApplicationService>(services =>
{
    // A single job is CPU-light (~1-2 cores; per-candle processing is inherently sequential,
    // measured 2026-07-20 via `top -H` while 2 jobs ran: ~12% of a 20-core box). 3 was sized for
    // "auto-cal trains legacy+improved in parallel, plus one user sim", not for the hardware -
    // left most of the machine idle. 12 keeps ~8 cores of headroom for Postgres/the OS/DB
    // connection pool rather than maxing out to core count.
    var appService = new BacktestApplicationService(
        services.GetRequiredService<ISimulationJobRepository>(),
        new BacktestApplicationServiceOptions
        {
            MaxConcurrentJobs = 12,
            QueueCapacity = 32,
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
        });
    return appService;
});
builder.Services.AddSingleton<IBacktestApplicationService>(services =>
    services.GetRequiredService<BacktestApplicationService>());
builder.Services.AddSingleton<ISimulationResourceGovernor>(_ => new SimulationResourceGovernor(
    new SimulationResourceGovernorOptions
    {
        // Host ceilings: two experiments/profile groups, one download per broker, and a
        // CPU-sized worker pool. Experiment manifests default profile groups to one; callers
        // must explicitly opt into running structural profiles concurrently.
        MaxConcurrentExperiments = 2,
        MaxConcurrentProfileGroups = 2,
        MaxHistoricalDownloadsPerBroker = 1,
        MaxTotalStrategyWorkers = Math.Max(2, Environment.ProcessorCount)
    }));
builder.Services.AddSingleton<ResearchCalibrationService>();
builder.Services.AddSingleton<PendingAutoCalibrationTracker>();
builder.Services.AddSingleton(services => new CalibrationTrainingPipeline(
    services.GetRequiredService<IBacktestApplicationService>(),
    services.GetRequiredService<ICalibrationArtifactRepository>()));
builder.Services.AddSingleton(services => new BacktestSimulationExperimentExecutor(
    services.GetRequiredService<IBacktestApplicationService>(),
    services.GetRequiredService<ICalibrationArtifactRepository>()));
builder.Services.AddSingleton<ISimulationExperimentExecutor>(services =>
    new CalibrationAwareSimulationExperimentExecutor(
        services.GetRequiredService<BacktestSimulationExperimentExecutor>(),
        services.GetRequiredService<CalibrationTrainingPipeline>(),
        services.GetRequiredService<ICalibrationArtifactRepository>()));
builder.Services.AddSingleton<SimulationExperimentApplicationService>();
builder.Services.AddSingleton<ISimulationExperimentApplicationService>(services =>
    services.GetRequiredService<SimulationExperimentApplicationService>());
builder.Services.AddSingleton<IIndicatorCalibrationLedgerRepository>(_ => new FileIndicatorCalibrationLedgerRepository());
builder.Services.AddSingleton(services => new IndicatorCalibrationApplicationService(
    services.GetRequiredService<IBacktestApplicationService>(),
    services.GetRequiredService<ICalibrationArtifactRepository>(),
    services.GetRequiredService<IIndicatorCalibrationLedgerRepository>(),
    [new IndicatorConfluenceCalibrationStrategyAdapter(), new LiquidityBreakRetestCalibrationStrategyAdapter()]));
builder.Services.AddSingleton<IIndicatorCalibrationApplicationService>(services =>
    services.GetRequiredService<IndicatorCalibrationApplicationService>());
builder.Services.AddSingleton(services => new PreRunCalibrationService(
    services.GetRequiredService<CalibrationTrainingPipeline>(),
    services.GetRequiredService<ICalibrationArtifactRepository>(),
    services.GetRequiredService<ILogger<PreRunCalibrationService>>()));
builder.Services.AddSingleton(services => new CalibrationBundleWorkflow(
    services.GetRequiredService<CalibrationTrainingPipeline>(),
    services.GetRequiredService<ICalibrationArtifactRepository>(),
    services.GetRequiredService<ICalibrationBundleApprovalStore>()));
builder.Services.AddSingleton<SimulationRealtimePublisher>();
builder.Services.AddHostedService<SimulationRealtimeBridge>();
builder.Services.AddSignalR().AddJsonProtocol(options =>
{
    // Match HTTP JSON so clients receive "WarmingUp" / "Cancelled" instead of 4 / 8.
    options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddProblemDetails();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .SetIsOriginAllowed(origin =>
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        return uri.Scheme is "http" or "https" &&
            uri.Host is "localhost" or "127.0.0.1";
    })
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

WebApplication app = builder.Build();
await TradingHubPersistenceRegistration.ValidatePostgresStartupAsync(
    app.Services,
    requireCurrentSchema: true);
app.UseExceptionHandler();
app.UseCors();

string dashboardPath = Path.GetFullPath(Path.Combine(
    app.Environment.ContentRootPath,
    "..",
    "Dashboard",
    "dist"));
if (Directory.Exists(dashboardPath))
{
    var fileProvider = new PhysicalFileProvider(dashboardPath);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = fileProvider });
}

app.MapGet("/api/live/snapshot", (BinanceLiveAnalysisService service) =>
    Results.Ok(LiveSseFrameProjector.ProjectSnapshot(service.GetPayload())));

app.MapGet("/api/workspaces/catalog", async (
    BinanceWorkspaceMarketData marketData,
    CancellationToken cancellationToken) =>
    Results.Ok(await marketData.GetCatalogAsync(cancellationToken)));

app.MapGet("/api/workspaces/binance/snapshot", async (
    string symbol,
    string interval,
    BinanceWorkspaceMarketData marketData,
    CancellationToken cancellationToken) =>
{
    try
    {
        WorkspaceSnapshot snapshot = await marketData.GetSnapshotAsync(
            symbol,
            interval,
            cancellationToken);
        return Results.Ok(LiveSseFrameProjector.ProjectWorkspaceSnapshot(snapshot));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (KeyNotFoundException exception)
    {
        return Results.NotFound(new { error = exception.Message });
    }
    catch (HttpRequestException exception)
    {
        return Results.Problem(
            title: "Binance market data is unavailable",
            detail: exception.Message,
            statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapGet("/api/workspaces/oanda/account", async (
    OandaWorkspaceService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await service.GetAccountAsync(cancellationToken));
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (HttpRequestException exception)
    {
        return Results.Problem(
            title: "OANDA account data is unavailable",
            detail: exception.Message,
            statusCode: StatusCodes.Status502BadGateway);
    }
    catch (Brokers.Exceptions.BrokerApiException exception)
    {
        return Results.Problem(
            title: "OANDA rejected the account request",
            detail: exception.Message,
            statusCode: StatusCodes.Status502BadGateway);
    }
});

// HTTP snapshot avoids multi-megabyte EventSource payloads that browsers truncate while
// indicators are still warming ("Unexpected end of JSON input").
app.MapGet("/api/workspaces/oanda/snapshot", async (
    string symbol,
    string interval,
    OandaWorkspaceService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        OandaLiveAnalysisSession session = await service.GetSessionAsync(symbol, interval, cancellationToken);
        // Wait briefly for warmup so the first paint has candles when possible.
        LiveReplayPayload payload = session.GetPayload();
        if (payload.Dataset.Series.All(series => series.Frames.Count == 0))
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(TimeSpan.FromSeconds(25));
            try
            {
                long revision = payload.Revision;
                while (payload.Dataset.Series.All(series => series.Frames.Count == 0) &&
                       !wait.IsCancellationRequested)
                {
                    await session.WaitForChangeAsync(revision, wait.Token);
                    payload = session.GetPayload();
                    revision = payload.Revision;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Return whatever is available (WarmingUp with 0 frames) rather than failing.
            }
        }

        return Results.Ok(LiveSseFrameProjector.ProjectSnapshot(payload));
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (HttpRequestException exception)
    {
        return Results.Problem(
            title: "OANDA market data is unavailable",
            detail: exception.Message,
            statusCode: StatusCodes.Status502BadGateway);
    }
    catch (Brokers.Exceptions.BrokerApiException exception)
    {
        return Results.Problem(
            title: "OANDA rejected the market-data request",
            detail: exception.Message,
            statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/api/workspaces/oanda/orders", async (
    OandaWorkspaceOrderRequest order,
    HttpRequest request,
    OandaWorkspaceService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        if (!string.Equals(
                request.Headers["X-TradingHub-Demo-Confirm"],
                "OANDA-PRACTICE",
                StringComparison.Ordinal))
        {
            return Results.BadRequest(new { error = "The explicit OANDA practice confirmation header is required." });
        }
        return Results.Ok(await service.PlaceOrderAsync(order, cancellationToken));
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (HttpRequestException exception)
    {
        return Results.Problem(
            title: "OANDA rejected or could not confirm the demo order",
            detail: exception.Message,
            statusCode: StatusCodes.Status502BadGateway);
    }
    catch (Brokers.Exceptions.BrokerApiException exception)
    {
        return Results.Problem(
            title: "OANDA rejected the demo order",
            detail: exception.Message,
            statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapDelete("/api/workspaces/oanda/orders/{orderId}", async (
    string orderId,
    HttpRequest request,
    OandaWorkspaceService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        if (!string.Equals(
                request.Headers["X-TradingHub-Demo-Confirm"],
                "OANDA-PRACTICE",
                StringComparison.Ordinal))
        {
            return Results.BadRequest(new { error = "The explicit OANDA practice confirmation header is required." });
        }
        await service.CancelOrderAsync(orderId, cancellationToken);
        return Results.NoContent();
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (HttpRequestException exception)
    {
        return Results.Problem(
            title: "OANDA could not confirm the demo order cancellation",
            detail: exception.Message,
            statusCode: StatusCodes.Status502BadGateway);
    }
    catch (Brokers.Exceptions.BrokerApiException exception)
    {
        return Results.Problem(
            title: "OANDA rejected the demo order cancellation",
            detail: exception.Message,
            statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapGet("/api/workspaces/oanda/events", async (
    string symbol,
    string interval,
    HttpContext context,
    OandaWorkspaceService service,
    IHostApplicationLifetime applicationLifetime,
    CancellationToken requestAborted) =>
{
    OandaLiveAnalysisSession session;
    try
    {
        session = await service.GetSessionAsync(symbol, interval, requestAborted);
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = exception.Message }, requestAborted);
        return;
    }

    // Prefer HTTP /snapshot for the heavy initial payload; SSE then only streams deltas.
    bool updatesOnly = string.Equals(
        context.Request.Query["updatesOnly"],
        "1",
        StringComparison.Ordinal);

    using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(
        requestAborted,
        applicationLifetime.ApplicationStopping);
    CancellationToken cancellationToken = requestLifetime.Token;
    context.Response.StatusCode = StatusCodes.Status200OK;
    context.Response.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache, no-transform";
    context.Response.Headers.Append("X-Accel-Buffering", "no");
    JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    long observedRevision = -1;
    int lastFrameIndex = -1;
    bool initialSnapshotSent = false;
    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            LiveReplayPayload payload = session.GetPayload();
            if (payload.Revision != observedRevision)
            {
                IReadOnlyList<Dashboard.Contracts.ReplayFrame> frames =
                    payload.Dataset.Series.SelectMany(series => series.Frames).ToArray();
                if (!initialSnapshotSent)
                {
                    if (updatesOnly)
                    {
                        // Client already loaded /snapshot; only seed the cursor.
                        if (frames.Count > 0)
                        {
                            lastFrameIndex = frames[^1].Index;
                        }

                        var seed = new LiveReplayUpdate(
                            payload.Revision,
                            payload.Status,
                            Array.Empty<Dashboard.Contracts.ReplayFrame>());
                        await context.Response.WriteAsync("event: update\ndata: ", cancellationToken);
                        await JsonSerializer.SerializeAsync(
                            context.Response.Body, seed, jsonOptions, cancellationToken);
                    }
                    else
                    {
                        LiveReplayPayload projected = LiveSseFrameProjector.ProjectSnapshot(payload);
                        await context.Response.WriteAsync("event: snapshot\ndata: ", cancellationToken);
                        await JsonSerializer.SerializeAsync(
                            context.Response.Body, projected, jsonOptions, cancellationToken);
                    }

                    initialSnapshotSent = true;
                }
                else
                {
                    var update = new LiveReplayUpdate(
                        payload.Revision,
                        payload.Status,
                        LiveSseFrameProjector.ProjectUpdateFrames(
                            frames.Where(frame => frame.Index > lastFrameIndex).ToArray()));
                    await context.Response.WriteAsync("event: update\ndata: ", cancellationToken);
                    await JsonSerializer.SerializeAsync(
                        context.Response.Body, update, jsonOptions, cancellationToken);
                }
                await context.Response.WriteAsync("\n\n", cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
                observedRevision = payload.Revision;
                if (frames.Count > 0)
                {
                    lastFrameIndex = frames[^1].Index;
                }
            }

            using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            heartbeat.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await session.WaitForChangeAsync(observedRevision, heartbeat.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await context.Response.WriteAsync(": keep-alive\n\n", cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
            }
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
    }
});

app.MapGet("/api/workspaces/oanda/order-events", async (
    HttpContext context,
    OandaWorkspaceService service,
    IHostApplicationLifetime applicationLifetime,
    CancellationToken requestAborted) =>
{
    if (!service.IsConfigured)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
        requestAborted,
        applicationLifetime.ApplicationStopping);
    CancellationToken cancellationToken = lifetime.Token;
    context.Response.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache, no-transform";
    long revision = -1;
    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            OandaOrderEventBatch batch = service.GetOrderEvents(revision);
            if (batch.Revision != revision)
            {
                await context.Response.WriteAsync("event: orders\ndata: ", cancellationToken);
                await JsonSerializer.SerializeAsync(
                    context.Response.Body,
                    batch,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)
                    {
                        Converters = { new JsonStringEnumConverter() }
                    },
                    cancellationToken);
                await context.Response.WriteAsync("\n\n", cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
                revision = batch.Revision;
            }

            using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            heartbeat.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await service.WaitForOrderChangeAsync(revision, heartbeat.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await context.Response.WriteAsync(": keep-alive\n\n", cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
            }
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
    }
});

app.MapGet("/api/live/health", (BinanceLiveAnalysisService service) =>
{
    LiveReplayPayload payload = service.GetPayload();
    bool healthy = payload.Status.State is
        LiveConnectionState.Connected or
        LiveConnectionState.WarmingUp or
        LiveConnectionState.Reconnecting;
    return Results.Json(
        new
        {
            healthy,
            payload.Status.State,
            payload.Status.LastMessageAt,
            payload.Status.LastClosedCandleAt,
            frames = payload.Dataset.Series.Sum(series => series.Frames.Count)
        },
        statusCode: healthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
});

app.MapGet("/api/live/events", async (
    HttpContext context,
    BinanceLiveAnalysisService service,
    IHostApplicationLifetime applicationLifetime,
    CancellationToken requestAborted) =>
{
    using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(
        requestAborted,
        applicationLifetime.ApplicationStopping);
    CancellationToken cancellationToken = requestLifetime.Token;
    context.Response.StatusCode = StatusCodes.Status200OK;
    context.Response.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache, no-transform";
    context.Response.Headers.Append("X-Accel-Buffering", "no");
    JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    long observedRevision = -1;
    int lastFrameIndex = -1;
    bool initialSnapshotSent = false;

    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            LiveReplayPayload payload = service.GetPayload();
            if (payload.Revision != observedRevision)
            {
                IReadOnlyList<Dashboard.Contracts.ReplayFrame> frames =
                    payload.Dataset.Series.SelectMany(series => series.Frames).ToArray();
                if (!initialSnapshotSent)
                {
                    LiveReplayPayload projected = LiveSseFrameProjector.ProjectSnapshot(payload);
                    await context.Response.WriteAsync("event: snapshot\ndata: ", cancellationToken);
                    await JsonSerializer.SerializeAsync(
                        context.Response.Body,
                        projected,
                        jsonOptions,
                        cancellationToken);
                    initialSnapshotSent = true;
                }
                else
                {
                    Dashboard.Contracts.ReplayFrame[] additions = frames
                        .Where(frame => frame.Index > lastFrameIndex)
                        .ToArray();
                    var update = new LiveReplayUpdate(
                        payload.Revision,
                        payload.Status,
                        LiveSseFrameProjector.ProjectUpdateFrames(additions));
                    await context.Response.WriteAsync("event: update\ndata: ", cancellationToken);
                    await JsonSerializer.SerializeAsync(
                        context.Response.Body,
                        update,
                        jsonOptions,
                        cancellationToken);
                }

                await context.Response.WriteAsync("\n\n", cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
                observedRevision = payload.Revision;
                if (frames.Count > 0)
                {
                    lastFrameIndex = frames[^1].Index;
                }
            }

            using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            heartbeat.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await service.WaitForChangeAsync(observedRevision, heartbeat.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await context.Response.WriteAsync(": keep-alive\n\n", cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
            }
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
    }
});

app.MapSimulationEndpoints();
app.MapSimulationProfileEndpoints();
app.MapSimulationExperimentEndpoints();
app.MapIndicatorCalibrationEndpoints();
app.MapCalibrationEndpoints();
app.MapCalibrationBundleEndpoints();
app.MapResearchEndpoints();
app.MapTradingReportEndpoints();
app.MapHub<SimulationHub>("/hubs/simulations");

app.Run();
