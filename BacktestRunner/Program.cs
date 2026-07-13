using System.Text.Json;
using System.Text.Json.Serialization;
using Brokers.Models;
using Dashboard.Contracts;
using Simulator.Jobs;
using Simulator.Models;
using Simulator.Services;

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

            Console.WriteLine(
                $"Starting simulation {request.Instrument} {request.From:yyyy-MM-dd} → {request.To:yyyy-MM-dd} " +
                $"({options.StrategyExecution})...");

            ComparativeSimulationResult result = await service
                .RunToCompletionAsync(request, progress, cancellation.Token)
                .ConfigureAwait(false);

            string dashboardOutput = Path.GetFullPath(options.OutputDirectory);
            Directory.CreateDirectory(dashboardOutput);
            await ExportDashboardCompatibleAsync(options, result, dashboardOutput, cancellation.Token)
                .ConfigureAwait(false);

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

    private static void PrintHelp() => Console.WriteLine("""
TradingHub streamed dual-strategy backtest (CLI → shared application service)

Usage:
  dotnet run --project BacktestRunner -- [options]

Options:
  --instrument FX:GBP/JPY
  --from 2025-01-01
  --to 2026-01-01
  --execution-interval 1m / --base-interval 1m
  --analysis-intervals 5m,15m,1h
  --environment demo|live
  --output Dashboard/public/data/backtests
  --cache .cache/oanda
  --jobs .cache/simulation-jobs
  --refresh
  --no-cache
  --strategies legacy,improved
  --strategy-execution sequential|parallel
  --strategy-worker task|thread
  --strategy-channel-capacity 4
  --max-parallel-strategies 4
  --strategy-failure-policy stop-all|stop-one
  --analysis-sharing shared|independent
  --ambiguous-policy stop-first|target-first|nearest-open
  --warmup-days 45
  --seed 12345
  --quantity 1000
  --starting-balance 100000
  --base-currency JPY
  --leverage 20
  --commission-rate 0.00002
  --spread-bps 1
  --slippage-bps 0.5
  --minimum-rr 1.5
  --progress-interval 500

Credentials (only needed when downloading):
  Oanda__AccountId / OANDA_ACCOUNT_ID
  Oanda__AccessToken / OANDA_ACCESS_TOKEN
""");
}
