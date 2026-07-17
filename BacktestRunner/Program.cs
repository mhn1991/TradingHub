using System.Text.Json;
using System.Text.Json.Serialization;
using Agent.Strategies;
using Brokers.Models;
using Dashboard.Contracts;
using QuantResearch.Training.Pipeline;
using Simulator.Jobs;
using Simulator.Models;
using Simulator.Services;
using TradeManager;
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
        try
        {
            BacktestCommandOptions options = BacktestCommandOptions.Parse(args);
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

            options = await ResolveCalibrationArtifactsAsync(options, cancellation.Token);
            BacktestRequest request = options.ToBacktestRequest();
            await using var service = new BacktestApplicationService(
                new FileSimulationJobRepository(request.JobsDirectory),
                new BacktestApplicationServiceOptions
                {
                    MaxConcurrentJobs = 1,
                    QueueCapacity = 4
                });

            var progress = new Progress<BacktestProgress>(update =>
            {
                Console.WriteLine(
                    $"[{update.Status}] {update.CurrentMarketTime:u} · " +
                    $"{update.ProcessedBaseCandles:N0} candles · " +
                    $"{update.ProgressPercent:F1}% · " +
                    $"{update.CandlesPerSecond:F0} c/s");
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

    private static void PrintHelp() => Console.WriteLine("""
TradingHub streamed dual-strategy backtest (CLI → shared application service)

Usage:
  dotnet run --project BacktestRunner -- [options]

Options:
  --instrument FX:EUR/USD                  (default: liquid major)
  --from YYYY-MM-DD                        (default: start of previous UTC month)
  --to YYYY-MM-DD                          (default: start of current UTC month)
  --execution-interval 1m / --base-interval 1m
  --precision-mode fast|broker-native|high-precision
  --source oanda|binance|imported
  --imported-candles /server/path/to/candles.csv
  --analysis-base-interval 1m
  --analysis-intervals 5m,15m,30m,1h,2h
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
  --base-currency USD
  --leverage 20
  --commission-rate 0.00002
  --spread-bps 1
  --slippage-bps 0.5
  --minimum-rr 1.5
  --daily-equity-profit-target 3000
  --daily-equity-giveback-activation 2500
  --maximum-daily-equity-giveback 750
  --price-action-mode disabled|soft|required|required-with-context
  --minimum-price-action-confidence 55
  --allow-opposing-price-action
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
