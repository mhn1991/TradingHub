using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agent.Configuration;
using Agent.Strategies;
using Brokers.Abstractions;
using Brokers.Models;
using RiskManager;
using RiskManager.Calibration;
using RiskManager.Safety;
using Simulator.Calibration;
using Simulator.MarketData;
using Simulator.Jobs;
using Simulator.Models;
using Simulator.Services;
using TradeManager;
using ChartAnnotator.Confluence;
using ChartAnnotator.Engine;
using ChartAnnotator.Liquidity;
using ChartAnnotator.Regime;
using ChartAnnotator.NeoWave;
using ChartAnnotator.SupplyDemand;
using RiskManager.Conditions;
using PortfolioManager.Risk;
using PortfolioManager.Correlation;
using PortfolioManager.CrossMarket;
using ChartAnnotator.Value;
using ChartAnnotator.Models;
using Simulator.Execution;
using Simulator.Experiments.Models;
using Simulator.Experiments.Persistence;
using Simulator.Financing;
using TradingPolicies;
using QuantResearch.Training.Pipeline;

namespace Dashboard.Live;

public static class SimulationApi
{
    private const long MaximumInteractiveReplayChunkBytes = 64L * 1024 * 1024;

    private static readonly JsonSerializerOptions ReplayJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static void MapSimulationEndpoints(this WebApplication app)
    {
        string importRoot = Path.GetFullPath(Path.Combine(
            app.Environment.ContentRootPath,
            "..",
            ".cache",
            "imported-candles"));
        CleanupExpiredImports(importRoot);

        app.MapPost("/api/simulations/imports", async (
            HttpRequest request,
            CancellationToken cancellationToken) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "Expected multipart form data containing a CSV file." });

            IFormCollection form = await request.ReadFormAsync(cancellationToken);
            IFormFile? upload = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (upload is null || upload.Length == 0)
                return Results.BadRequest(new { error = "A non-empty CSV file is required." });
            if (upload.Length > 2L * 1024 * 1024 * 1024)
                return Results.BadRequest(new { error = "Imported datasets are limited to 2 GiB." });
            if (!string.Equals(Path.GetExtension(upload.FileName), ".csv", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Imported candle datasets must be CSV files." });

            BarInterval interval;
            try
            {
                interval = BarIntervalParser.Parse(form["interval"].FirstOrDefault() ?? "1s");
                if (interval != BarInterval.Seconds(1) && interval != BarInterval.Seconds(5))
                    throw new ArgumentException("Imported datasets support 1s or 5s intervals.");
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }

            Directory.CreateDirectory(importRoot);
            string datasetId = Guid.NewGuid().ToString("N");
            string path = Path.Combine(importRoot, datasetId + ".csv");
            try
            {
                await using (FileStream output = new(
                                 path,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 128 * 1024,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await upload.CopyToAsync(output, cancellationToken);
                }

                long rows = await ImportedSecondCandleSource.ValidateFileAsync(
                    path,
                    interval,
                    cancellationToken);
                DateTimeOffset createdAt = DateTimeOffset.UtcNow;
                var metadata = new ImportedDatasetMetadata(
                    datasetId,
                    Path.GetFileName(upload.FileName),
                    BarIntervalParser.Format(interval),
                    rows,
                    upload.Length,
                    createdAt,
                    createdAt.AddDays(7));
                string metadataPath = Path.Combine(importRoot, datasetId + ".metadata.json");
                string temporaryMetadata = metadataPath + ".tmp";
                await File.WriteAllTextAsync(
                    temporaryMetadata,
                    JsonSerializer.Serialize(metadata),
                    cancellationToken);
                File.Move(temporaryMetadata, metadataPath, overwrite: true);
                return Results.Created($"/api/simulations/imports/{datasetId}", new
                {
                    datasetId,
                    fileName = Path.GetFileName(upload.FileName),
                    interval = BarIntervalParser.Format(interval),
                    rows,
                    sizeBytes = upload.Length,
                    expiresAt = metadata.ExpiresAt
                });
            }
            catch (InvalidDataException exception)
            {
                if (File.Exists(path))
                    File.Delete(path);
                return Results.BadRequest(new { error = exception.Message });
            }
            catch
            {
                if (File.Exists(path))
                    File.Delete(path);
                throw;
            }
        });

        app.MapGet("/api/simulations/imports", () =>
        {
            CleanupExpiredImports(importRoot);
            if (!Directory.Exists(importRoot))
                return Results.Ok(Array.Empty<ImportedDatasetMetadata>());
            var datasets = new List<ImportedDatasetMetadata>();
            foreach (string metadataPath in Directory.EnumerateFiles(importRoot, "*.metadata.json"))
            {
                try
                {
                    ImportedDatasetMetadata? metadata = JsonSerializer.Deserialize<ImportedDatasetMetadata>(
                        File.ReadAllText(metadataPath));
                    if (metadata is not null && metadata.ExpiresAt > DateTimeOffset.UtcNow)
                        datasets.Add(metadata);
                }
                catch (JsonException)
                {
                    // Invalid metadata is not selectable and is removed by cleanup.
                }
            }
            return Results.Ok(datasets.OrderByDescending(item => item.CreatedAt).ToArray());
        });

        app.MapDelete("/api/simulations/imports/{datasetId}", (string datasetId) =>
        {
            if (!Guid.TryParseExact(datasetId, "N", out Guid parsed))
                return Results.BadRequest(new { error = "Invalid dataset ID." });
            string canonical = parsed.ToString("N");
            string csv = Path.Combine(importRoot, canonical + ".csv");
            string metadata = Path.Combine(importRoot, canonical + ".metadata.json");
            if (!File.Exists(csv) && !File.Exists(metadata))
                return Results.NotFound();
            if (File.Exists(csv)) File.Delete(csv);
            if (File.Exists(metadata)) File.Delete(metadata);
            return Results.NoContent();
        });

        app.MapGet("/api/simulations/catalog", async (
            bool? refresh,
            SimulationBrokerCatalogService catalog,
            CancellationToken cancellationToken) =>
        {
            SimulationBrokerCatalog result = await catalog.GetAsync(refresh == true, cancellationToken);
            return Results.Ok(result);
        });

        app.MapGet("/api/simulations/health", (
            ISimulationJobRepository repository,
            Microsoft.Extensions.Options.IOptions<OandaWorkspaceOptions> oandaOptions) =>
        {
            SimulationJobRecoveryReport recovery = repository is FileSimulationJobRepository files
                ? files.LastRecoveryReport
                : new SimulationJobRecoveryReport();
            bool oandaConfigured = oandaOptions.Value.IsConfigured;
            return Results.Ok(new
            {
                status = "Healthy",
                jobRepository = "Healthy",
                jobSchemaVersion = FileSimulationJobRepository.CurrentSchemaVersion,
                recovery.ValidJobs,
                recovery.InterruptedJobsMarkedFailed,
                recovery.QuarantinedFiles,
                recovery.TemporaryFilesRemoved,
                recovery.WarningCodes,
                oandaCredentialsConfigured = oandaConfigured
            });
        });

        app.MapPost("/api/simulations", async (
            CreateSimulationRequest body,
            IBacktestApplicationService service,
            SimulationBrokerCatalogService catalog,
            ISimulationStrategyProfileStore profileStore,
            ICalibrationArtifactRepository calibrationArtifacts,
            PreRunCalibrationService preRunCalibration,
            PendingAutoCalibrationTracker pendingTracker,
            CancellationToken cancellationToken) =>
        {
            try
            {
                SimulationStrategyProfile? profile = await ResolveSimulationProfileAsync(
                    body, profileStore, cancellationToken);
                SimulationBrokerOption selectedBroker = await catalog.ValidateSelectionAsync(
                    body.ResolveBrokerId(),
                    profile?.Instrument.Value ?? body.Instrument,
                    cancellationToken);
                ResolvedCalibration calibration = profile is null
                    ? new ResolvedCalibration(
                        await ResolveSetupCalibrationArtifactAsync(
                            body.SetupCalibrationArtifactId, calibrationArtifacts, cancellationToken),
                        await ResolveManagementCalibrationArtifactAsync(
                            body.ManagementCalibrationArtifactId, calibrationArtifacts, cancellationToken),
                        await ResolveMetaModelArtifactAsync(
                            body.MetaModelArtifactId, calibrationArtifacts, cancellationToken))
                    : await ResolveProfileCalibrationAsync(profile, calibrationArtifacts, cancellationToken);
                SetupCalibrationArtifact? setupCalibrationArtifact = calibration.Setup;
                TradeManagementCalibration? managementCalibrationArtifact = calibration.Management;
                MetaModelArtifact? metaModelArtifact = calibration.MetaModel;

                // When auto-calibration is on and the caller did not supply manual artifact
                // IDs, train setup→meta→management on a past window (default 2 months) ending
                // EmbargoDays before From, with one parallel chain per strategy (legacy/improved).
                bool shouldAutoCalibrate = profile?.Calibration.Mode ==
                        ExperimentCalibrationMode.TrainFreshAndUseForHeldOutEvaluation ||
                    profile is null && body.ShouldAutoCalibrate(
                        setupCalibrationArtifact,
                        managementCalibrationArtifact,
                        metaModelArtifact);

                if (shouldAutoCalibrate)
                {
                    // Training is a full nested backtest over the training window (minutes, and
                    // it competes for CPU with every other running simulation) - running it
                    // inline here would hold this HTTP request open for the entire duration with
                    // no job id to poll in the meantime. Register a placeholder immediately,
                    // return it, and do the training + real StartAsync in the background against
                    // an independent CancellationToken (the request's own token dies with the
                    // HTTP response).
                    BacktestRequest trainingSeed = body.ToBacktestRequest(selectedBroker);
                    if (profile is not null)
                        trainingSeed = ApplySimulationProfile(trainingSeed, profile, null, null, null);
                    string[] trainingStrategies = ResolveTrainingStrategies(trainingSeed);

                    Guid placeholderId = Guid.NewGuid();
                    DateTimeOffset now = DateTimeOffset.UtcNow;
                    SimulationJobSnapshot placeholder = new()
                    {
                        Id = placeholderId,
                        Status = SimulationJobStatus.Queued,
                        CreatedAt = now,
                        Instrument = trainingSeed.Instrument.Value,
                        RequestedFrom = body.From,
                        RequestedTo = body.To,
                        WarmupFrom = trainingSeed.ResolveWarmupFrom(),
                        ProcessedBaseCandles = 0,
                        ProgressPercent = 0m,
                        CandlesPerSecond = 0m,
                        Strategies = trainingStrategies.Select(name => new StrategyProgressSnapshot
                        {
                            StrategyId = name,
                            StrategyName = name,
                            Balance = trainingSeed.StartingBalance,
                            Equity = trainingSeed.StartingBalance,
                            UnrealizedProfitLoss = 0m,
                            OpenPositions = 0,
                            CompletedTrades = 0,
                            ActiveSetups = 0,
                            NetProfit = 0m,
                            Status = "Training"
                        }).ToArray(),
                        DataSourceStatus = "Training"
                    };
                    Guid registeredId = pendingTracker.Register(placeholder, out CancellationToken trainingToken);

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            PreRunCalibrationResult autoCal = await preRunCalibration.TrainAsync(
                                new PreRunCalibrationRequest
                                {
                                    Instrument = trainingSeed.Instrument,
                                    Strategies = trainingStrategies,
                                    AgentDefinition = profile?.Agent,
                                    EvaluationFrom = body.From,
                                    EvaluationTo = body.To,
                                    Runtime = trainingSeed.Runtime,
                                    StartingBalance = trainingSeed.StartingBalance,
                                    Quantity = trainingSeed.Quantity,
                                    TrainMonths = body.AutoCalibrateTrainMonths,
                                    EmbargoDays = body.AutoCalibrateEmbargoDays,
                                    Description =
                                        $"pre-run auto-cal before sim · {trainingSeed.Instrument} · " +
                                        $"{body.From:yyyy-MM-dd}→{body.To:yyyy-MM-dd}"
                                },
                                trainingToken).ConfigureAwait(false);

                            BacktestRequest resolvedRequest = body.ToBacktestRequest(
                                selectedBroker, autoCal.Setup, autoCal.Management, autoCal.MetaModel);
                            if (profile is not null)
                            {
                                resolvedRequest = ApplySimulationProfile(
                                    resolvedRequest, profile, autoCal.Setup, autoCal.Management, autoCal.MetaModel);
                            }

                            SimulationJobHandle handle = await service.StartAsync(resolvedRequest, trainingToken)
                                .ConfigureAwait(false);
                            pendingTracker.Resolve(registeredId, handle.SimulationId);
                        }
                        catch (OperationCanceledException)
                        {
                            // Cancelled via the tracker's own cancel path - already marked there.
                        }
                        catch (Exception exception)
                        {
                            pendingTracker.Fail(registeredId, exception.Message);
                        }
                    }, CancellationToken.None);

                    return Results.Accepted($"/api/simulations/{registeredId:N}", new
                    {
                        simulationId = registeredId,
                        status = "Queued",
                        training = true
                    });
                }

                BacktestRequest request = body.ToBacktestRequest(
                    selectedBroker, setupCalibrationArtifact, managementCalibrationArtifact, metaModelArtifact);
                if (profile is not null)
                {
                    request = ApplySimulationProfile(
                        request,
                        profile,
                        setupCalibrationArtifact,
                        managementCalibrationArtifact,
                        metaModelArtifact);
                }
                SimulationJobHandle handle = await service.StartAsync(request, cancellationToken);
                return Results.Accepted($"/api/simulations/{handle.SimulationId:N}", new
                {
                    simulationId = handle.SimulationId,
                    status = handle.Status.ToString(),
                    autoCalibration = (object?)null
                });
            }
            catch (HistoricalGranularityNotSupportedException exception)
            {
                return Results.BadRequest(new
                {
                    error = exception.Message,
                    code = exception.ErrorCode,
                    source = exception.SourceName,
                    requestedInterval = BarIntervalParser.Format(exception.RequestedInterval),
                    supportedIntervals = exception.SupportedIntervals,
                    suggestion = exception.Suggestion
                });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message, code = "InvalidSimulationRequest" });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message, code = "SimulationStateConflict" });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Results.Problem(
                    title: "Simulation service is temporarily unavailable",
                    detail: "The simulation job repository could not be accessed. Check the server log for the trace ID.",
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    extensions: new Dictionary<string, object?>
                    {
                        ["code"] = "SimulationRepositoryUnavailable"
                    });
            }
        });

        app.MapGet("/api/simulations", async (
            IBacktestApplicationService service,
            PendingAutoCalibrationTracker pendingTracker,
            int? take,
            CancellationToken cancellationToken) =>
        {
            IReadOnlyList<SimulationJobSnapshot> jobs =
                await service.ListAsync(take ?? 50, cancellationToken);
            IReadOnlyList<SimulationJobSnapshot> pending = pendingTracker.ListPending();
            return Results.Ok(pending.Count == 0 ? jobs : pending.Concat(jobs).ToArray());
        });

        app.MapGet("/api/simulations/{id:guid}", async (
            Guid id,
            IBacktestApplicationService service,
            PendingAutoCalibrationTracker pendingTracker,
            CancellationToken cancellationToken) =>
        {
            SimulationJobSnapshot? snapshot = await service.GetAsync(id, cancellationToken)
                ?? await pendingTracker.TryGetSnapshotAsync(id, service.GetAsync, cancellationToken);
            return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
        });

        app.MapPost("/api/simulations/{id:guid}/policy-profiles/{strategy}", async (
            Guid id,
            string strategy,
            PromoteTradingPolicyRequest promotion,
            IBacktestApplicationService service,
            ITradingPolicyProfileStore profiles,
            CancellationToken cancellationToken) =>
        {
            SimulationJobSnapshot? snapshot = await service.GetAsync(id, cancellationToken);
            if (snapshot is null)
                return Results.NotFound();
            if (snapshot.Status != SimulationJobStatus.Completed || !snapshot.IsComplete)
            {
                return Results.Conflict(new
                {
                    error = "Only a completed simulation can be promoted into a live policy profile.",
                    code = "SimulationNotCompleted"
                });
            }
            if (snapshot.Request is null)
            {
                return Results.Conflict(new
                {
                    error = "The persisted simulation does not contain its originating request.",
                    code = "SimulationRequestUnavailable"
                });
            }

            string? normalized = TryNormalizePromotedStrategy(strategy);
            if (normalized is null)
            {
                return Results.BadRequest(new
                {
                    error = $"Unknown strategy '{strategy}'. Use legacy, improved, or structural-confluence.",
                    code = "UnknownStrategy"
                });
            }
            bool included = snapshot.Request.StrategyAssignments is { Count: > 0 } assignments
                ? assignments.Any(item => TryNormalizePromotedStrategy(item.StrategyType) == normalized)
                : snapshot.Request.Strategies.Any(item => TryNormalizePromotedStrategy(item) == normalized);
            if (!included)
            {
                return Results.BadRequest(new
                {
                    error = $"Strategy '{strategy}' was not part of simulation {id:N}.",
                    code = "StrategyNotSimulated"
                });
            }

            StrategyInstrumentAssignment? selectedAssignment = snapshot.Request.StrategyAssignments?
                .FirstOrDefault(item => TryNormalizePromotedStrategy(item.StrategyType) == normalized);
            TradingAgentDefinition agentDefinition = selectedAssignment is null
                ? snapshot.Request.ResolveAgentDefinition(normalized)
                : snapshot.Request.ResolveAgentDefinition(
                    selectedAssignment.StrategyType,
                    selectedAssignment.AgentDefinitionOverride,
                    selectedAssignment.AgentOptionsOverride);
            string strategyVersion = string.IsNullOrWhiteSpace(promotion.StrategyVersion)
                ? $"{normalized}:{snapshot.SimulationConfigurationId ?? "unversioned"}"
                : promotion.StrategyVersion.Trim();
            Guid profileId = DerivePolicyProfileId(id, normalized, snapshot.SimulationConfigurationId);
            TradingPolicyProfile profile = TradingPolicyPromotion.CreateProfile(
                snapshot.Request.Runtime,
                normalized,
                strategyVersion,
                agentDefinition,
                profileId,
                promotion.Revision,
                DateTimeOffset.UtcNow,
                promotion.ApproveForDemo
                    ? TradingPolicyProfileStatus.ApprovedForDemo
                    : TradingPolicyProfileStatus.Reviewed,
                promotion.SetupCalibrationArtifactId,
                promotion.ManagementCalibrationArtifactId,
                promotion.MetaModelArtifactId,
                promotion.Description);

            try
            {
                await profiles.StoreAsync(profile, cancellationToken);
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message, code = "PolicyProfileAlreadyExists" });
            }

            return Results.Ok(profile);
        });

        app.MapPost("/api/simulations/{id:guid}/pause", async (
            Guid id,
            IBacktestApplicationService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await service.PauseAsync(id, cancellationToken);
                return Results.Accepted();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message, code = "SimulationStateConflict" });
            }
        });

        app.MapPost("/api/simulations/{id:guid}/resume", async (
            Guid id,
            IBacktestApplicationService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await service.ResumeAsync(id, cancellationToken);
                return Results.Accepted();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message, code = "SimulationStateConflict" });
            }
        });

        app.MapPost("/api/simulations/{id:guid}/cancel", async (
            Guid id,
            IBacktestApplicationService service,
            PendingAutoCalibrationTracker pendingTracker,
            CancellationToken cancellationToken) =>
        {
            if (pendingTracker.TryCancelPending(id, out SimulationJobSnapshot? _))
                return Results.Accepted();

            try
            {
                await service.CancelAsync(id, cancellationToken);
                return Results.Accepted();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        app.MapGet("/api/simulations/{id:guid}/strategies", async (
            Guid id,
            IBacktestApplicationService service,
            CancellationToken cancellationToken) =>
        {
            SimulationJobSnapshot? snapshot = await service.GetAsync(id, cancellationToken);
            return snapshot is null ? Results.NotFound() : Results.Ok(snapshot.Strategies);
        });

        app.MapGet("/api/simulations/{id:guid}/trades", async (
            Guid id,
            string? strategy,
            string? cursor,
            int? limit,
            IBacktestApplicationService service,
            CancellationToken cancellationToken) =>
        {
            int take = Math.Clamp(limit ?? 100, 1, 1_000);
            SimulationJobSnapshot? snapshot = await service.GetAsync(id, cancellationToken);
            if (snapshot?.OutputDirectory is null || !Directory.Exists(snapshot.OutputDirectory))
                return snapshot is null
                    ? Results.NotFound()
                    : Results.Ok(new { items = Array.Empty<object>(), nextCursor = cursor, hasMore = false });

            string strategiesRoot = Path.Combine(snapshot.OutputDirectory, "strategies");
            if (!Directory.Exists(strategiesRoot))
                return Results.Ok(new { items = Array.Empty<object>(), nextCursor = cursor, hasMore = false });

            int startOrdinal = 0;
            if (!string.IsNullOrWhiteSpace(cursor) &&
                (!cursor.StartsWith("trade:", StringComparison.OrdinalIgnoreCase) ||
                 !int.TryParse(cursor[6..], out startOrdinal) || startOrdinal < 0))
            {
                return Results.BadRequest(new { error = "Invalid trade cursor." });
            }

            var indexed = new List<(string StrategyId, string DataPath, TradeIndexEntryDto Entry)>();
            foreach (string directory in Directory.EnumerateDirectories(strategiesRoot).OrderBy(path => path))
            {
                string strategyId = Path.GetFileName(directory);
                if (!string.IsNullOrWhiteSpace(strategy) &&
                    !string.Equals(strategyId, strategy, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string indexPath = Path.Combine(directory, "trades.index.json");
                string liveTradesPath = Path.Combine(directory, "trades.ndjson");
                if (!File.Exists(indexPath) || !File.Exists(liveTradesPath))
                    continue;
                try
                {
                    await using FileStream indexFile = new(
                        indexPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite,
                        16 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    TradeIndexDto? tradeIndex = await JsonSerializer.DeserializeAsync<TradeIndexDto>(
                        indexFile,
                        cancellationToken: cancellationToken);
                    IReadOnlyList<TradeIndexEntryDto?> entries = tradeIndex?.Items ?? [];
                    long dataLength = new FileInfo(liveTradesPath).Length;
                    foreach (TradeIndexEntryDto? entry in entries)
                    {
                        if (entry is null)
                        {
                            app.Logger.LogWarning(
                                "Ignoring null trade-index entry for simulation {SimulationId}, strategy {StrategyId}.",
                                id,
                                strategyId);
                            continue;
                        }

                        bool valid = entry.Ordinal >= 0 &&
                            entry.Offset >= 0 &&
                            entry.Length > 0 &&
                            entry.Length <= 16 * 1024 * 1024 &&
                            entry.Offset <= dataLength &&
                            entry.Length <= dataLength - entry.Offset &&
                            !string.IsNullOrWhiteSpace(entry.SetupId);
                        if (valid)
                        {
                            indexed.Add((strategyId, liveTradesPath, entry));
                        }
                        else
                        {
                            app.Logger.LogWarning(
                                "Ignoring invalid trade-index entry for simulation {SimulationId}, strategy {StrategyId}, ordinal {Ordinal}.",
                                id,
                                strategyId,
                                entry.Ordinal);
                        }
                    }
                }
                catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
                {
                    app.Logger.LogWarning(
                        exception,
                        "Ignoring unreadable trade index for simulation {SimulationId}, strategy {StrategyId}.",
                        id,
                        strategyId);
                }
            }

            indexed.Sort(static (left, right) =>
            {
                int time = Nullable.Compare(left.Entry.ClosedAt, right.Entry.ClosedAt);
                if (time != 0) return time;
                int strategyOrder = string.Compare(left.StrategyId, right.StrategyId, StringComparison.Ordinal);
                return strategyOrder != 0 ? strategyOrder : left.Entry.Ordinal.CompareTo(right.Entry.Ordinal);
            });

            var items = new List<object>(take);
            int consumedEntries = 0;
            foreach ((string strategyId, string dataPath, TradeIndexEntryDto entry) in
                     indexed.Skip(startOrdinal).Take(take))
            {
                consumedEntries++;
                try
                {
                    await using FileStream data = new(
                        dataPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite,
                        16 * 1024,
                        FileOptions.Asynchronous | FileOptions.RandomAccess);
                    if (entry.Offset < 0 || entry.Length <= 0 ||
                        entry.Offset > data.Length || entry.Length > data.Length - entry.Offset)
                        continue;
                    data.Seek(entry.Offset, SeekOrigin.Begin);
                    byte[] bytes = new byte[entry.Length];
                    await data.ReadExactlyAsync(bytes, cancellationToken);
                    JsonElement trade = JsonSerializer.Deserialize<JsonElement>(bytes.AsSpan());
                    items.Add(new { strategyId, trade });
                }
                catch (Exception exception) when (exception is JsonException or IOException or EndOfStreamException)
                {
                    app.Logger.LogWarning(
                        exception,
                        "Ignoring unreadable live trade for simulation {SimulationId}, strategy {StrategyId}, ordinal {Ordinal}.",
                        id,
                        strategyId,
                        entry.Ordinal);
                }
            }

            // Advance over every examined index entry, including a corrupt NDJSON row. Otherwise
            // a client can become trapped retrying the same unreadable trade forever.
            int nextOrdinal = startOrdinal + consumedEntries;
            bool hasMore = nextOrdinal < indexed.Count;
            return Results.Ok(new
            {
                items,
                nextCursor = $"trade:{nextOrdinal}",
                hasMore
            });
        });

        app.MapGet("/api/simulations/{id:guid}/performance", async (
            Guid id,
            IBacktestApplicationService service,
            CancellationToken cancellationToken) =>
        {
            SimulationJobSnapshot? snapshot = await service.GetAsync(id, cancellationToken);
            if (snapshot is null)
                return Results.NotFound();

            return Results.Ok(new
            {
                snapshot.Id,
                snapshot.Status,
                snapshot.Strategies,
                snapshot.WorkerMetrics,
                snapshot.DataQuality,
                snapshot.InputHash
            });
        });

        app.MapGet("/api/simulations/{id:guid}/replay", async (
            Guid id,
            DateTimeOffset? from,
            DateTimeOffset? to,
            long? startSequence,
            long? endSequence,
            int? limit,
            string? cursor,
            IBacktestApplicationService service,
            CancellationToken cancellationToken) =>
        {
            const int maxLimit = 5_000;
            int take = Math.Clamp(limit ?? 1_000, 1, maxLimit);

            SimulationJobSnapshot? snapshot = await service.GetAsync(id, cancellationToken);
            if (snapshot?.OutputDirectory is null)
                return snapshot is null ? Results.NotFound() : Results.NotFound(new { error = "No replay output yet." });

            string marketDir = Path.Combine(snapshot.OutputDirectory, "market");
            if (!Directory.Exists(marketDir))
            {
                return Results.Ok(new
                {
                    simulationId = id,
                    startSequence = startSequence ?? 0,
                    nextCursor = (string?)null,
                    hasMore = false,
                    rows = Array.Empty<object>()
                });
            }

            long minSequence = startSequence ?? 0;
            if (!string.IsNullOrWhiteSpace(cursor) &&
                cursor.StartsWith("seq:", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(cursor[4..], out long cursorSeq))
            {
                minSequence = Math.Max(minSequence, cursorSeq);
            }

            var rows = new List<JsonElement>(take);
            long lastSequence = minSequence;
            bool hasMore = false;

            foreach (string path in Directory.EnumerateFiles(marketDir, "chunk-*.json.gz").OrderBy(p => p))
            {
                await using FileStream file = File.OpenRead(path);
                await using var gzip = new GZipStream(file, CompressionMode.Decompress);
                await foreach (JsonElement row in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(
                                   gzip,
                                   cancellationToken: cancellationToken))
                {
                    long sequence = 0;
                    if (row.TryGetProperty("sequence", out JsonElement seqEl))
                        sequence = seqEl.GetInt64();
                    if (sequence < minSequence)
                        continue;
                    if (endSequence is not null && sequence > endSequence)
                    {
                        hasMore = false;
                        goto Done;
                    }

                    if (from is not null || to is not null)
                    {
                        if (row.TryGetProperty("availableAt", out JsonElement availableAt) &&
                            DateTimeOffset.TryParse(availableAt.GetString(), out DateTimeOffset stamp))
                        {
                            if (from is not null && stamp < from)
                                continue;
                            if (to is not null && stamp > to)
                                continue;
                        }
                    }

                    if (rows.Count >= take)
                    {
                        hasMore = true;
                        goto Done;
                    }

                    rows.Add(row);
                    lastSequence = sequence + 1;
                }
            }

        Done:
            return Results.Ok(new
            {
                simulationId = id,
                startSequence = minSequence,
                nextCursor = hasMore ? $"seq:{lastSequence}" : null,
                hasMore,
                count = rows.Count,
                rows
            });
        });

        app.MapGet("/api/simulations/{id:guid}/replay/chunks", async (
            Guid id,
            IBacktestApplicationService service,
            CancellationToken cancellationToken) =>
        {
            SimulationJobSnapshot? snapshot = await service.GetAsync(id, cancellationToken);
            if (snapshot?.OutputDirectory is null)
                return snapshot is null ? Results.NotFound() : Results.Ok(Array.Empty<object>());

            string marketDir = Path.Combine(snapshot.OutputDirectory, "market");
            if (!Directory.Exists(marketDir))
                return Results.Ok(Array.Empty<object>());

            string indexPath = Path.Combine(marketDir, "index.json");
            if (File.Exists(indexPath))
            {
                await using FileStream indexFile = new(
                    indexPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    16 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                JsonElement index = await JsonSerializer.DeserializeAsync<JsonElement>(
                    indexFile,
                    cancellationToken: cancellationToken);
                if (index.TryGetProperty("chunks", out JsonElement indexedChunks))
                    return Results.Ok(indexedChunks.Clone());
            }

            var chunks = Directory.EnumerateFiles(marketDir, "chunk-*.json.gz")
                .OrderBy(path => path)
                .Select(path =>
                {
                    string name = Path.GetFileName(path);
                    string chunkId = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path));
                    return new
                    {
                        chunkId,
                        file = name,
                        sizeBytes = new FileInfo(path).Length
                    };
                })
                .ToArray();
            return Results.Ok(chunks);
        });

        app.MapGet("/api/simulations/{id:guid}/replay/chunks/{chunkId}", async (
            Guid id,
            string chunkId,
            int? take,
            HttpContext context,
            IBacktestApplicationService service,
            CancellationToken cancellationToken) =>
        {
            string normalizedChunkId = chunkId.EndsWith(".json.gz", StringComparison.OrdinalIgnoreCase)
                ? chunkId[..^8]
                : chunkId;
            if (!IsSafeChunkId(normalizedChunkId))
                return Results.BadRequest(new { error = "Invalid chunk id." });

            SimulationJobSnapshot? snapshot = await service.GetAsync(id, cancellationToken);
            if (snapshot?.OutputDirectory is null)
                return Results.NotFound();

            string marketRoot = Path.GetFullPath(Path.Combine(snapshot.OutputDirectory, "market"));
            string path = Path.GetFullPath(Path.Combine(marketRoot, $"{normalizedChunkId}.json.gz"));
            if (!path.StartsWith(marketRoot, StringComparison.Ordinal))
                return Results.BadRequest(new { error = "Invalid chunk path." });

            if (!File.Exists(path))
                return Results.NotFound();

            if (take is not null)
            {
                if (new FileInfo(path).Length > MaximumInteractiveReplayChunkBytes)
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status413PayloadTooLarge,
                        title: "Replay chunk is too large for interactive playback.",
                        detail: "This replay was produced by an older runtime with oversized chunks. " +
                                "Start a new run after restarting Dashboard.Live; full gzip download remains available.");
                }
                return await ReadReplayChunkTailAsync(path, take.Value, cancellationToken);
            }

            return GzipJsonFile(context, path);
        });

        app.MapGet("/api/simulations/{id:guid}/replay/execution-detail", async (
            Guid id,
            string strategy,
            string setupId,
            IBacktestApplicationService service,
            CancellationToken cancellationToken) =>
        {
            SimulationJobSnapshot? snapshot = await service.GetAsync(id, cancellationToken);
            if (snapshot?.OutputDirectory is null)
                return Results.NotFound();
            string detailRoot = Path.GetFullPath(Path.Combine(snapshot.OutputDirectory, "execution-detail"));
            string directory = Path.GetFullPath(Path.Combine(
                detailRoot,
                SanitizePathSegment(strategy),
                SanitizePathSegment(setupId)));
            if (!directory.StartsWith(detailRoot, StringComparison.Ordinal) || !Directory.Exists(directory))
                return Results.NotFound();
            string indexPath = Path.Combine(directory, "index.json");
            if (!File.Exists(indexPath))
                return Results.NotFound();
            await using FileStream index = new(
                indexPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            JsonElement payload = await JsonSerializer.DeserializeAsync<JsonElement>(
                index,
                cancellationToken: cancellationToken);
            return Results.Ok(payload);
        });

        app.MapGet("/api/simulations/{id:guid}/replay/execution-detail/{chunkId}", async (
            Guid id,
            string chunkId,
            string strategy,
            string setupId,
            HttpContext context,
            IBacktestApplicationService service,
            CancellationToken cancellationToken) =>
        {
            if (!IsSafeChunkId(chunkId))
                return Results.BadRequest(new { error = "Invalid chunk id." });
            SimulationJobSnapshot? snapshot = await service.GetAsync(id, cancellationToken);
            if (snapshot?.OutputDirectory is null)
                return Results.NotFound();
            string detailRoot = Path.GetFullPath(Path.Combine(snapshot.OutputDirectory, "execution-detail"));
            string directory = Path.GetFullPath(Path.Combine(
                detailRoot,
                SanitizePathSegment(strategy),
                SanitizePathSegment(setupId)));
            string path = Path.GetFullPath(Path.Combine(directory, $"{chunkId}.json.gz"));
            if (!path.StartsWith(detailRoot, StringComparison.Ordinal) || !File.Exists(path))
                return Results.NotFound();
            return GzipJsonFile(context, path);
        });
    }

    private static IResult GzipJsonFile(HttpContext context, string path)
    {
        // Replay chunks are already valid JSON compressed by the writer. Serving the
        // stored representation lets the HTTP client decompress it incrementally and
        // avoids building a second, potentially very large, server-side JSON DOM.
        context.Response.Headers["Content-Encoding"] = "gzip";
        context.Response.Headers["Vary"] = "Accept-Encoding";
        return Results.File(path, "application/json");
    }

    private static async Task<IResult> ReadReplayChunkTailAsync(
        string path,
        int requestedTake,
        CancellationToken cancellationToken)
    {
        const int maximumTake = 750;
        const int swingLimit = 100;
        int take = Math.Clamp(requestedTake, 1, maximumTake);
        var rows = new Queue<DashboardReplayRow>(take);
        await using FileStream file = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        await foreach (DashboardReplayRow? source in JsonSerializer.DeserializeAsyncEnumerable<DashboardReplayRow>(
                           gzip,
                           ReplayJsonOptions,
                           cancellationToken))
        {
            if (source is null)
                continue;

            DashboardReplayRow row = source;
            if (source.Analysis is { } analysis && analysis.Swings.Count > swingLimit)
            {
                row = source with
                {
                    Analysis = analysis with
                    {
                        Swings = analysis.Swings
                            .Skip(analysis.Swings.Count - swingLimit)
                            .ToArray()
                    }
                };
            }

            if (rows.Count == take)
                rows.Dequeue();
            rows.Enqueue(row);
        }

        return Results.Ok(rows.ToArray());
    }

    private static string SanitizePathSegment(string value) => string.Concat(value.Select(character =>
        char.IsLetterOrDigit(character) ? character : '_'));

    private static bool IsSafeChunkId(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_');

    private static void CleanupExpiredImports(string importRoot)
    {
        if (!Directory.Exists(importRoot))
            return;
        foreach (string metadataPath in Directory.EnumerateFiles(importRoot, "*.metadata.json"))
        {
            bool delete = false;
            string fileName = Path.GetFileName(metadataPath);
            string id = fileName[..^".metadata.json".Length];
            if (!Guid.TryParseExact(id, "N", out _))
            {
                delete = true;
            }
            else
            {
                try
                {
                    ImportedDatasetMetadata? metadata = JsonSerializer.Deserialize<ImportedDatasetMetadata>(
                        File.ReadAllText(metadataPath));
                    delete = metadata is null || metadata.ExpiresAt <= DateTimeOffset.UtcNow;
                }
                catch (JsonException)
                {
                    delete = true;
                }
            }

            if (!delete)
                continue;
            if (File.Exists(metadataPath)) File.Delete(metadataPath);
            string csv = Path.Combine(importRoot, id + ".csv");
            if (File.Exists(csv)) File.Delete(csv);
        }

        // Remove orphaned CSV files older than the retention window.
        foreach (string csv in Directory.EnumerateFiles(importRoot, "*.csv"))
        {
            string id = Path.GetFileNameWithoutExtension(csv);
            string metadata = Path.Combine(importRoot, id + ".metadata.json");
            if (!File.Exists(metadata) && File.GetCreationTimeUtc(csv) < DateTime.UtcNow.AddDays(-7))
                File.Delete(csv);
        }
    }

    private static string[] ResolveTrainingStrategies(BacktestRequest request)
    {
        if (request.StrategyAssignments is { Count: > 0 } assignments)
        {
            return assignments
                .Select(item => item.StrategyType)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return request.Strategies
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task<SimulationStrategyProfile?> ResolveSimulationProfileAsync(
        CreateSimulationRequest body,
        ISimulationStrategyProfileStore store,
        CancellationToken cancellationToken)
    {
        if (body.SimulationProfileId.HasValue != body.SimulationProfileRevision.HasValue)
            throw new ArgumentException(
                "SimulationProfileId and SimulationProfileRevision must be supplied together.");
        if (body.SimulationProfileId is not Guid profileId ||
            body.SimulationProfileRevision is not int revision)
            return null;
        if (profileId == Guid.Empty || revision < 1)
            throw new ArgumentException("The selected simulation profile revision is invalid.");

        SimulationStrategyProfile profile = await store.ReadAsync(profileId, revision, cancellationToken)
            ?? throw new ArgumentException("The selected simulation profile revision does not exist.");
        profile.ValidateForExecution();
        return profile;
    }

    private static async Task<ResolvedCalibration> ResolveProfileCalibrationAsync(
        SimulationStrategyProfile profile,
        ICalibrationArtifactRepository repository,
        CancellationToken cancellationToken)
    {
        if (profile.Calibration.Mode == ExperimentCalibrationMode.Disabled)
            return new ResolvedCalibration();
        if (profile.Calibration.Mode == ExperimentCalibrationMode.TrainFreshPendingReviewOnly)
        {
            throw new ArgumentException(
                "This profile trains artifacts for review only and cannot be evaluated as a Single Run. " +
                "Run it as an Experiment or create a revision that reuses/uses calibration artifacts.");
        }
        if (profile.Calibration.Mode == ExperimentCalibrationMode.TrainFreshAndUseForHeldOutEvaluation)
            return new ResolvedCalibration();

        SetupCalibrationArtifact? setup = null;
        TradeManagementCalibration? management = null;
        MetaModelArtifact? metaModel = null;
        foreach (Guid artifactId in profile.Calibration.ArtifactIds)
        {
            CalibrationArtifactMetadata metadata = await repository
                .GetMetadataAsync(artifactId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new ArgumentException(
                    $"The profile references calibration artifact '{artifactId}' which no longer exists.");
            switch (metadata.Type)
            {
                case CalibrationArtifactType.Setup when setup is null:
                    setup = await repository.GetSetupAsync(artifactId, cancellationToken).ConfigureAwait(false)
                        ?? throw new ArgumentException($"Setup artifact '{artifactId}' has no stored content.");
                    break;
                case CalibrationArtifactType.Management when management is null:
                    management = await repository.GetManagementAsync(artifactId, cancellationToken).ConfigureAwait(false)
                        ?? throw new ArgumentException($"Management artifact '{artifactId}' has no stored content.");
                    break;
                case CalibrationArtifactType.MetaModel when metaModel is null:
                    metaModel = await repository.GetMetaModelAsync(artifactId, cancellationToken).ConfigureAwait(false)
                        ?? throw new ArgumentException($"Meta-model artifact '{artifactId}' has no stored content.");
                    break;
                default:
                    throw new ArgumentException(
                        $"The profile contains more than one '{metadata.Type}' calibration artifact.");
            }
        }

        return new ResolvedCalibration(setup, management, metaModel);
    }

    private static BacktestRequest ApplySimulationProfile(
        BacktestRequest request,
        SimulationStrategyProfile profile,
        SetupCalibrationArtifact? setup,
        TradeManagementCalibration? management,
        MetaModelArtifact? metaModel)
    {
        string strategyType = TradingAgentTypeIds.Format(profile.Agent.Kind);
        (decimal quantity, decimal rewardRisk) = profile.Agent.Kind switch
        {
            TradingAgentKind.LegacyProgressive or TradingAgentKind.ImprovedProgressive =>
                (profile.Agent.Progressive!.Quantity, profile.Agent.Progressive.MinimumRewardRisk),
            TradingAgentKind.StructuralConfluence =>
                (profile.Agent.StructuralConfluence!.Quantity,
                    profile.Agent.StructuralConfluence.MinimumRewardRisk),
            _ => throw new ArgumentOutOfRangeException(nameof(profile))
        };
        BacktestRuntimeOptions runtime = profile.Runtime.Options with
        {
            SourceKind = request.Runtime.SourceKind,
            ImportedCandlePath = request.Runtime.ImportedCandlePath,
            RefreshCache = request.Runtime.RefreshCache,
            NoCache = request.Runtime.NoCache,
            AnnotationOptions = profile.Analysis,
            LegacyPositionManagement = profile.Management,
            ImprovedPositionManagement = profile.Management,
            StructuralPositionManagement = profile.Management,
            MaximumParallelStrategies = 1,
            SetupCalibration = profile.Runtime.Options.SetupCalibration with { Enabled = setup is not null },
            SetupCalibrationArtifact = setup,
            ManagementCalibration = profile.Runtime.Options.ManagementCalibration with
            {
                Enabled = management is not null
            },
            ManagementCalibrationArtifact = management,
            MetaModel = profile.Runtime.Options.MetaModel with { Enabled = metaModel is not null },
            MetaModelArtifact = metaModel
        };

        return request with
        {
            Instrument = profile.Instrument,
            Strategies = [strategyType],
            StrategyAssignments =
            [
                new StrategyInstrumentAssignment
                {
                    Id = $"profile-{profile.ProfileId:N}-r{profile.Revision}",
                    StrategyType = strategyType,
                    Instrument = profile.Instrument,
                    AgentDefinitionOverride = profile.Agent,
                    AnalysisOptionsOverride = profile.Analysis
                }
            ],
            Quantity = quantity,
            MinimumRewardRisk = rewardRisk,
            Runtime = runtime
        };
    }

    private sealed record ResolvedCalibration(
        SetupCalibrationArtifact? Setup = null,
        TradeManagementCalibration? Management = null,
        MetaModelArtifact? MetaModel = null);

    private static async Task<SetupCalibrationArtifact?> ResolveSetupCalibrationArtifactAsync(
        string? id, ICalibrationArtifactRepository repository, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        if (!Guid.TryParse(id, out Guid parsed))
            throw new ArgumentException("SetupCalibrationArtifactId is invalid.");
        return await repository.GetSetupAsync(parsed, cancellationToken)
            ?? throw new ArgumentException("The referenced setup calibration artifact does not exist.");
    }

    private static async Task<TradeManagementCalibration?> ResolveManagementCalibrationArtifactAsync(
        string? id, ICalibrationArtifactRepository repository, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        if (!Guid.TryParse(id, out Guid parsed))
            throw new ArgumentException("ManagementCalibrationArtifactId is invalid.");
        return await repository.GetManagementAsync(parsed, cancellationToken)
            ?? throw new ArgumentException("The referenced management calibration artifact does not exist.");
    }

    private static async Task<MetaModelArtifact?> ResolveMetaModelArtifactAsync(
        string? id, ICalibrationArtifactRepository repository, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        if (!Guid.TryParse(id, out Guid parsed))
            throw new ArgumentException("MetaModelArtifactId is invalid.");
        return await repository.GetMetaModelAsync(parsed, cancellationToken)
            ?? throw new ArgumentException("The referenced meta-model artifact does not exist.");
    }

    private static string? TryNormalizePromotedStrategy(string value)
    {
        return TradingAgentTypeIds.TryParse(value, out TradingAgentKind kind)
            ? TradingAgentTypeIds.Format(kind)
            : null;
    }

    private static Guid DerivePolicyProfileId(
        Guid simulationId,
        string strategyId,
        string? configurationId)
    {
        string value = $"{simulationId:N}|{strategyId}|{configurationId ?? "unversioned"}";
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value));
        return new Guid(hash.AsSpan(0, 16));
    }
}

/// <summary>API-facing (plain-string) mirror of Simulator.Models.StrategyInstrumentAssignment.</summary>
public sealed record StrategyInstrumentAssignmentDto
{
    /// <summary>"legacy", "improved", or "structural-confluence".</summary>
    public required string StrategyType { get; init; }
    public required string Instrument { get; init; }
    public string? Id { get; init; }
    public TradingAgentDefinition? AgentDefinitionOverride { get; init; }
    public ProgressiveStrategyOptions? AgentOptionsOverride { get; init; }
}

public sealed record CreateSimulationRequest
{
    public CreateSimulationRequest()
    {
        (From, To) = RecommendedSimulationDefaults.PreviousFullMonth(DateTimeOffset.UtcNow);
    }

    /// <summary>Catalog broker ID: oanda, binance, or imported.</summary>
    public string? BrokerId { get; init; }
    /// <summary>Optional immutable strategy-profile identity. Must be paired with its exact revision.</summary>
    public Guid? SimulationProfileId { get; init; }
    public int? SimulationProfileRevision { get; init; }
    public string Instrument { get; init; } = RecommendedSimulationDefaults.Instrument;
    public DateTimeOffset From { get; init; }
    public DateTimeOffset To { get; init; }
    /// <summary>Legacy field; prefer ExecutionInterval / PrecisionMode.</summary>
    public string BaseInterval { get; init; } = "1m";
    public string? ExecutionInterval { get; init; }
    public string AnalysisBaseInterval { get; init; } = "1m";
    public string[] AnalysisIntervals { get; init; } = ["5m", "15m", "30m", "1h", "2h"];
    public string PrecisionMode { get; init; } = "Fast";
    public string SourceKind { get; init; } = "OandaCandles";
    /// <summary>
    /// Retained only so older clients receive a clear validation error. Server-local paths are never accepted.
    /// </summary>
    public string? ImportedCandlePath { get; init; }
    public string? ImportedDatasetId { get; init; }
    /// <summary>
    /// Server-owned setup-calibration artifact ID from POST /api/calibrations/setup. Never a
    /// raw artifact object or path - resolved server-side via ICalibrationArtifactRepository.
    /// </summary>
    public string? SetupCalibrationArtifactId { get; init; }
    /// <summary>
    /// Server-owned management-calibration artifact ID from POST /api/calibrations/management.
    /// Never a raw artifact object or path - resolved server-side via ICalibrationArtifactRepository.
    /// </summary>
    public string? ManagementCalibrationArtifactId { get; init; }
    /// <summary>
    /// Server-owned meta-model artifact ID from POST /api/calibrations/metamodel. Never a
    /// raw artifact object or path - resolved server-side via ICalibrationArtifactRepository.
    /// </summary>
    public string? MetaModelArtifactId { get; init; }

    /// <summary>
    /// When true (default) and no manual calibration artifact IDs are supplied, the server
    /// trains setup → meta → management on a past window before starting the simulation:
    /// train ends <see cref="AutoCalibrateEmbargoDays"/> before <see cref="From"/>, spanning
    /// <see cref="AutoCalibrateTrainMonths"/> calendar months. Strategy chains (legacy/improved)
    /// train in parallel; stages within each chain stay sequential for leakage safety.
    /// Manual artifact IDs always win and skip auto-calibration.
    /// </summary>
    public bool AutoCalibrateBeforeRun { get; init; } = true;

    /// <summary>Calendar months of history used for pre-run calibration (default 2).</summary>
    public int AutoCalibrateTrainMonths { get; init; } = PreRunCalibrationPlanner.DefaultTrainMonths;

    /// <summary>
    /// Gap in days between the end of the training window and <see cref="From"/> (default 10).
    /// Prevents training labels from leaking into the evaluation window.
    /// </summary>
    public int AutoCalibrateEmbargoDays { get; init; } = PreRunCalibrationPlanner.DefaultEmbargoDays;

    public string TrendInterval { get; init; } = "2h";
    public string[] SecondaryTrendIntervals { get; init; } = ["1h"];
    public string[] SetupIntervals { get; init; } = ["30m"];
    public string ConfirmationInterval { get; init; } = "15m";
    public string[] AdditionalConfirmationIntervals { get; init; } = [];
    public string EntryInterval { get; init; } = "5m";
    public int MinimumSecondaryTrendAlignments { get; init; }
    public int MinimumSetupAlignments { get; init; } = 1;
    public int MinimumConfirmationAlignments { get; init; } = 1;
    public bool StrongOppositionVeto { get; init; } = true;
    public string[] Strategies { get; init; } = ["legacy", "improved"];
    /// <summary>
    /// Optional §7 multi-instrument portfolio clock: assigns each strategy its own
    /// instrument instead of every strategy trading the single top-level Instrument field.
    /// Null/empty (the default) preserves today's single-instrument behaviour exactly -
    /// see BacktestRequest.StrategyAssignments.
    /// </summary>
    public StrategyInstrumentAssignmentDto[]? StrategyAssignments { get; init; }
    public decimal StartingBalance { get; init; } = 100_000m;
    public decimal Quantity { get; init; } = 1_000m;
    public string PositionSizingMode { get; init; } = "FixedFractionalRisk";
    public decimal FixedCashRisk { get; init; } = 250m;
    public decimal RiskPercentOfEquity { get; init; } =
        RecommendedSimulationDefaults.PositionSizing.RiskPercentOfEquity;
    public decimal MinimumQuantity { get; init; } = 1m;
    public decimal? MaximumQuantity { get; init; }
    public decimal QuantityStep { get; init; } = 1m;
    public decimal MaximumAccountMarginUsagePercent { get; init; } = 30m;
    public decimal MaximumSinglePositionMarginPercent { get; init; } = 10m;
    public decimal Leverage { get; init; } = 20m;
    public decimal CommissionRate { get; init; } = 0.00002m;
    public decimal SpreadBasisPoints { get; init; } = 1m;
    public decimal SlippageBasisPoints { get; init; } = 0.5m;
    public decimal MinimumRewardRisk { get; init; } = 1.5m;
    public string PriceActionConfirmation { get; init; } = "Soft";
    public decimal MinimumPriceActionConfidence { get; init; } = 55m;
    public bool RejectStrongOpposingPriceAction { get; init; } = true;
    public int WarmupDays { get; init; } = RecommendedSimulationDefaults.WarmupDays;
    public string StrategyExecutionMode { get; init; } = "ParallelWorkers";
    public string AmbiguousIntrabarPolicy { get; init; } = "ConservativeStopFirst";
    public string AccountMode { get; init; } = "IndependentStrategyAccounts";
    /// <summary>
    /// Enabled by default (2026-07-16 agent decision-quality pass) for requests that omit
    /// this field entirely - regime routing/risk is proven and tested. The Dashboard's own
    /// Simulator panel always sends an explicit value from its form state, so this default
    /// only governs direct API/raw-request callers, not the Dashboard UI's own presets.
    /// </summary>
    public bool RegimeEnabled { get; init; } = true;
    public int EfficiencyRatioPeriod { get; init; } = 14;
    public int RegimeConfirmationBars { get; init; } = 2;
    public int RegimePersistenceBars { get; init; } = 3;
    public decimal RegimeSoftSpreadAtr { get; init; } = SpreadAtrSafetyDefaults.SoftMaximum;
    public decimal RegimeHardSpreadAtr { get; init; } = SpreadAtrSafetyDefaults.HardMaximum;
    /// <summary>Causal chart-only monowave/hypothesis analysis.</summary>
    public bool NeoWaveEnabled { get; init; } = true;
    /// <summary>Disabled, RecordOnly, SoftConfidence, SoftRiskReduction, or SoftConfidenceAndRisk.</summary>
    public string NeoWaveEvidenceMode { get; init; } = "RecordOnly";
    public string? NeoWaveEvidenceInterval { get; init; }
    public decimal NeoWaveMinimumStructuralScore { get; init; } = 60m;
    public decimal NeoWaveMaximumTrustedConflictScore { get; init; } = 40m;
    public decimal NeoWaveMinimumRiskMultiplier { get; init; } = 0.75m;
    public decimal NeoWaveMaximumConflictRiskReduction { get; init; } = 0.25m;
    public bool TradingConditionsEnabled { get; init; } = true;
    public string[] AllowedSessions { get; init; } = ["Asian", "London", "NewYork", "LondonNewYorkOverlap"];
    public int RolloverBlackoutMinutesBefore { get; init; } = 15;
    public int RolloverBlackoutMinutesAfter { get; init; } = 15;
    public decimal ConditionSoftSpreadAtr { get; init; } = SpreadAtrSafetyDefaults.SoftMaximum;
    public decimal ConditionHardSpreadAtr { get; init; } = SpreadAtrSafetyDefaults.HardMaximum;
    public bool EconomicEventFilterEnabled { get; init; }
    public decimal MaximumTotalPortfolioHeatPercent { get; init; } = 1.5m;
    public decimal MaximumPendingRiskPercent { get; init; } = 0.75m;
    public decimal MaximumStrategyRiskPercent { get; init; } = 0.75m;
    public decimal MaximumInstrumentRiskPercent { get; init; } = 0.75m;
    public decimal MaximumCurrencyStopRiskPercent { get; init; } = 0.75m;
    public decimal MaximumNetCurrencyExposurePercent { get; init; } = 300m;
    public decimal MaximumGrossCurrencyExposurePercent { get; init; } = 300m;
    public decimal MinimumUnallocatedMarginReservePercent { get; init; } = 30m;
    public int MaximumOpenPositions { get; init; } = 3;
    public int CorrelationLookbackBars { get; init; } = 120;
    public int CorrelationMinimumSamples { get; init; } = 60;
    public decimal CorrelationSoftThreshold { get; init; } = 0.50m;
    public decimal CorrelationHardThreshold { get; init; } = 0.75m;
    /// <summary>
    /// Optional, independent value-location evidence (spec §13.3). Enabled by default
    /// (2026-07-16 agent decision-quality pass) for requests that omit this field - see the
    /// same caveat as <see cref="RegimeEnabled"/> about the Dashboard UI sending its own
    /// explicit value regardless of this default.
    /// </summary>
    public bool ValueLocationEvidenceEnabled { get; init; } = true;
    public decimal ValueLocationNearAtrThreshold { get; init; } = 0.5m;
    public decimal ValueLocationStretchedAtrThreshold { get; init; } = 2.5m;
    public decimal ValueLocationConfidenceAdjustment { get; init; } = 3m;
    /// <summary>Cross-market currency-strength coordinator (spec §5). Disabled by default;
    /// when enabled, CurrencyStrengthBaskets must contain at least one non-empty basket.</summary>
    public bool CurrencyStrengthEnabled { get; init; }
    public string CurrencyStrengthInterval { get; init; } = "1h";
    public int CurrencyStrengthReturnLookbackBars { get; init; } = 12;
    public int CurrencyStrengthVolatilityLookbackBars { get; init; } = 120;
    public decimal CurrencyStrengthMinimumCoveragePercent { get; init; } = 60m;
    public Dictionary<string, string[]> CurrencyStrengthBaskets { get; init; } = [];
    /// <summary>
    /// Drawdown/volatility-scaled risk-budget multiplier. Enabled by default
    /// (2026-07-16 agent decision-quality pass) for requests that omit this field - see the
    /// same caveat as <see cref="RegimeEnabled"/> about the Dashboard UI sending its own
    /// explicit value regardless of this default.
    /// </summary>
    public bool AdaptiveRiskEnabled { get; init; } = true;
    public string ExecutionFillModel { get; init; } = "MidpointPlusConfiguredSpread";
    public string StressExecutionScenario { get; init; } = "Base";
    public decimal? MaximumFillQuantityPerFrame { get; init; }
    public decimal MaximumFillParticipationFraction { get; init; } = 1m;
    public decimal AsianSessionSpreadMultiplier { get; init; } = 1.20m;
    public decimal RolloverSpreadMultiplier { get; init; } = 3m;
    public decimal VolatilitySlippageFraction { get; init; }
    public decimal GapSlippageFraction { get; init; }
    public bool FinancingEnabled { get; init; }
    public IReadOnlyDictionary<string, FinancingRate> FinancingRates { get; init; } =
        new Dictionary<string, FinancingRate>(StringComparer.OrdinalIgnoreCase);
    public bool RefreshCache { get; init; }
    public bool NoCache { get; init; }
    /// <summary>
    /// Whether to build the chart replay chunks for this run. Measured locally at roughly a
    /// third of throughput (candles/second) - true by default so the Dashboard replay chart
    /// keeps working out of the box, but callers that only need metrics/trades can turn it off.
    /// </summary>
    public bool CaptureMarketReplay { get; init; } = true;
    public PositionManagementRequest LegacyPositionManagement { get; init; } =
        PositionManagementRequest.LegacyDefaults;
    public PositionManagementRequest ImprovedPositionManagement { get; init; } =
        PositionManagementRequest.ImprovedDefaults;

    /// <summary>
    /// Optional account-currency profit at which new entries are paused for the rest
    /// of the UTC day. Existing positions remain managed and protected.
    /// </summary>
    public decimal? DailyEquityProfitTarget { get; init; }

    /// <summary>
    /// Optional daily equity-profit level that activates peak-giveback protection.
    /// Must be supplied together with MaximumDailyEquityGiveback.
    /// </summary>
    public decimal? DailyEquityGivebackActivation { get; init; }

    /// <summary>
    /// Optional maximum account-currency giveback from the daily equity peak.
    /// Must be supplied together with DailyEquityGivebackActivation.
    /// </summary>
    public decimal? MaximumDailyEquityGiveback { get; init; }

    /// <summary>
    /// Persistent (non-daily-resetting) equity-protection tier. Configures a single
    /// simple tier; multi-tier setups remain programmatic/JSON-only.
    /// </summary>
    public bool EquityProtectionEnabled { get; init; }
    public decimal? EquityProtectionActivationProfitPercent { get; init; }
    public decimal? EquityProtectionMaxGivebackPercent { get; init; }
    public string EquityProtectionActionType { get; init; } = "PauseNewEntries";
    public decimal EquityProtectionReductionFraction { get; init; } = 0.25m;
    public decimal EquityProtectionFutureRiskMultiplier { get; init; } = 0.5m;
    public int EquityProtectionRecoveryBars { get; init; } = 3;

    public string ResolveBrokerId()
    {
        if (!string.IsNullOrWhiteSpace(BrokerId))
            return BrokerId.Trim().ToLowerInvariant();
        return SourceKind.ToUpperInvariant() switch
        {
            "BINANCECANDLES" => "binance",
            "IMPORTEDSECONDCANDLES" or "RECORDEDQUOTES" => "imported",
            _ => "oanda"
        };
    }

    /// <summary>
    /// Auto-calibration runs only when requested and the caller has not already attached
    /// manual setup/meta/management artifact IDs (those always take precedence).
    /// </summary>
    public bool ShouldAutoCalibrate(
        SetupCalibrationArtifact? setup,
        TradeManagementCalibration? management,
        MetaModelArtifact? metaModel) =>
        AutoCalibrateBeforeRun &&
        setup is null &&
        management is null &&
        metaModel is null;

    public BacktestRequest ToBacktestRequest(
        SimulationBrokerOption selectedBroker,
        SetupCalibrationArtifact? resolvedSetupCalibrationArtifact = null,
        TradeManagementCalibration? resolvedManagementCalibrationArtifact = null,
        MetaModelArtifact? resolvedMetaModelArtifact = null)
    {
        ArgumentNullException.ThrowIfNull(selectedBroker);
        string solutionRoot = FindSolutionRoot() ?? Directory.GetCurrentDirectory();
        var precision = Enum.Parse<Simulator.MarketData.SimulationPrecisionMode>(PrecisionMode, ignoreCase: true);
        var source = Enum.Parse<Simulator.MarketData.HistoricalDataSourceKind>(selectedBroker.SourceKind, ignoreCase: true);
        SimulationTimeframeOptions fromPrecision =
            SimulationTimeframeOptions.FromPrecision(precision, AnalysisIntervals.Select(BarIntervalParser.Parse).ToArray());

        BarInterval execution = !string.IsNullOrWhiteSpace(ExecutionInterval)
            ? BarIntervalParser.Parse(ExecutionInterval)
            : precision == Simulator.MarketData.SimulationPrecisionMode.Fast &&
              !string.IsNullOrWhiteSpace(BaseInterval)
                ? BarIntervalParser.Parse(BaseInterval)
                : fromPrecision.ExecutionInterval;

        BarInterval analysisBase = !string.IsNullOrWhiteSpace(AnalysisBaseInterval)
            ? BarIntervalParser.Parse(AnalysisBaseInterval)
            : fromPrecision.AnalysisBaseInterval;

        if (!string.IsNullOrWhiteSpace(ImportedCandlePath))
            throw new ArgumentException(
                "ImportedCandlePath is not accepted. Upload or select a server-owned ImportedDatasetId.");

        string? importedCandlePath = null;
        if (!string.IsNullOrWhiteSpace(ImportedDatasetId))
        {
            if (!Guid.TryParseExact(ImportedDatasetId, "N", out Guid datasetId))
                throw new ArgumentException("ImportedDatasetId is invalid.");
            string importRoot = Path.Combine(solutionRoot, ".cache", "imported-candles");
            string canonicalId = datasetId.ToString("N");
            string metadataPath = Path.Combine(importRoot, canonicalId + ".metadata.json");
            importedCandlePath = Path.Combine(importRoot, canonicalId + ".csv");
            if (!File.Exists(metadataPath) || !File.Exists(importedCandlePath))
                throw new ArgumentException("The imported dataset does not exist or has expired.");

            ImportedDatasetMetadata metadata;
            try
            {
                metadata = JsonSerializer.Deserialize<ImportedDatasetMetadata>(
                               File.ReadAllText(metadataPath))
                           ?? throw new JsonException("Dataset metadata was empty.");
            }
            catch (JsonException exception)
            {
                throw new ArgumentException("The imported dataset metadata is invalid.", exception);
            }

            if (metadata.ExpiresAt <= DateTimeOffset.UtcNow)
                throw new ArgumentException("The imported dataset has expired.");
            if (BarIntervalParser.Parse(metadata.Interval) != execution)
                throw new ArgumentException(
                    $"Imported dataset interval {metadata.Interval} does not match execution interval " +
                    $"{BarIntervalParser.Format(execution)}.");
        }

        BrokerEnvironment environment = ResolveEnvironment(selectedBroker);

        return new BacktestRequest
        {
            Instrument = new InstrumentKey(Instrument),
            Environment = environment,
            From = From,
            To = To,
            Strategies = Strategies,
            StrategyAssignments = StrategyAssignments?.Select(assignment => new StrategyInstrumentAssignment
            {
                StrategyType = assignment.StrategyType,
                Instrument = new InstrumentKey(assignment.Instrument),
                Id = assignment.Id,
                AgentDefinitionOverride = assignment.AgentDefinitionOverride,
                AgentOptionsOverride = assignment.AgentOptionsOverride
            }).ToArray(),
            StartingBalance = StartingBalance,
            Quantity = Quantity,
            Leverage = Leverage,
            CommissionRate = CommissionRate,
            SpreadBasisPoints = SpreadBasisPoints,
            SlippageBasisPoints = SlippageBasisPoints,
            MinimumRewardRisk = MinimumRewardRisk,
            PriceActionConfirmation = Enum.Parse<PriceActionConfirmationMode>(PriceActionConfirmation, ignoreCase: true),
            MinimumPriceActionConfidence = MinimumPriceActionConfidence,
            RejectStrongOpposingPriceAction = RejectStrongOpposingPriceAction,
            CaptureMarketReplay = CaptureMarketReplay,
            OutputDirectory = Path.Combine(solutionRoot, "Dashboard", "public", "data", "simulations"),
            CacheDirectory = Path.Combine(solutionRoot, ".cache", "historical"),
            JobsDirectory = Path.Combine(solutionRoot, ".cache", "simulation-jobs"),
            Runtime = new BacktestRuntimeOptions
            {
                AccountMode = Enum.Parse<SimulationAccountMode>(AccountMode, ignoreCase: true),
                ExecutionInterval = execution,
                AnalysisBaseInterval = analysisBase,
                AnalysisIntervals = AnalysisIntervals.Select(BarIntervalParser.Parse).ToArray(),
                PrecisionMode = precision,
                SourceKind = source,
                ImportedCandlePath = importedCandlePath,
                StrategyTimeframes = new ProgressiveStrategyTimeframes
                {
                    TrendInterval = BarIntervalParser.Parse(TrendInterval),
                    SecondaryTrendIntervals = SecondaryTrendIntervals
                        .Select(BarIntervalParser.Parse)
                        .ToArray(),
                    SetupIntervals = SetupIntervals
                        .Select(BarIntervalParser.Parse)
                        .ToArray(),
                    ConfirmationInterval = BarIntervalParser.Parse(ConfirmationInterval),
                    AdditionalConfirmationIntervals = AdditionalConfirmationIntervals
                        .Select(BarIntervalParser.Parse)
                        .ToArray(),
                    EntryInterval = BarIntervalParser.Parse(EntryInterval),
                    MinimumSecondaryTrendAlignments = MinimumSecondaryTrendAlignments,
                    MinimumSetupAlignments = MinimumSetupAlignments,
                    MinimumConfirmationAlignments = MinimumConfirmationAlignments,
                    StrongOppositionVeto = StrongOppositionVeto
                },
                WarmupDays = WarmupDays,
                StrategyExecutionMode = Enum.Parse<StrategyExecutionMode>(StrategyExecutionMode, ignoreCase: true),
                AmbiguousIntrabarPolicy = Enum.Parse<AmbiguousIntrabarPolicy>(AmbiguousIntrabarPolicy, ignoreCase: true),
                RefreshCache = RefreshCache,
                NoCache = NoCache,
                LegacyPositionManagement = LegacyPositionManagement.ToOptions(
                    EntryInterval,
                    PositionManagementOptions.LegacyDefaults),
                ImprovedPositionManagement = ImprovedPositionManagement.ToOptions(
                    EntryInterval,
                    PositionManagementOptions.ImprovedDefaults),
                PositionSizing = new PositionSizingOptions
                {
                    Mode = Enum.Parse<RiskManager.PositionSizingMode>(this.PositionSizingMode, ignoreCase: true),
                    FixedQuantity = Quantity,
                    FixedCashRisk = FixedCashRisk,
                    RiskPercentOfEquity = RiskPercentOfEquity,
                    MinimumQuantity = MinimumQuantity,
                    MaximumQuantity = MaximumQuantity is null or <= 0m ? null : MaximumQuantity,
                    QuantityStep = QuantityStep,
                    MaximumAccountMarginUsagePercent = MaximumAccountMarginUsagePercent,
                    MaximumSinglePositionMarginPercent = MaximumSinglePositionMarginPercent,
                    Leverage = Leverage,
                    EstimatedRoundTripCostBasisPoints = SpreadBasisPoints +
                        2m * SlippageBasisPoints +
                        2m * CommissionRate * 10_000m
                },
                SafetyOptions = new TradingSafetyOptions
                {
                    DailyEquityProfitTarget = NormaliseOptionalPositive(
                        DailyEquityProfitTarget,
                        nameof(DailyEquityProfitTarget)),
                    DailyEquityGivebackActivation = NormaliseOptionalPositive(
                        DailyEquityGivebackActivation,
                        nameof(DailyEquityGivebackActivation)),
                    MaximumDailyEquityGiveback = NormaliseOptionalPositive(
                        MaximumDailyEquityGiveback,
                        nameof(MaximumDailyEquityGiveback)),
                    EquityProtection = ResolveEquityProtection()
                },
                AnnotationOptions = new ChartAnnotationOptions
                {
                    EfficiencyRatioPeriod = EfficiencyRatioPeriod,
                    MarketRegime = new MarketRegimeOptions
                    {
                        Enabled = RegimeEnabled,
                        MinimumConfirmationBars = RegimeConfirmationBars,
                        MinimumPersistenceBars = RegimePersistenceBars,
                        MaximumTradeableSpreadAtr = RegimeSoftSpreadAtr,
                        HardMaximumSpreadAtr = RegimeHardSpreadAtr
                    },
                    NeoWave = new NeoWaveOptions
                    {
                        Enabled = NeoWaveEnabled
                    },
                    // Dashboard chart layers expect zones/pools; detection defaults off for backtest identity.
                    SupplyDemand = new SupplyDemandCalculationProfile { Enabled = true },
                    Liquidity = new LiquidityCalculationProfile { Enabled = true },
                    SupplyDemandLiquidityConfluence = new SupplyDemandLiquidityConfluenceOptions { Enabled = true }
                },
                MarketRegimeRouting = new MarketRegimePolicyOptions { Enabled = RegimeEnabled },
                RegimeManagement = new RegimeManagementOptions { Enabled = RegimeEnabled },
                TradingConditions = new TradingConditionOptions
                {
                    Enabled = TradingConditionsEnabled,
                    AllowedSessions = AllowedSessions
                        .Select(value => Enum.Parse<TradingSession>(value, ignoreCase: true))
                        .Distinct()
                        .ToArray(),
                    RolloverBlackoutMinutesBefore = RolloverBlackoutMinutesBefore,
                    RolloverBlackoutMinutesAfter = RolloverBlackoutMinutesAfter,
                    SoftMaximumSpreadAtr = ConditionSoftSpreadAtr,
                    HardMaximumSpreadAtr = ConditionHardSpreadAtr,
                    EconomicEventFilterEnabled = EconomicEventFilterEnabled
                },
                PortfolioRisk = new PortfolioRiskOptions
                {
                    MaximumTotalOpenRiskPercent = MaximumTotalPortfolioHeatPercent,
                    MaximumPendingRiskPercent = MaximumPendingRiskPercent,
                    MaximumStrategyRiskPercent = MaximumStrategyRiskPercent,
                    MaximumInstrumentRiskPercent = MaximumInstrumentRiskPercent,
                    MaximumCurrencyStopRiskPercent = MaximumCurrencyStopRiskPercent,
                    MaximumNetCurrencyExposurePercent = MaximumNetCurrencyExposurePercent,
                    MaximumGrossCurrencyExposurePercent = MaximumGrossCurrencyExposurePercent,
                    MaximumMarginUsagePercent = MaximumAccountMarginUsagePercent,
                    MaximumSinglePositionMarginPercent = MaximumSinglePositionMarginPercent,
                    MinimumUnallocatedMarginReservePercent = MinimumUnallocatedMarginReservePercent,
                    MaximumOpenPositions = MaximumOpenPositions
                },
                CorrelationRisk = new CorrelationRiskOptions
                {
                    LookbackBars = CorrelationLookbackBars,
                    MinimumSamples = CorrelationMinimumSamples,
                    SoftCorrelationThreshold = CorrelationSoftThreshold,
                    HardCorrelationThreshold = CorrelationHardThreshold
                },
                ValueLocationEvidence = new ValueLocationEvidenceOptions
                {
                    Enabled = ValueLocationEvidenceEnabled,
                    NearValueAtrThreshold = ValueLocationNearAtrThreshold,
                    StretchedFromValueAtrThreshold = ValueLocationStretchedAtrThreshold,
                    ConfidenceAdjustmentPerSignal = ValueLocationConfidenceAdjustment
                },
                NeoWaveEvidence = new NeoWaveEvidenceOptions
                {
                    // Property NeoWaveEvidenceMode (string) shadows the enum type — qualify fully.
                    Mode = NeoWaveEnabled
                        ? Enum.Parse<ChartAnnotator.NeoWave.NeoWaveEvidenceMode>(NeoWaveEvidenceMode, ignoreCase: true)
                        : ChartAnnotator.NeoWave.NeoWaveEvidenceMode.Disabled,
                    MinimumStructuralScore = NeoWaveMinimumStructuralScore,
                    MaximumTrustedConflictScore = NeoWaveMaximumTrustedConflictScore,
                    MinimumRiskMultiplier = NeoWaveMinimumRiskMultiplier,
                    MaximumConflictRiskReduction = NeoWaveMaximumConflictRiskReduction
                },
                NeoWaveEvidenceInterval = string.IsNullOrWhiteSpace(NeoWaveEvidenceInterval)
                    ? null
                    : BarIntervalParser.Parse(NeoWaveEvidenceInterval),
                CurrencyStrength = new CurrencyStrengthOptions
                {
                    Enabled = CurrencyStrengthEnabled,
                    Interval = BarIntervalParser.Parse(CurrencyStrengthInterval),
                    ReturnLookbackBars = CurrencyStrengthReturnLookbackBars,
                    VolatilityLookbackBars = CurrencyStrengthVolatilityLookbackBars,
                    MinimumCurrencyCoveragePercent = CurrencyStrengthMinimumCoveragePercent,
                    Baskets = CurrencyStrengthBaskets.ToDictionary(
                        pair => pair.Key,
                        pair => (IReadOnlyList<InstrumentKey>)pair.Value.Select(value => new InstrumentKey(value)).ToArray())
                },
                AdaptiveRisk = new AdaptiveRiskOptions { Enabled = AdaptiveRiskEnabled },
                SetupCalibration = new SetupCalibrationPolicyOptions
                {
                    Enabled = resolvedSetupCalibrationArtifact is not null
                },
                SetupCalibrationArtifact = resolvedSetupCalibrationArtifact,
                ManagementCalibration = new TradeManagementCalibrationOptions
                {
                    Enabled = resolvedManagementCalibrationArtifact is not null
                },
                ManagementCalibrationArtifact = resolvedManagementCalibrationArtifact,
                MetaModel = new MetaModelPolicyOptions { Enabled = resolvedMetaModelArtifact is not null },
                MetaModelArtifact = resolvedMetaModelArtifact,
                Execution = new ExecutionModelOptions
                {
                    FillModel = Enum.Parse<SimulationFillModel>(ExecutionFillModel, ignoreCase: true),
                    StressScenario = Enum.Parse<StressExecutionScenario>(StressExecutionScenario, ignoreCase: true),
                    FillCapacity = new FillCapacityModel
                    {
                        MaximumQuantityPerExecutionFrame = MaximumFillQuantityPerFrame,
                        MaximumParticipationFraction = MaximumFillParticipationFraction
                    },
                    AsianSessionSpreadMultiplier = AsianSessionSpreadMultiplier,
                    RolloverSpreadMultiplier = RolloverSpreadMultiplier,
                    VolatilitySlippageFraction = VolatilitySlippageFraction,
                    GapSlippageFraction = GapSlippageFraction
                },
                Financing = new FinancingOptions
                {
                    Enabled = FinancingEnabled,
                    InstrumentRates = FinancingRates
                },
                ReplayChunkSize = 250,
                ProgressPublishIntervalMilliseconds = 500
            }
        };
    }

    private static BrokerEnvironment ResolveEnvironment(SimulationBrokerOption broker)
    {
        if (string.Equals(broker.Id, "binance", StringComparison.OrdinalIgnoreCase))
            return BrokerEnvironment.Live;
        if (string.Equals(broker.Id, "imported", StringComparison.OrdinalIgnoreCase))
            return BrokerEnvironment.Demo;
        return Enum.TryParse(broker.Environment, ignoreCase: true, out BrokerEnvironment environment)
            ? environment
            : BrokerEnvironment.Demo;
    }

    private static decimal? NormaliseOptionalPositive(decimal? value, string fieldName)
    {
        if (value is null or 0m)
            return null;
        if (value < 0m)
            throw new ArgumentOutOfRangeException(fieldName, "Value cannot be negative.");
        return value;
    }

    private EquityProtectionOptions ResolveEquityProtection()
    {
        if (!EquityProtectionEnabled || EquityProtectionMaxGivebackPercent is not decimal givebackPercent)
        {
            return new EquityProtectionOptions { Enabled = EquityProtectionEnabled };
        }

        var action = Enum.Parse<EquityProtectionAction>(EquityProtectionActionType, ignoreCase: true);
        return new EquityProtectionOptions
        {
            Enabled = true,
            RecoveryConfirmationBars = EquityProtectionRecoveryBars,
            Tiers =
            [
                new EquityProtectionTier
                {
                    TierId = "dashboard-tier",
                    ActivationProfitPercent = EquityProtectionActivationProfitPercent,
                    MaximumGivebackPercent = givebackPercent,
                    Action = action,
                    ReductionFraction = EquityProtectionReductionFraction,
                    FutureRiskMultiplier = EquityProtectionFutureRiskMultiplier
                }
            ]
        };
    }

    private static string? FindSolutionRoot()
    {
        DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TradingHub.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        return null;
    }
}

public sealed record PositionManagementRequest
{
    public string Mode { get; init; } = "StructureAtr";
    public string? ManagementInterval { get; init; }
    public bool? EvaluateMechanicalProtectionOnEveryExecutionFrame { get; init; }
    public string? FastStructureInterval { get; init; }
    public string? MainStructureInterval { get; init; }
    public string? ThesisInterval { get; init; }
    public decimal BreakEvenActivationR { get; init; } = 1m;
    public decimal StructureTrailActivationR { get; init; } = 1.5m;
    public decimal AtrBufferMultiplier { get; init; } = 0.25m;
    public decimal BreakEvenBufferAtr { get; init; } = 0.05m;
    public decimal MinimumStopImprovementAtr { get; init; } = 0.05m;
    public decimal MinimumStopImprovementTicks { get; init; } = 1m;
    public int MinimumAnalysisBarsBetweenAmendments { get; init; } = 1;
    public bool ExitOnAdverseStructureBreak { get; init; }
    public bool? EnableNeoWaveInvalidationExit { get; init; }
    public decimal? NeoWaveInvalidationBufferAtr { get; init; }
    public bool PreserveBracketTarget { get; init; } = true;
    public bool IncludeEstimatedExitCostsAtBreakEven { get; init; } = true;
    public bool? EnableScaleOut { get; init; }
    public decimal? MinimumRunnerFraction { get; init; }
    public bool? EnableProfitFloor { get; init; }
    public bool? EnableMaximumGiveback { get; init; }
    public bool? EnableStagnationReduction { get; init; }
    public bool? EnableStructuralDeteriorationReduction { get; init; }
    public bool? EnableMomentumDecayReduction { get; init; }
    public bool? EnableVolatilityExhaustionReduction { get; init; }
    public bool? EnableRiskWindowReduction { get; init; }
    public string? RiskWindowStartUtc { get; init; }
    public string? RiskWindowEndUtc { get; init; }
    public bool? EnableExecutionCostStressReduction { get; init; }

    public static PositionManagementRequest LegacyDefaults { get; } = new()
    {
        StructureTrailActivationR = 1.5m,
        PreserveBracketTarget = false
    };

    public static PositionManagementRequest ImprovedDefaults { get; } = new()
    {
        StructureTrailActivationR = 2m,
        PreserveBracketTarget = true
    };

    public PositionManagementOptions ToOptions(
        string defaultManagementInterval,
        PositionManagementOptions defaults) => defaults with
    {
        Mode = Enum.Parse<TrailingStopMode>(Mode, ignoreCase: true),
        ManagementInterval = BarIntervalParser.Parse(
            string.IsNullOrWhiteSpace(ManagementInterval)
                ? defaultManagementInterval
                : ManagementInterval),
        EvaluateMechanicalProtectionOnEveryExecutionFrame =
            EvaluateMechanicalProtectionOnEveryExecutionFrame ??
            defaults.EvaluateMechanicalProtectionOnEveryExecutionFrame,
        FastStructureInterval = ParseOptionalInterval(FastStructureInterval) ??
            defaults.FastStructureInterval ?? BarIntervalParser.Parse(defaultManagementInterval),
        MainStructureInterval = ParseOptionalInterval(MainStructureInterval) ??
            ParseOptionalInterval(ManagementInterval) ?? defaults.MainStructureInterval ??
            BarIntervalParser.Parse(defaultManagementInterval),
        ThesisInterval = ParseOptionalInterval(ThesisInterval) ?? defaults.ThesisInterval,
        BreakEvenActivationR = BreakEvenActivationR,
        StructureTrailActivationR = StructureTrailActivationR,
        AtrBufferMultiplier = AtrBufferMultiplier,
        BreakEvenBufferAtr = BreakEvenBufferAtr,
        MinimumStopImprovementAtr = MinimumStopImprovementAtr,
        MinimumStopImprovementTicks = MinimumStopImprovementTicks,
        MinimumAnalysisBarsBetweenAmendments = MinimumAnalysisBarsBetweenAmendments,
        ExitOnAdverseStructureBreak = ExitOnAdverseStructureBreak,
        EnableNeoWaveInvalidationExit =
            EnableNeoWaveInvalidationExit ?? defaults.EnableNeoWaveInvalidationExit,
        NeoWaveInvalidationBufferAtr =
            NeoWaveInvalidationBufferAtr ?? defaults.NeoWaveInvalidationBufferAtr,
        PreserveBracketTarget = PreserveBracketTarget,
        IncludeEstimatedExitCostsAtBreakEven = IncludeEstimatedExitCostsAtBreakEven,
        EnableScaleOut = EnableScaleOut ?? defaults.EnableScaleOut,
        MinimumRunnerFraction = MinimumRunnerFraction ?? defaults.MinimumRunnerFraction,
        EnableProfitFloor = EnableProfitFloor ?? defaults.EnableProfitFloor,
        EnableMaximumGiveback = EnableMaximumGiveback ?? defaults.EnableMaximumGiveback,
        EnableStagnationReduction =
            EnableStagnationReduction ?? defaults.EnableStagnationReduction,
        EnableStructuralDeteriorationReduction =
            EnableStructuralDeteriorationReduction ?? defaults.EnableStructuralDeteriorationReduction,
        EnableMomentumDecayReduction =
            EnableMomentumDecayReduction ?? defaults.EnableMomentumDecayReduction,
        EnableVolatilityExhaustionReduction =
            EnableVolatilityExhaustionReduction ?? defaults.EnableVolatilityExhaustionReduction,
        EnableRiskWindowReduction =
            EnableRiskWindowReduction ?? defaults.EnableRiskWindowReduction,
        RiskWindowStartUtc = ParseOptionalTime(RiskWindowStartUtc) ?? defaults.RiskWindowStartUtc,
        RiskWindowEndUtc = ParseOptionalTime(RiskWindowEndUtc) ?? defaults.RiskWindowEndUtc,
        EnableExecutionCostStressReduction =
            EnableExecutionCostStressReduction ?? defaults.EnableExecutionCostStressReduction
    };

    private static BarInterval? ParseOptionalInterval(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : BarIntervalParser.Parse(value);

    private static TimeOnly? ParseOptionalTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (!TimeOnly.TryParse(value, out TimeOnly parsed))
            throw new ArgumentException($"Invalid UTC time '{value}'. Use HH:mm.");
        return parsed;
    }
}

public sealed record PromoteTradingPolicyRequest
{
    public int Revision { get; init; } = 1;
    public bool ApproveForDemo { get; init; }
    public string? StrategyVersion { get; init; }
    public Guid? SetupCalibrationArtifactId { get; init; }
    public Guid? ManagementCalibrationArtifactId { get; init; }
    public Guid? MetaModelArtifactId { get; init; }
    public string? Description { get; init; }
}

internal sealed record TradeIndexDto(
    int SchemaVersion,
    string? StrategyId,
    IReadOnlyList<TradeIndexEntryDto?>? Items);

internal sealed record TradeIndexEntryDto(
    int Ordinal,
    long Offset,
    int Length,
    string SetupId,
    DateTimeOffset? ClosedAt);

internal sealed record DashboardReplayRow(
    long Sequence,
    DateTimeOffset AvailableAt,
    DateTimeOffset OpenTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    IReadOnlyList<string> ClosedIntervals,
    bool IsWarmup,
    AnalysisSnapshot? Analysis,
    string? Instrument = null);

public sealed record ImportedDatasetMetadata(
    string DatasetId,
    string FileName,
    string Interval,
    long Rows,
    long SizeBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);
