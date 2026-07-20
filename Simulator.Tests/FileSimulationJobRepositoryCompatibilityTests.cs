using Simulator.Jobs;
using Simulator.Models;
using Brokers.Models;
using RiskManager.Conditions;
using Simulator.Financing;

namespace Simulator.Tests;

[TestFixture]
public sealed class FileSimulationJobRepositoryCompatibilityTests
{
    [Test]
    public async Task PhaseThreePerformanceSnapshot_LoadsAndCanBeMarkedInterrupted()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"tradinghub-legacy-jobs-{Guid.NewGuid():N}");
        Guid id = Guid.NewGuid();
        DateTimeOffset createdAt = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
        var repository = new FileSimulationJobRepository(directory);

        try
        {
            string legacyJson = $$"""
                {
                  "id": "{{id}}",
                  "revision": 4,
                  "status": "Running",
                  "createdAt": "{{createdAt:O}}",
                  "instrument": "FX:GBP/USD",
                  "requestedFrom": "2026-01-01T00:00:00+00:00",
                  "requestedTo": "2026-01-02T00:00:00+00:00",
                  "processedBaseCandles": 123,
                  "progressPercent": 50,
                  "candlesPerSecond": 12,
                  "strategies": [
                    {
                      "strategyName": "Legacy",
                      "strategyId": "legacy",
                      "balance": 10010,
                      "equity": 10010,
                      "unrealizedProfitLoss": 0,
                      "openPositions": 0,
                      "completedTrades": 1,
                      "activeSetups": 0,
                      "netProfit": 10,
                      "performance": {
                        "grossProfit": 11,
                        "netProfit": 10,
                        "commissions": 1,
                        "tradeCount": 1,
                        "wins": 1,
                        "losses": 0,
                        "winRatePercent": 100,
                        "profitFactor": null,
                        "averageR": 1,
                        "medianR": 1,
                        "expectancy": 10,
                        "maximumDrawdown": 0,
                        "averageHoldingSeconds": 60,
                        "averageSetupSeconds": 30,
                        "averageMfe": 12,
                        "averageMae": 2,
                        "mfeCapturedPercent": 83.33,
                        "exitReasons": { "TakeProfit": 1 }
                      }
                    }
                  ],
                  "isComplete": false
                }
                """;
            await File.WriteAllTextAsync(
                Path.Combine(directory, $"{id:N}.json"),
                legacyJson);

            IReadOnlyList<SimulationJobSnapshot> jobs = await repository.ListAsync();

            Assert.That(jobs, Has.Count.EqualTo(1));
            StrategyPerformanceSnapshot? performance = jobs[0].Strategies[0].Performance;
            Assert.Multiple(() =>
            {
                Assert.That(performance, Is.Not.Null);
                Assert.That(performance!.BreakEvenActivations, Is.Zero);
                Assert.That(performance.StructureTrailingActivations, Is.Zero);
                Assert.That(performance.AcceptedStopAmendments, Is.Zero);
                Assert.That(performance.RejectedStopAmendments, Is.Zero);
                Assert.That(performance.UnsupportedStopAmendments, Is.Zero);
                Assert.That(performance.AverageAmendmentsPerTrade, Is.Zero);
                Assert.That(performance.AverageMaximumLockedR, Is.Zero);
                Assert.That(performance.AverageProfitGivebackFromMfeR, Is.Zero);
                Assert.That(performance.TrailingStopExits, Is.Zero);
                Assert.That(performance.BreakEvenExits, Is.Zero);
            });

            await repository.MarkInterruptedJobsAsync();
            SimulationJobSnapshot? interrupted = await repository.GetAsync(id);

            Assert.Multiple(() =>
            {
                Assert.That(interrupted, Is.Not.Null);
                Assert.That(interrupted!.Status, Is.EqualTo(SimulationJobStatus.Failed));
                Assert.That(interrupted.IsComplete, Is.True);
                Assert.That(interrupted.Revision, Is.EqualTo(5));
                Assert.That(interrupted.Error, Does.StartWith("HostRestartedWhileRunning:"));
            });
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
    [Test]
    public async Task CorruptPersistedJob_IsQuarantinedWithoutBlockingNewJobs()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"tradinghub-corrupt-jobs-{Guid.NewGuid():N}");
        var repository = new FileSimulationJobRepository(directory);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, $"{Guid.NewGuid():N}.json"),
                "{ \"id\": \"truncated\"");

            await repository.MarkInterruptedJobsAsync();

            SimulationJobSnapshot snapshot = CreateQueuedSnapshot(Guid.NewGuid());
            await repository.SaveAsync(snapshot);
            IReadOnlyList<SimulationJobSnapshot> jobs = await repository.ListAsync();

            Assert.Multiple(() =>
            {
                Assert.That(jobs.Any(item => item.Id == snapshot.Id), Is.True);
                Assert.That(repository.LastRecoveryReport.QuarantinedFiles, Is.EqualTo(1));
                Assert.That(repository.LastRecoveryReport.WarningCodes, Does.Contain("PersistedJobsQuarantined"));
                Assert.That(
                    Directory.EnumerateFiles(Path.Combine(directory, "quarantine"), "*.json").Count(),
                    Is.EqualTo(1));
            });
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task LegacySnapshotWithInvalidBarInterval_IsQuarantinedWithoutBlockingStart()
    {
        // Regression for InvalidSimulationRequest ("Parameter 'value'") when recovery
        // scanned jobs that persisted default(BarInterval) as { "value": 0, "unit": "Second" }.
        // MarkInterruptedJobsAsync must quarantine those files, not throw ArgumentOutOfRangeException.
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"tradinghub-invalid-barinterval-jobs-{Guid.NewGuid():N}");
        var repository = new FileSimulationJobRepository(directory);
        Guid corruptId = Guid.NewGuid();

        try
        {
            string corruptJson = $$"""
                {
                  "schemaVersion": 2,
                  "snapshot": {
                    "id": "{{corruptId}}",
                    "revision": 1,
                    "status": "Failed",
                    "createdAt": "2026-07-16T00:00:00+00:00",
                    "instrument": "FX:EUR/USD",
                    "requestedFrom": "2026-06-01T00:00:00+00:00",
                    "requestedTo": "2026-07-01T00:00:00+00:00",
                    "processedBaseCandles": 0,
                    "progressPercent": 0,
                    "candlesPerSecond": 0,
                    "strategies": [],
                    "isComplete": true,
                    "request": {
                      "instrument": "FX:EUR/USD",
                      "from": "2026-06-01T00:00:00+00:00",
                      "to": "2026-07-01T00:00:00+00:00",
                      "strategies": ["legacy"],
                      "startingBalance": 100000,
                      "quantity": 1000,
                      "leverage": 20,
                      "runtime": {
                        "accountMode": "IndependentStrategyAccounts",
                        "baseInterval": { "value": 0, "unit": "Second", "isValid": false },
                        "executionInterval": { "value": 0, "unit": "Second", "isValid": false },
                        "analysisBaseInterval": { "value": 1, "unit": "Minute", "isValid": true },
                        "analysisIntervals": [{ "value": 5, "unit": "Minute", "isValid": true }]
                      }
                    }
                  }
                }
                """;
            await File.WriteAllTextAsync(
                Path.Combine(directory, $"{corruptId:N}.json"),
                corruptJson);

            Assert.That(async () => await repository.MarkInterruptedJobsAsync(), Throws.Nothing);

            SimulationJobSnapshot valid = CreateQueuedSnapshot(Guid.NewGuid());
            await repository.SaveAsync(valid);
            IReadOnlyList<SimulationJobSnapshot> jobs = await repository.ListAsync();

            Assert.Multiple(() =>
            {
                Assert.That(jobs.Any(item => item.Id == valid.Id), Is.True);
                Assert.That(jobs.Any(item => item.Id == corruptId), Is.False);
                Assert.That(repository.LastRecoveryReport.QuarantinedFiles, Is.EqualTo(1));
                Assert.That(repository.LastRecoveryReport.WarningCodes, Does.Contain("PersistedJobsQuarantined"));
                Assert.That(
                    Directory.EnumerateFiles(Path.Combine(directory, "quarantine"), "*.json")
                        .Any(path => path.Contains("invalid-domain", StringComparison.Ordinal) ||
                                     path.Contains("invalid-json", StringComparison.Ordinal)),
                    Is.True);
            });
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [TestCase("{ \"schemaVersion\": \"bad\", \"snapshot\": {} }")]
    [TestCase("{ \"schemaVersion\": 999, \"snapshot\": {} }")]
    public async Task InvalidPersistedEnvelope_IsQuarantinedWithoutPoisoningRepository(string json)
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"tradinghub-invalid-envelope-{Guid.NewGuid():N}");
        var repository = new FileSimulationJobRepository(directory);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, $"{Guid.NewGuid():N}.json"),
                json);

            await repository.MarkInterruptedJobsAsync();

            SimulationJobSnapshot valid = CreateQueuedSnapshot(Guid.NewGuid());
            await repository.SaveAsync(valid);
            SimulationJobSnapshot? loaded = await repository.GetAsync(valid.Id);

            Assert.Multiple(() =>
            {
                Assert.That(loaded, Is.Not.Null);
                Assert.That(loaded!.Id, Is.EqualTo(valid.Id));
                Assert.That(repository.LastRecoveryReport.QuarantinedFiles, Is.EqualTo(1));
            });
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task CurrentSchema_RoundTripsConstructibleQuantitativeConfigurationCollections()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"tradinghub-current-jobs-{Guid.NewGuid():N}");
        var repository = new FileSimulationJobRepository(directory);
        Guid id = Guid.NewGuid();
        DateTimeOffset now = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
        SimulationJobSnapshot snapshot = CreateQueuedSnapshot(id) with
        {
            Request = new BacktestRequest
            {
                Instrument = new InstrumentKey("FX:GBP/USD"),
                From = now.AddDays(-2),
                To = now,
                Runtime = new BacktestRuntimeOptions
                {
                    TradingConditions = new TradingConditionOptions
                    {
                        Enabled = true,
                        AllowedSessions = [TradingSession.London, TradingSession.LondonNewYorkOverlap],
                        InstrumentAllowedSessions = new Dictionary<string, IReadOnlyList<TradingSession>>
                        {
                            ["FX:GBP/USD"] = [TradingSession.London]
                        }
                    },
                    Financing = new FinancingOptions
                    {
                        Enabled = true,
                        InstrumentRates = new Dictionary<string, FinancingRate>
                        {
                            ["FX:GBP/USD"] = new() { LongAnnualPercent = -2m, ShortAnnualPercent = 1m }
                        },
                        Holidays = [new DateOnly(2026, 12, 25)]
                    }
                }
            }
        };

        try
        {
            await repository.SaveAsync(snapshot);
            SimulationJobSnapshot? loaded = await repository.GetAsync(id);

            Assert.Multiple(() =>
            {
                Assert.That(loaded, Is.Not.Null);
                Assert.That(loaded!.Request!.Runtime.TradingConditions.AllowedSessions,
                    Is.EqualTo(new[] { TradingSession.London, TradingSession.LondonNewYorkOverlap }));
                Assert.That(loaded.Request.Runtime.TradingConditions
                    .InstrumentAllowedSessions["FX:GBP/USD"],
                    Is.EqualTo(new[] { TradingSession.London }));
                Assert.That(loaded.Request.Runtime.Financing.Holidays,
                    Is.EqualTo(new[] { new DateOnly(2026, 12, 25) }));
                Assert.That(Directory.EnumerateFiles(Path.Combine(directory, "quarantine"), "*.json"), Is.Empty);
            });
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static SimulationJobSnapshot CreateQueuedSnapshot(Guid id) => new()
    {
        Id = id,
        Revision = 1,
        Status = SimulationJobStatus.Queued,
        CreatedAt = DateTimeOffset.UtcNow,
        Instrument = "FX:GBP/JPY",
        RequestedFrom = DateTimeOffset.UtcNow.AddDays(-1),
        RequestedTo = DateTimeOffset.UtcNow,
        ProcessedBaseCandles = 0,
        ProgressPercent = 0m,
        CandlesPerSecond = 0m,
        Strategies = [],
        IsComplete = false
    };

}
