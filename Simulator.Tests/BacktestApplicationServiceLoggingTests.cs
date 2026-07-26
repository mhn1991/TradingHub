using Agent.Abstractions;
using Agent.Models;
using Brokers.Models;
using ChartAnnotator.Engine;
using Microsoft.Extensions.Logging;
using RiskManager;
using RiskManager.Conditions;
using Simulator.Engine;
using Simulator.Jobs;
using Simulator.Models;
using Simulator.Services;

namespace Simulator.Tests;

/// <summary>
/// BacktestApplicationService previously had zero ILogger usage (matching the whole Simulator
/// project's 0/111 files) - a top-level job failure left no structured trace anywhere except a
/// bare exception.Message string on the job snapshot. These prove the new optional logger
/// actually receives job lifecycle events, with the full exception (not just its message) on
/// failure.
/// </summary>
[TestFixture]
public sealed class BacktestApplicationServiceLoggingTests
{
    private static readonly InstrumentKey EurUsd = new("FX:EUR/USD");

    [Test]
    public async Task JobFailure_LogsFullExceptionNotJustItsMessage()
    {
        BarInterval interval = BarInterval.Minutes(1);
        DateTimeOffset start = new(2026, 7, 13, 8, 0, 0, TimeSpan.Zero);
        Candle[] candles = Enumerable.Range(0, 10)
            .Select(index => TestCandles.Create(EurUsd, start.AddMinutes(index), interval, 1.10m, 1.10m, 1.10m, 1.10m))
            .ToArray();

        string tempRoot = Path.Combine(
            Path.GetTempPath(), "tradinghub-app-service-logging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var capturingLogger = new CapturingLogger<BacktestApplicationService>();

        try
        {
            await using var service = new BacktestApplicationService(
                new FileSimulationJobRepository(Path.Combine(tempRoot, "jobs")),
                new BacktestApplicationServiceOptions
                {
                    MaxConcurrentJobs = 1,
                    QueueCapacity = 2,
                    // A strategy that always throws, combined with the default
                    // StopEntireComparison policy, propagates out of RunAsync entirely - a
                    // genuine job-level failure, not one attributable to a single strategy
                    // session (that path was already covered before this work).
                    StrategyFactory = _ =>
                        [new StrategyFactoryEntry("throwing-strategy", new AlwaysThrowingAgent(interval), EurUsd)]
                },
                capturingLogger);

            var request = new BacktestRequest
            {
                Instrument = EurUsd,
                From = start,
                To = start.AddMinutes(10),
                BaseCurrency = "USD",
                StartingBalance = 100_000m,
                Quantity = 1_000m,
                OutputDirectory = Path.Combine(tempRoot, "out"),
                JobsDirectory = Path.Combine(tempRoot, "jobs"),
                InlineCandles = candles,
                Runtime = new BacktestRuntimeOptions
                {
                    BaseInterval = interval,
                    AnalysisBaseInterval = interval,
                    AnalysisIntervals = [interval],
                    WarmupDays = 0,
                    TradingConditions = new TradingConditionOptions { Enabled = false },
                    AnnotationOptions = new ChartAnnotationOptions
                    {
                        AtrPeriod = 3,
                        RsiPeriod = 3,
                        BollingerPeriod = 3,
                        HeavyAnalysisEveryCandles = 1
                    }
                }
            };

            Assert.ThrowsAsync<InvalidOperationException>(async () => await service.RunToCompletionAsync(request));

            Assert.That(capturingLogger.Entries, Has.Some.Matches<(LogLevel Level, string Message, Exception? Exception)>(
                entry => entry.Level == LogLevel.Information && entry.Message.Contains("queued")),
                "Job queuing must be logged.");
            var failureEntry = capturingLogger.Entries.FirstOrDefault(
                entry => entry.Level == LogLevel.Error && entry.Message.Contains("failed"));
            Assert.That(failureEntry.Exception, Is.Not.Null,
                "The failure log must carry the actual exception object (so a real sink can " +
                "render its full stack trace), not just a pre-formatted message string.");
            Assert.That(failureEntry.Exception!.Message, Does.Contain("Deliberate test failure"));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }

    private sealed class AlwaysThrowingAgent(BarInterval interval) : ITradingAgent
    {
        public string Name => "Always-throwing test agent";
        public IReadOnlySet<BarInterval> RequiredIntervals { get; } = new HashSet<BarInterval> { interval };
        public BarInterval TriggerInterval => interval;
        public AgentExitManagementMode ExitManagementMode => AgentExitManagementMode.Bracket;

        public Task<AgentDecision> EvaluateAsync(
            AgentMarketContext context,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Deliberate test failure for logging coverage.");
    }
}
