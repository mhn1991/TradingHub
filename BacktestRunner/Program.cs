using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agent.Configuration;
using Agent.Strategies;
using Agent.Strategies.StructuralConfluence;
using Brokers.Models;
using ChartAnnotator.Engine;
using Dashboard.Contracts;
using DBManager.Abstractions.Credentials;
using DBManager.Postgres.Security;
using QuantResearch.Training.Pipeline;
using RiskManager.Conditions;
using Simulator.Calibration;
using Simulator.Experiments.IndicatorCalibration;
using Simulator.Experiments.IndicatorCalibration.Strategies;
using Simulator.Jobs;
using Simulator.Models;
using Simulator.Services;
using TradeManager;
using TradingCore.Pipeline;
using TradingPolicies;

namespace BacktestRunner;

/// <summary>
/// Thin CLI adapter over <see cref="IBacktestApplicationService"/>. Dashboard and CLI
/// share the same job/engine path.
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<int> Main(string[] args)
    {
        if (IndicatorCalibrationCli.Matches(args))
        {
            using var calibrationCancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                calibrationCancellation.Cancel();
            };
            return await IndicatorCalibrationCli.RunAsync(args, calibrationCancellation.Token).ConfigureAwait(false);
        }

        try
        {
            string? requestJsonPath = ExtractRequestJsonPath(args);
            BacktestCommandOptions options = requestJsonPath is null
                ? BacktestCommandOptions.Parse(args)
                : new BacktestCommandOptions
                {
                    OutputDirectory = Path.Combine("Dashboard", "public", "data", "simulations"),
                    StrategyExecution = "sequential",
                    CaptureMarketReplay = false
                };
            if (options.ShowHelp)
            {
                PrintHelp();
                return 0;
            }

            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };

            // Must happen before any agent is built. Without it the ML agents resolve to a no-trade
            // model and the run reports 0 trades with no error, which is indistinguishable from the
            // strategy genuinely finding nothing (PROJECT_STATE §3.23).
            string? modelDescription = ClassifierModelInstaller.Install(
                options.ClassifierModelPath, options.ExecutionInterval.Value, options.StopModelPath);
            if (options.StopModelPath is not null)
                Console.WriteLine($"Stop-placement model loaded from {options.StopModelPath}");
            if (modelDescription is not null)
            {
                Console.WriteLine($"Classifier model loaded from {options.ClassifierModelPath}");
                Console.WriteLine(modelDescription);
                // The agent is configured FROM the model, not alongside it.
                options = options with
                {
                    ClassifierOptions = ClassifierModelInstaller.LoadedOptions,
                    ClassifierSignalInterval = ClassifierModelInstaller.LoadedIntervalMinutes is int minutes
                        ? BarInterval.Minutes(minutes)
                        : null
                };
            }
            else if (options.Strategies.Any(strategy =>
                strategy.Contains("classification", StringComparison.OrdinalIgnoreCase) ||
                strategy.Contains("trend-tactical", StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine(
                    "WARNING: an ML agent was requested without --classifier-model. It will fall back " +
                    "to a no-trade model and produce ZERO trades. This is not a strategy result.");
            }

            options = await ResolveCalibrationArtifactsAsync(options, cancellation.Token);
            BacktestRequest request = requestJsonPath is null
                ? options.ToBacktestRequest()
                : await LoadRequestJsonAsync(requestJsonPath, cancellation.Token).ConfigureAwait(false);
            if (args.Any(item => string.Equals(item, "--pullback-only", StringComparison.OrdinalIgnoreCase)))
            {
                request = WithPullbackOnlyStructuralAgent(request);
                Console.WriteLine(
                    "Playbook filter: supply/demand pullback only (sweep + break/retest disabled); " +
                    "RequireTrendAlignment=false (step A).");
            }

            if (args.Any(item => string.Equals(item, "--candidate-fixes", StringComparison.OrdinalIgnoreCase)))
            {
                request = WithCandidateFixes(request);
                Console.WriteLine(
                    "Candidate fixes applied (2026-07-27 review): strict break/retest routing " +
                    "(AllowBreakRetestAfterBreakoutTransition=false, now the default), " +
                    "IndicatorConfluence disabled, soft spread/ATR limit rejects entries instead of half-sizing.");
            }

            if (options.AutoApplyIndicatorCalibration)
            {
                request = await ApplyAutoCalibrationPinsAsync(
                    request, options.CalibrationArtifactsDirectory, cancellation.Token).ConfigureAwait(false);
            }
            request = await ResolveIndicatorCalibrationOverlaysAsync(
                request, options.CalibrationArtifactsDirectory, cancellation.Token).ConfigureAwait(false);

            BrokerCredentialDatabaseConfiguration? databaseConfiguration =
                BrokerCredentialDatabaseConfiguration.TryLoad(Directory.GetCurrentDirectory(), out string repositoryRoot);
            IBrokerCredentialStore? credentialStore = databaseConfiguration?.OpenStore(repositoryRoot);

            await using var service = new BacktestApplicationService(
                new FileSimulationJobRepository(request.JobsDirectory),
                new BacktestApplicationServiceOptions
                {
                    MaxConcurrentJobs = 1,
                    QueueCapacity = 4,
                    CredentialResolver = credentialStore is null
                        ? null
                        : async (backtest, cancellationToken) =>
                        {
                            string environment = backtest.Environment == Brokers.Abstractions.BrokerEnvironment.Live
                                ? "LIVE"
                                : "DEMO";
                            string broker = backtest.Runtime.SourceKind == Simulator.MarketData.HistoricalDataSourceKind.BinanceCandles
                                ? "BINANCE"
                                : "OANDA";
                            BrokerCredential? credential = await credentialStore
                                .GetAsync(
                                    broker,
                                    broker == "BINANCE" && environment == "DEMO" ? "TESTNET" : environment,
                                    cancellationToken: cancellationToken)
                                .ConfigureAwait(false);
                            return credential is null
                                ? null
                                : new HistoricalBrokerCredentials(
                                    credential.AccountId,
                                    credential.AccessToken,
                                    credential.ApiKey,
                                    credential.SecretKey);
                        }
                });

            int progressLinesDrawn = 0;
            bool progressInteractive = !Console.IsOutputRedirected;
            var progress = new Progress<BacktestProgress>(update =>
            {
                var block = new StringBuilder();
                block.AppendLine(
                    $"[{JobStatusLabel(update.Status)}] {update.CurrentMarketTime:u} · " +
                    $"[{AsciiBar(update.ProgressPercent)}] {update.ProgressPercent,5:F1}% · " +
                    $"{update.ProcessedBaseCandles:N0} candles · {update.CandlesPerSecond:F0} c/s");
                foreach (StrategyProgressSnapshot strategy in update.Strategies)
                {
                    string label = strategy.Instrument ?? strategy.StrategyName;
                    string line =
                        $"  {label,-16} {(strategy.Status ?? "Running"),-10} " +
                        $"bal {strategy.Balance,10:N2}  net {strategy.NetProfit,10:N2}  " +
                        $"trades {strategy.CompletedTrades,4}  open {strategy.OpenPositions}";
                    if (strategy.Instrument is not null &&
                        update.SourceProgressByInstrument.TryGetValue(strategy.Instrument, out HistoricalSourceProgress? download))
                    {
                        line += $"  [{download.Phase}{(download.FromCache ? ", cache" : "")} " +
                            $"{download.CandlesRead:N0} candles, {download.PagesRead} pages]";
                    }
                    block.AppendLine(line);
                }
                string text = block.ToString();
                if (progressInteractive && progressLinesDrawn > 0)
                    Console.Write($"\x1b[{progressLinesDrawn}F\x1b[0J");
                Console.Write(text);
                progressLinesDrawn = text.Count(c => c == '\n');
            });

            if (options.AutoTrainCalibration)
            {
                await RunAutoTrainCalibrationAsync(options, request, service, cancellation.Token)
                    .ConfigureAwait(false);
            }

            var comparisonRuns = new List<(string Configuration, ComparativeSimulationResult Result)>();
            ComparativeSimulationResult result;
            if (options.TrailingComparison)
            {
                BacktestRequest disabled = request with
                {
                    Runtime = request.Runtime with
                    {
                        LegacyPositionManagement = DisableProfitProtection(
                            request.Runtime.LegacyPositionManagement),
                        ImprovedPositionManagement = DisableProfitProtection(
                            request.Runtime.ImprovedPositionManagement)
                    }
                };
                Console.WriteLine("Running trailing comparison baseline (Legacy/Improved disabled)...");
                ComparativeSimulationResult baseline = await service
                    .RunToCompletionAsync(disabled, progress, cancellation.Token)
                    .ConfigureAwait(false);
                comparisonRuns.Add(("trailing-disabled", baseline));

                Console.WriteLine("Running trailing comparison candidate (configured Legacy/Improved management)...");
                result = await service
                    .RunToCompletionAsync(request, progress, cancellation.Token)
                    .ConfigureAwait(false);
                comparisonRuns.Add(("trailing-configured", result));
            }
            else
            {
                Console.WriteLine(
                    $"Starting simulation {request.Instrument} {request.From:yyyy-MM-dd} → {request.To:yyyy-MM-dd} " +
                    $"({options.StrategyExecution})...");
                result = await service
                    .RunToCompletionAsync(request, progress, cancellation.Token)
                    .ConfigureAwait(false);
            }

            string dashboardOutput = Path.GetFullPath(options.OutputDirectory);
            Directory.CreateDirectory(dashboardOutput);
            await ExportDashboardCompatibleAsync(options, result, dashboardOutput, cancellation.Token)
                .ConfigureAwait(false);
            if (comparisonRuns.Count > 0)
            {
                await WriteJsonAsync(
                    Path.Combine(dashboardOutput, "trailing-comparison.json"),
                    new
                    {
                        generatedAt = DateTimeOffset.UtcNow,
                        request.Instrument,
                        request.From,
                        request.To,
                        configurations = comparisonRuns.SelectMany(run =>
                            run.Result.Strategies.Select(strategy =>
                                BuildTrailingComparisonRow(run.Configuration, strategy)))
                    },
                    cancellation.Token).ConfigureAwait(false);
                Console.WriteLine(
                    $"Trailing comparison: {Path.Combine(dashboardOutput, "trailing-comparison.json")}");
            }

            Console.WriteLine($"Simulation {result.SimulationId:N} completed in {result.TotalDuration}.");
            Console.WriteLine($"Input hash: {result.InputHash}");
            Console.WriteLine($"Chunked replay: {result.OutputDirectory}");
            Console.WriteLine($"Dashboard export: {dashboardOutput}");
            foreach (StrategySimulationResult strategy in result.Strategies)
            {
                Console.WriteLine(
                    $"{strategy.StrategyName}: {strategy.Result.Trades.Count} trades, " +
                    $"net {strategy.Result.NetProfit:N2}, " +
                    $"frames/sec metrics avg {strategy.Metrics.AverageFrameProcessingTime.TotalMicroseconds:F0}µs/frame");
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Backtest cancelled.");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static readonly Dictionary<SimulationJobStatus, string> JobStatusLabels = new()
    {
        [SimulationJobStatus.Queued] = "Queued",
        [SimulationJobStatus.PreparingData] = "Preparing data",
        [SimulationJobStatus.DownloadingData] = "Downloading data",
        [SimulationJobStatus.LoadingCache] = "Loading cache",
        [SimulationJobStatus.WarmingUp] = "Warming up",
        [SimulationJobStatus.Running] = "Running",
        [SimulationJobStatus.Paused] = "Paused",
        [SimulationJobStatus.Cancelling] = "Cancelling",
        [SimulationJobStatus.Cancelled] = "Cancelled",
        [SimulationJobStatus.Exporting] = "Exporting",
        [SimulationJobStatus.Completed] = "Completed",
        [SimulationJobStatus.Failed] = "Failed",
    };

    private static string JobStatusLabel(SimulationJobStatus status) =>
        JobStatusLabels.TryGetValue(status, out string? label) ? label : status.ToString();

    /// <summary>wget-style ascii bar: <c>[=========&gt;          ]</c>.</summary>
    private static string AsciiBar(decimal percent, int width = 24)
    {
        decimal clamped = Math.Max(0m, Math.Min(100m, percent));
        int filled = Math.Clamp((int)Math.Round(clamped / 100m * width), 0, width);
        Span<char> bar = stackalloc char[width];
        for (int i = 0; i < width; i++)
            bar[i] = i < filled ? '=' : ' ';
        if (filled > 0 && filled < width)
            bar[filled - 1] = '>';
        return new string(bar);
    }

    private static async Task ExportDashboardCompatibleAsync(
        BacktestCommandOptions options,
        ComparativeSimulationResult result,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        DateTimeOffset generatedAt = DateTimeOffset.UtcNow;
        var manifestRuns = new List<BacktestManifestRun>();

        foreach (StrategySimulationResult strategy in result.Strategies)
        {
            string id = strategy.StrategyId.Contains("legacy", StringComparison.OrdinalIgnoreCase)
                ? "legacy-progressive"
                : "improved-progressive";
            string fileName = $"{id}.json";
            ReplayPerformanceSummary performance = SimulationReplayMapper.ToPerformance(strategy.Result);
            var runDataset = new BacktestRunDataset(
                1,
                strategy.StrategyName,
                $"{strategy.StrategyName} · {options.From:yyyy-MM-dd} to {options.To:yyyy-MM-dd}",
                SimulationReplayMapper.ToTrades(strategy.Result),
                performance);
            await WriteJsonAsync(Path.Combine(outputDirectory, fileName), runDataset, cancellationToken)
                .ConfigureAwait(false);
            manifestRuns.Add(new BacktestManifestRun(id, strategy.StrategyName, fileName, performance));
            Console.WriteLine(
                $"{strategy.StrategyName}: {performance.TradeCount} trades, " +
                $"{performance.NetProfit:N2} {performance.Currency} net, {performance.WinRatePercent:F1}% win rate.");
        }

        // Point the market file at the chunked simulation output for progressive UIs.
        var manifest = new BacktestManifest(
            1,
            $"Dual-strategy streamed backtest · {options.Instrument}",
            generatedAt,
            options.Instrument.Value,
            options.From,
            options.To,
            BacktestCommandOptions.FormatInterval(options.ExecutionInterval),
            result.OutputDirectory,
            manifestRuns);
        await WriteJsonAsync(Path.Combine(outputDirectory, "manifest.json"), manifest, cancellationToken)
            .ConfigureAwait(false);
        await WriteJsonAsync(Path.Combine(outputDirectory, "comparison.json"), manifest, cancellationToken)
            .ConfigureAwait(false);
        await WriteJsonAsync(
            Path.Combine(outputDirectory, "simulation-result.json"),
            new
            {
                result.SimulationId,
                result.InputStreamId,
                result.InputHash,
                result.ProcessedBaseCandles,
                result.TotalDuration,
                result.FillModel,
                result.DataQuality,
                Strategies = result.Strategies.Select(item => new
                {
                    item.StrategyId,
                    item.StrategyName,
                    item.IsComplete,
                    item.Metrics,
                    Performance = SimulationReplayMapper.ToPerformance(item.Result),
                    Trades = SimulationReplayMapper.ToTrades(item.Result)
                })
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        await using FileStream output = File.Create(path);
        await JsonSerializer.SerializeAsync(output, value, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<BacktestCommandOptions> ResolveCalibrationArtifactsAsync(
        BacktestCommandOptions options, CancellationToken cancellationToken)
    {
        if (options.SetupCalibrationArtifactId is null &&
            options.ManagementCalibrationArtifactId is null &&
            options.MetaModelArtifactId is null)
            return options;

        var repository = new Simulator.Calibration.FileCalibrationArtifactRepository(
            options.CalibrationArtifactsDirectory);

        if (options.SetupCalibrationArtifactId is string setupId)
        {
            if (!Guid.TryParse(setupId, out Guid parsed))
                throw new ArgumentException("--setup-calibration-artifact-id is invalid.");
            RiskManager.Calibration.SetupCalibrationArtifact artifact = await repository
                .GetSetupAsync(parsed, cancellationToken).ConfigureAwait(false)
                ?? throw new ArgumentException("The referenced setup calibration artifact does not exist.");
            options = options with { SetupCalibrationArtifact = artifact };
        }

        if (options.ManagementCalibrationArtifactId is string managementId)
        {
            if (!Guid.TryParse(managementId, out Guid parsed))
                throw new ArgumentException("--management-calibration-artifact-id is invalid.");
            TradeManagementCalibration artifact = await repository
                .GetManagementAsync(parsed, cancellationToken).ConfigureAwait(false)
                ?? throw new ArgumentException("The referenced management calibration artifact does not exist.");
            options = options with { ManagementCalibrationArtifact = artifact };
        }

        if (options.MetaModelArtifactId is string metaModelId)
        {
            if (!Guid.TryParse(metaModelId, out Guid parsed))
                throw new ArgumentException("--meta-model-artifact-id is invalid.");
            Simulator.Calibration.MetaModelArtifact artifact = await repository
                .GetMetaModelAsync(parsed, cancellationToken).ConfigureAwait(false)
                ?? throw new ArgumentException("The referenced meta-model artifact does not exist.");
            options = options with { MetaModelArtifact = artifact };
        }

        return options;
    }

    /// <summary>
    /// For every structural-confluence assignment missing an explicit
    /// indicator-confluence/liquidity-break-retest calibration pin, resolves the most recent
    /// Approved+Improved artifact for that assignment's instrument and this request's execution
    /// interval and fills the pin in - exactly as if a human had typed the GUID via
    /// <c>--request-json</c>. This is the tooling-layer half of the "auto-apply" flow: it never
    /// bakes a "latest approved" lookup into the engine itself, only resolves once per invocation
    /// here and hands the overlay resolver (<see cref="ResolveIndicatorCalibrationOverlaysAsync"/>)
    /// an ordinary explicit GUID, which still runs its own compatibility check afterward.
    /// </summary>
    private static async Task<BacktestRequest> ApplyAutoCalibrationPinsAsync(
        BacktestRequest request, string calibrationArtifactsDirectory, CancellationToken cancellationToken)
    {
        if (request.StrategyAssignments is not { Count: > 0 } assignments)
            return request;

        var artifacts = new FileCalibrationArtifactRepository(calibrationArtifactsDirectory);
        var indicatorConfluenceStrategyId = new IndicatorConfluenceCalibrationManifest().StrategyId;
        var liquidityBreakRetestStrategyId = new LiquidityBreakRetestCalibrationManifest().StrategyId;

        var updatedAssignments = new List<StrategyInstrumentAssignment>(assignments.Count);
        bool anyChanged = false;
        foreach (StrategyInstrumentAssignment assignment in assignments)
        {
            if (!string.Equals(assignment.StrategyType, TradingAgentTypeIds.StructuralConfluence, StringComparison.Ordinal))
            {
                updatedAssignments.Add(assignment);
                continue;
            }

            StrategyInstrumentAssignment updated = assignment;
            if (updated.IndicatorCalibrationArtifactId is null)
            {
                Guid? resolved = await BestApprovedCalibrationArtifactResolver.ResolveAsync(
                    artifacts, indicatorConfluenceStrategyId, updated.Instrument, request.Runtime.StructuralTriggerInterval,
                    cancellationToken).ConfigureAwait(false);
                if (resolved is { } indicatorArtifactId)
                {
                    updated = updated with { IndicatorCalibrationArtifactId = indicatorArtifactId };
                    Console.WriteLine(
                        $"Auto-apply: resolved indicator-confluence artifact '{indicatorArtifactId}' for assignment '{assignment.Id ?? assignment.Instrument.Value}'.");
                    anyChanged = true;
                }
            }
            if (updated.LiquidityBreakRetestCalibrationArtifactId is null)
            {
                Guid? resolved = await BestApprovedCalibrationArtifactResolver.ResolveAsync(
                    artifacts, liquidityBreakRetestStrategyId, updated.Instrument, request.Runtime.StructuralTriggerInterval,
                    cancellationToken).ConfigureAwait(false);
                if (resolved is { } liquidityArtifactId)
                {
                    updated = updated with { LiquidityBreakRetestCalibrationArtifactId = liquidityArtifactId };
                    Console.WriteLine(
                        $"Auto-apply: resolved liquidity-break-retest artifact '{liquidityArtifactId}' for assignment '{assignment.Id ?? assignment.Instrument.Value}'.");
                    anyChanged = true;
                }
            }
            updatedAssignments.Add(updated);
        }

        return anyChanged ? request with { StrategyAssignments = updatedAssignments } : request;
    }

    /// <summary>
    /// Applies each structural-confluence assignment's pinned indicator-confluence and/or
    /// liquidity-break-retest calibration artifacts (blueprint §19 Phase 7/8), if any, via the
    /// shared per-strategy <c>RequestOverlayResolver</c> library entry points. An assignment
    /// without any pin is returned completely unchanged - a true no-op for every request that
    /// doesn't opt in. A pinned artifact that turns out incompatible fails the whole run loudly.
    /// </summary>
    private static async Task<BacktestRequest> ResolveIndicatorCalibrationOverlaysAsync(
        BacktestRequest request, string calibrationArtifactsDirectory, CancellationToken cancellationToken)
    {
        if (request.StrategyAssignments is not { Count: > 0 } assignments || assignments.All(assignment =>
                assignment.IndicatorCalibrationArtifactId is null &&
                assignment.LiquidityBreakRetestCalibrationArtifactId is null))
        {
            return request;
        }

        var artifacts = new FileCalibrationArtifactRepository(calibrationArtifactsDirectory);
        BacktestRequest resolved = await IndicatorConfluenceRequestOverlayResolver
            .ApplyToRequestAsync(request, artifacts, cancellationToken).ConfigureAwait(false);
        resolved = await LiquidityBreakRetestRequestOverlayResolver
            .ApplyToRequestAsync(resolved, artifacts, cancellationToken).ConfigureAwait(false);
        foreach (StrategyInstrumentAssignment assignment in assignments)
        {
            if (assignment.IndicatorCalibrationArtifactId is { } indicatorArtifactId)
                Console.WriteLine($"Applied indicator-calibration overlay '{indicatorArtifactId}' to assignment '{assignment.Id}'.");
            if (assignment.LiquidityBreakRetestCalibrationArtifactId is { } liquidityArtifactId)
                Console.WriteLine($"Applied liquidity-break-retest calibration overlay '{liquidityArtifactId}' to assignment '{assignment.Id}'.");
        }
        return resolved;
    }

    /// <summary>
    /// Isolated pre-step: trains a fresh leakage-safe calibration bundle per strategy on the
    /// window immediately preceding <see cref="BacktestCommandOptions.From"/>, via the same
    /// <see cref="CalibrationTrainingPipeline"/>/<see cref="CalibrationBundleWorkflow"/>
    /// <c>LiveTradingHost</c>'s scheduler uses. Deliberately never wires its output into
    /// <paramref name="request"/> or the run this call is part of - a successful run only ever
    /// produces a PendingReview candidate; promotion/activation stays a separate, explicit,
    /// later step (the same discipline the live host follows).
    /// </summary>
    private static async Task RunAutoTrainCalibrationAsync(
        BacktestCommandOptions options,
        BacktestRequest request,
        IBacktestApplicationService backtests,
        CancellationToken cancellationToken)
    {
        var artifacts = new Simulator.Calibration.FileCalibrationArtifactRepository(options.CalibrationArtifactsDirectory);
        var profiles = new FileTradingPolicyProfileStore(
            Path.Combine(options.CalibrationArtifactsDirectory, "..", "trading-policy-profiles"));
        ICalibrationBundleApprovalStore approvals = new FileCalibrationBundleApprovalStore(
            Path.Combine(options.CalibrationArtifactsDirectory, "..", "calibration-bundle-candidates"),
            artifacts,
            profiles);
        var pipeline = new CalibrationTrainingPipeline(backtests, artifacts);
        var workflow = new CalibrationBundleWorkflow(pipeline, artifacts, approvals);

        DateTimeOffset trainingTo = options.From;
        DateTimeOffset trainingFrom = trainingTo.AddDays(-options.AutoTrainCalibrationWindowDays);

        foreach (string strategy in options.Strategies)
        {
            Console.WriteLine(
                $"Auto-training calibration for '{strategy}' on {trainingFrom:yyyy-MM-dd} → {trainingTo:yyyy-MM-dd} " +
                "(isolated research job - not used by this run)...");

            var promotionRequest = new CalibrationBundlePromotionRequest
            {
                Training = new CalibrationTrainingRequest
                {
                    Instruments = [request.Instrument],
                    Strategies = [strategy],
                    From = trainingFrom,
                    To = trainingTo,
                    Runtime = request.Runtime,
                    StartingBalance = request.StartingBalance,
                    Quantity = options.Quantity,
                    // AGENT-01: sourced from the real request driving this run, so the promoted
                    // AgentOptions (derived from these plus Runtime) match what this run actually
                    // uses instead of silently drifting to bare defaults.
                    MinimumRewardRisk = request.MinimumRewardRisk,
                    PriceActionConfirmation = request.PriceActionConfirmation,
                    MinimumPriceActionConfidence = request.MinimumPriceActionConfidence,
                    RejectStrongOpposingPriceAction = request.RejectStrongOpposingPriceAction,
                    Folds = options.AutoTrainCalibrationFolds,
                    Embargo = TimeSpan.FromHours(options.AutoTrainCalibrationEmbargoHours),
                    Description = $"auto-train (BacktestRunner) · {strategy} · {request.Instrument} · {DateTimeOffset.UtcNow:O}"
                },
                AgentKind = strategy.Contains("legacy", StringComparison.OrdinalIgnoreCase)
                    ? ProgressiveAgentKind.Legacy
                    : ProgressiveAgentKind.Improved,
                StrategyVersion = options.AutoTrainCalibrationStrategyVersion ?? $"{strategy}-auto"
            };

            try
            {
                CalibrationBundlePromotionResult result = await workflow
                    .RunAndProposeAsync(promotionRequest, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                if (!result.Training.Success)
                {
                    Console.WriteLine($"Auto-train for '{strategy}' failed: {result.Training.FailureReason}");
                }
                else if (result.Candidate is null)
                {
                    Console.WriteLine(
                        $"Auto-train for '{strategy}' produced mutually incompatible artifacts: {result.IncompatibilityReason}");
                }
                else
                {
                    Console.WriteLine($"Auto-train for '{strategy}' produced candidate {result.Candidate.Id:N}, pending review.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Training must never block or fail the actual backtest run it precedes.
                Console.WriteLine($"Auto-train for '{strategy}' failed unexpectedly: {ex.Message}");
            }
        }
    }

    private static PositionManagementOptions DisableProfitProtection(
        PositionManagementOptions options) => options with
    {
        Mode = TrailingStopMode.Disabled,
        ExitOnAdverseStructureBreak = false,
        EnableScaleOut = false,
        EnableProfitFloor = false,
        EnableMaximumGiveback = false,
        EnableStagnationReduction = false,
        EnableStructuralDeteriorationReduction = false,
        EnableMomentumDecayReduction = false,
        EnableVolatilityExhaustionReduction = false,
        EnableRiskWindowReduction = false,
        EnableExecutionCostStressReduction = false
    };

    private static object BuildTrailingComparisonRow(
        string configuration,
        StrategySimulationResult strategy)
    {
        StrategyPerformanceSnapshot performance =
            StrategyPerformanceSnapshot.FromTrades(strategy.Result.Trades);
        return new
        {
            configuration,
            strategy.StrategyId,
            strategy.StrategyName,
            performance.NetProfit,
            performance.MaximumDrawdown,
            performance.ProfitFactor,
            performance.WinRatePercent,
            performance.AverageR,
            performance.MedianR,
            performance.AverageMfe,
            performance.AverageMae,
            performance.MfeCapturedPercent,
            initialStopExits = performance.ExitReasons.GetValueOrDefault("InitialStopLoss"),
            performance.BreakEvenExits,
            performance.TrailingStopExits,
            targetExits = performance.ExitReasons.GetValueOrDefault("TakeProfit"),
            reverseExits = performance.ExitReasons.GetValueOrDefault("ReverseStrategyClose") +
                performance.ExitReasons.GetValueOrDefault("StrategyClose"),
            performance.AverageAmendmentsPerTrade,
            performance.AverageMaximumLockedR,
            performance.AverageProfitGivebackFromMfeR,
            performance.AcceptedStopAmendments,
            performance.RejectedStopAmendments,
            performance.UnsupportedStopAmendments,
            performance.PositionReductions,
            performance.AveragePartialExitsPerTrade,
            performance.PartialExitNetProfit,
            performance.StagnationReductions,
            performance.StructuralDeteriorationReductions,
            performance.MomentumDecayReductions,
            performance.VolatilityExhaustionReductions,
            performance.SessionRiskReductions,
            performance.ExecutionCostStressReductions,
            performance.RunnerActivations,
            performance.ProfitFloorStopExits,
            performance.ProfitFloorExits,
            performance.MfeGivebackStopExits,
            performance.MaximumGivebackExits
        };
    }

    private static BacktestRequest WithPullbackOnlyStructuralAgent(BacktestRequest request)
    {
        var structural = new StructuralConfluenceStrategyOptions
        {
            Quantity = request.Quantity,
            MinimumRewardRisk = request.MinimumRewardRisk,
            Trigger = new StructuralTriggerOptions
            {
                MinimumPriceActionConfidence = request.MinimumPriceActionConfidence
            },
            LiquiditySweepReversal = new LiquiditySweepReversalOptions { Enabled = false },
            LiquidityBreakRetest = new LiquidityBreakRetestOptions { Enabled = false },
            // Step A diagnostic: drop HTF trend veto so pullbacks are judged on zone + trigger + geometry.
            SupplyDemandPullback = new SupplyDemandPullbackOptions
            {
                Enabled = true,
                RequireTrendAlignment = false
            }
        };
        structural.Validate();
        ChartAnnotationOptions annotation = request.Runtime.AnnotationOptions with
        {
            SupplyDemand = request.Runtime.AnnotationOptions.SupplyDemand with { Enabled = true }
        };
        return request with
        {
            Strategies = ["structural-confluence"],
            StrategyAssignments =
            [
                new StrategyInstrumentAssignment
                {
                    Id = "structural-pullback-step-a",
                    StrategyType = TradingAgentTypeIds.StructuralConfluence,
                    Instrument = request.Instrument,
                    Mode = AgentExecutionMode.Shadow,
                    AgentDefinitionOverride = new TradingAgentDefinition
                    {
                        Kind = TradingAgentKind.StructuralConfluence,
                        StructuralConfluence = structural
                    }
                }
            ],
            Runtime = request.Runtime with { AnnotationOptions = annotation }
        };
    }

    /// <summary>
    /// 2026-07-27 trade-log review candidate (Dashboard/public/data/backtests/simulations/
    /// 8b1435996f4a4b64bf23218d8e1ccef2/AGENT_IMPROVEMENT_RECOMMENDATIONS.md, the "A+B+C"
    /// combined run): strict break/retest routing is already the code default, so only
    /// IndicatorConfluence and the soft-spread action need overriding here. Rebuilds each
    /// structural-confluence assignment's options via the exact same fields
    /// BacktestRequest.ResolveAgentDefinition's default path would use (Quantity,
    /// MinimumRewardRisk, the three structural intervals, MinimumPriceActionConfidence) so the
    /// only difference from the original run is the three documented fixes - not an incidental
    /// reversion to some other default.
    /// </summary>
    private static BacktestRequest WithCandidateFixes(BacktestRequest request)
    {
        if (request.StrategyAssignments is not { Count: > 0 } existingAssignments)
        {
            throw new ArgumentException(
                "--candidate-fixes requires --strategy-assignment/--instruments-file StrategyAssignments; " +
                "the single-instrument Strategies path is not covered by this candidate transform.");
        }

        StrategyInstrumentAssignment[] assignments = existingAssignments
            .Select(assignment => !string.Equals(
                assignment.StrategyType, TradingAgentTypeIds.StructuralConfluence, StringComparison.OrdinalIgnoreCase)
                ? assignment
                : assignment with
                {
                    AgentDefinitionOverride = new TradingAgentDefinition
                    {
                        Kind = TradingAgentKind.StructuralConfluence,
                        StructuralConfluence = new StructuralConfluenceStrategyOptions
                        {
                            Quantity = request.Quantity,
                            MinimumRewardRisk = request.MinimumRewardRisk,
                            TriggerInterval = request.Runtime.StructuralTriggerInterval,
                            SetupInterval = request.Runtime.StructuralSetupInterval,
                            ContextInterval = request.Runtime.StructuralContextInterval,
                            MarketRegime = request.Runtime.MarketRegimeRouting,
                            Trigger = new StructuralTriggerOptions
                            {
                                MinimumPriceActionConfidence = request.MinimumPriceActionConfidence
                            },
                            LiquiditySweepReversal = new LiquiditySweepReversalOptions { Enabled = true },
                            SupplyDemandPullback = new SupplyDemandPullbackOptions { Enabled = true },
                            LiquidityBreakRetest = new LiquidityBreakRetestOptions { Enabled = true },
                            // Fix 2: disabled for the candidate (was Enabled = true).
                            IndicatorConfluence = new IndicatorConfluenceOptions { Enabled = false }
                            // Fix 1 (AllowBreakRetestAfterBreakoutTransition) left at its record
                            // default of false - strict routing is now the baseline behavior.
                        }
                    }
                })
            .ToArray();

        return request with
        {
            StrategyAssignments = assignments,
            Runtime = request.Runtime with
            {
                // Fix 3: reject at the soft spread/ATR limit instead of merely halving risk.
                TradingConditions = request.Runtime.TradingConditions with
                {
                    SoftSpreadLimitAction = SoftSpreadLimitAction.RejectEntry
                }
            }
        };
    }

    private static string? ExtractRequestJsonPath(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--request-json", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < args.Length)
                return args[i + 1];
            if (args[i].StartsWith("--request-json=", StringComparison.OrdinalIgnoreCase))
                return args[i]["--request-json=".Length..];
        }

        return null;
    }

    private static async Task<BacktestRequest> LoadRequestJsonAsync(
        string path,
        CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Backtest request JSON was not found: {fullPath}");

        await using FileStream stream = File.OpenRead(fullPath);
        BacktestRequest? request = await JsonSerializer
            .DeserializeAsync<BacktestRequest>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Failed to deserialize BacktestRequest from {fullPath}.");
        request.Validate();
        string strategies = request.StrategyAssignments is { Count: > 0 } assignments
            ? string.Join(',', assignments.Select(item => item.Id ?? item.StrategyType))
            : string.Join(',', request.Strategies);
        Console.WriteLine(
            $"Loaded request JSON {fullPath} · {request.Instrument} · min R:R {request.MinimumRewardRisk} · strategies={strategies}");
        return request;
    }

    private static void PrintHelp() => Console.WriteLine("""
TradingHub streamed dual-strategy backtest (CLI → shared application service)

Usage:
  dotnet run --project BacktestRunner -- [options]
  dotnet run --project BacktestRunner -- --request-json path/to/BacktestRequest.json

Options:
  --instrument FX:EUR/USD                  (default: liquid major; candle-request template - still
                                             required even when --instruments is set)
  --instruments FX:EUR/USD,FX:GBP/USD,...  (portfolio v1: every --strategies entry trades every
                                             instrument listed here, cartesian; all from the same
                                             --source. Use --account-mode shared to pool capital
                                             and enable correlation-aware risk across them.)
  --instruments-file watchlist.txt         (one instrument key per line, '#' comments allowed;
                                             merged/deduplicated with --instruments if both given)
  --from YYYY-MM-DD                        (default: start of previous UTC month)
  --to YYYY-MM-DD                          (default: start of current UTC month)
  --execution-interval 1m / --base-interval 1m
  --precision-mode fast|broker-native|high-precision
  --source oanda|binance|imported
  --imported-candles /server/path/to/candles.csv
  --analysis-base-interval 1m
  --analysis-intervals 5m,15m,30m,1h,2h
  --structural-trigger-interval 5m        (structural-confluence's own decision/trigger timeframe;
                                            independent of --execution-interval, which only governs
                                            fill precision and the raw-candle fetch/aggregation base;
                                            must satisfy trigger <= setup <= context)
  --structural-setup-interval 15m         (structural-confluence's own setup timeframe)
  --structural-context-interval 1h        (structural-confluence's own context timeframe)
  --trend-interval 2h
  --secondary-trend-intervals 1h
  --setup-intervals 30m
  --confirmation-interval 15m
  --additional-confirmation-intervals 10m
  --entry-interval 5m
  --minimum-secondary-alignments 0
  --minimum-setup-alignments 1
  --minimum-confirmation-alignments 1
  --allow-timeframe-opposition
  --environment demo|live
  --output Dashboard/public/data/backtests
  --cache .cache/oanda
  --jobs .cache/simulation-jobs
  --refresh
  --no-cache
  --strategies legacy,improved
  --strategy-execution sequential|parallel
  --strategy-channel-capacity 4
  --max-parallel-strategies 4
  --strategy-failure-policy stop-all|stop-one
  --analysis-sharing shared|independent
  --ambiguous-policy stop-first|target-first|nearest-open
  --account-mode independent|shared
  --no-regime (regime routing/risk is on by default)
  --er-period 14
  --no-trading-conditions (session/rollover/spread/stale-data protection is on by default)
  --no-adaptive-risk (drawdown/volatility-scaled risk is on by default)
  --neo-wave                              (enable causal wave analysis; RecordOnly by default)
  --neo-wave-mode RecordOnly|SoftConfidence|SoftRiskReduction|SoftConfidenceAndRisk
  --neo-wave-interval 2h                 (default: strategy trend interval)
  --maximum-portfolio-heat-percent 1.5
  --fill-model MidpointPlusConfiguredSpread|VariableSyntheticSpread|StressExecution
  --stress-scenario Base|SpreadDouble|SlippageTriple|GapStress|StopAmendmentFailure|ConnectionLoss|CorrelationShock|CombinedStress
  --fill-capacity 25000
  --financing
  --financing-long-annual-percent -3.5
  --financing-short-annual-percent 1.2
  --setup-calibration-artifact-id <guid>   (resolved from --calibration-artifacts-directory)
  --management-calibration-artifact-id <guid>
  --meta-model-artifact-id <guid>
  --calibration-artifacts-directory .cache/calibration-artifacts
  --auto-train-calibration                 (train a fresh setup/meta-model/management bundle
                                             per strategy, on the window immediately preceding
                                             --from, before this run starts; produces a
                                             PendingReview candidate only - never used by this
                                             run itself)
  --auto-train-calibration-window-days 180
  --auto-train-calibration-folds 5
  --auto-train-calibration-embargo-hours 24
  --auto-train-calibration-strategy-version <text>  (default: "{strategy}-auto")
  --auto-apply-calibration                 (for every structural-confluence assignment without an
                                             explicit indicator-confluence/liquidity-break-retest
                                             pin, auto-resolves and pins the most recent Approved+
                                             Improved artifact for that instrument/trigger
                                             interval - see 'indicator-calibration pending/review'
                                             to see what is available before relying on this)
  --warmup-days 21
  --quantity 1000                         (manual/fixed-quantity fallback)
  --position-sizing-mode fixed-fractional|fixed-cash|fixed-quantity
  --risk-percent 0.5
  --fixed-cash-risk 250
  --minimum-quantity 1
  --maximum-quantity 100000
  --quantity-step 1
  --maximum-account-margin-percent 30
  --maximum-position-margin-percent 10
  --starting-balance 100000
  --base-currency USD           account currency (default USD; NOT derived from the instrument)
  --quote-rate JPY=0.0067       quote->account rates, comma separated; needed for crosses
                                such as GBP/JPY on a USD account
  --leverage 20
  --commission-rate 0.00002
  --spread-bps 1
  --slippage-bps 0.5
  --minimum-rr 1.5
    Alfonso bracket tests may use 0 to disable the reward floor; stop, target and sizing checks remain.
  --alfonso-opposing-zone-target
    Target the nearest confirmed opposing entry-timeframe zone; skip if none exists.
  --alfonso-revalidate-pending-5m
    Cancel pending lower-aligned entries when 5m trend disagrees or a confirmed swing breaks.
  --alfonso-block-exhausted-buys-5m
    Block/cancel unfilled buys when closed 5m high >= BB upper and (RSI > 70 or CCI >= 100).
  --alfonso-reversal-shadow-log PATH
    Log 5m sweep/reclaim, break and holding-retest observations; never trade them.
  --alfonso-entry-policy core|lower-reversal|lower-aligned
    lower-aligned: 15m direction confirmed by 5m; higher trends informational.
  --daily-equity-profit-target 3000
  --daily-equity-giveback-activation 2500
  --maximum-daily-equity-giveback 750
  --price-action-mode disabled|soft|required|required-with-context
  --minimum-price-action-confidence 55
  --classifier-model PATH                  (required by trading-classification and by
                                             trend-tactical rung C; without it those agents
                                             produce zero trades)
  --allow-opposing-price-action
  --invert-signal-polarity                 (research: trade the OPPOSITE of every signal. Setup
                                             selection is unchanged; only the direction and the
                                             mirrored bracket differ. Only interpretable against a
                                             normal-polarity control over the same window.)
  --progress-interval 500
  --trailing-comparison
  --legacy-trailing-mode disabled|break-even|structure-atr
  --legacy-management-interval 15m        (legacy alias for main interval)
  --legacy-fast-management-interval 5m
  --legacy-main-management-interval 15m
  --legacy-thesis-management-interval 1h
  --legacy-disable-mechanical-protection
  --legacy-break-even-r 1.0
  --legacy-structure-r 1.5
  --legacy-atr-buffer 0.25
  --legacy-neo-wave-invalidation-exit
  --legacy-neo-wave-invalidation-buffer-atr 0.10
  --improved-trailing-mode disabled|break-even|structure-atr
  --improved-management-interval 15m      (legacy alias for main interval)
  --improved-fast-management-interval 5m
  --improved-main-management-interval 15m
  --improved-thesis-management-interval 1h
  --improved-disable-mechanical-protection
  --improved-break-even-r 1.0
  --improved-structure-r 2.0
  --improved-atr-buffer 0.25
  --improved-neo-wave-invalidation-exit
  --improved-neo-wave-invalidation-buffer-atr 0.10

Credentials (only needed when downloading):
  Oanda__AccountId / OANDA_ACCOUNT_ID
  Oanda__AccessToken / OANDA_ACCESS_TOKEN
""");
}
