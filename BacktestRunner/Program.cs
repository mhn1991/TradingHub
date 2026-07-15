using System.Text.Json;
using System.Text.Json.Serialization;
using Brokers.Models;
using Dashboard.Contracts;
using Simulator.Jobs;
using Simulator.Models;
using Simulator.Services;
using TradeManager;

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
  --regime
  --er-period 14
  --trading-conditions
  --adaptive-risk
  --maximum-portfolio-heat-percent 1.5
  --fill-model MidpointPlusConfiguredSpread|VariableSyntheticSpread|StressExecution
  --stress-scenario Base|SpreadDouble|SlippageTriple|GapStress|StopAmendmentFailure|ConnectionLoss|CorrelationShock|CombinedStress
  --fill-capacity 25000
  --financing
  --financing-long-annual-percent -3.5
  --financing-short-annual-percent 1.2
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
  --improved-trailing-mode disabled|break-even|structure-atr
  --improved-management-interval 15m      (legacy alias for main interval)
  --improved-fast-management-interval 5m
  --improved-main-management-interval 15m
  --improved-thesis-management-interval 1h
  --improved-disable-mechanical-protection
  --improved-break-even-r 1.0
  --improved-structure-r 2.0
  --improved-atr-buffer 0.25

Credentials (only needed when downloading):
  Oanda__AccountId / OANDA_ACCOUNT_ID
  Oanda__AccessToken / OANDA_ACCESS_TOKEN
""");
}
