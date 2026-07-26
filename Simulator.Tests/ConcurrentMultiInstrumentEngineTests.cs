using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Agent.Abstractions;
using Agent.Models;
using Brokers.Models;
using ChartAnnotator.Engine;
using ChartAnnotator.MarketData;
using RiskManager;
using RiskManager.Conditions;
using Simulator.Abstractions;
using Simulator.Engine;
using Simulator.MarketData;
using Simulator.Models;

namespace Simulator.Tests;

/// <summary>
/// Targets <see cref="StreamingComparativeEngine"/>'s cross-instrument concurrency path
/// (each traded instrument now runs its own fetch/annotate/evaluate task, merged back into
/// chronological order by one committer - see RunInstrumentsConcurrentlyAsync). Unlike
/// <see cref="MultiInstrumentPortfolioClockTests"/>, which proves the engine mechanics are
/// correct for a single run, these tests specifically hunt for concurrency bugs: races/
/// nondeterminism from thread scheduling, starvation of a slow or short-lived instrument
/// behind a fast/long one, and whether a mid-run failure is actually isolated (or actually
/// cancels everything) instead of hanging or silently corrupting output.
/// </summary>
[TestFixture]
public sealed class ConcurrentMultiInstrumentEngineTests
{
    private static readonly InstrumentKey EurUsd = new("FX:EUR/USD");
    private static readonly InstrumentKey GbpUsd = new("FX:GBP/USD");
    // Deliberately all USD-quote pairs (not e.g. USD/JPY): a JPY-quoted pair hit an unrelated,
    // pre-existing margin/conversion gap in this minimal test harness (no FX conversion source
    // configured) that has nothing to do with the concurrency behaviour under test here - see
    // the conversation notes for that separate finding. Kept out of these fixtures so failures
    // here can only mean an actual concurrency bug.
    private static readonly InstrumentKey NzdUsd = new("FX:NZD/USD");
    private static readonly InstrumentKey AudUsd = new("FX:AUD/USD");

    [Test]
    public async Task RepeatedRuns_ProduceIdenticalResultsEveryTime()
    {
        // The clearest signature of a race is nondeterminism: the same input producing a
        // different output on a different scheduling of the same concurrent tasks. Deliberately
        // staggered lengths/start times so the four producers are never in lockstep with each
        // other - that's exactly when interleaving varies most between runs.
        BarInterval interval = BarInterval.Minutes(1);
        DateTimeOffset start = new(2026, 7, 13, 8, 0, 0, TimeSpan.Zero);

        (InstrumentKey Instrument, int Count, DateTimeOffset Start, decimal Price)[] specs =
        [
            (EurUsd, 24, start, 1.10m),
            (GbpUsd, 17, start.AddMinutes(3), 1.30m),
            (NzdUsd, 31, start, 0.66m),
            (AudUsd, 12, start.AddMinutes(9), 0.66m)
        ];

        (int TradeCount, decimal FinalBalance, decimal? EntryPrice)[]? baseline = null;

        for (int iteration = 0; iteration < 15; iteration++)
        {
            var candlesByInstrument = specs.ToDictionary(
                spec => spec.Instrument,
                spec => BuildFlatSeries(spec.Instrument, interval, spec.Start, spec.Count, spec.Price));
            var stream = new MultiInstrumentCandleStream(candlesByInstrument);
            var engine = new StreamingComparativeEngine(
                stream,
                [.. specs.Select(spec => new StrategyFactoryEntry(
                    $"strategy-{spec.Instrument.Value}", new OneShotBuyAgent(interval, spec.Price), spec.Instrument))]);

            string output = CreateTempOutputDirectory();
            try
            {
                ComparativeSimulationResult result = await RunAsync(
                    engine, output, EurUsd, interval, start, evaluationTo: start.AddMinutes(40));

                var observed = specs
                    .Select(spec => result.Strategies.Single(s => s.StrategyId == $"strategy-{spec.Instrument.Value}"))
                    .Select(s => (
                        s.Result.Trades.Count,
                        s.Result.FinalBalance,
                        EntryPrice: s.Result.Trades.Count > 0 ? s.Result.Trades[0].EntryPrice : 0m))
                    .ToArray();

                if (baseline is null)
                {
                    baseline = observed;
                }
                else
                {
                    Assert.That(observed, Is.EqualTo(baseline),
                        $"Iteration {iteration} produced different results than iteration 0 - " +
                        "this points at a race in the concurrent producer/committer path.");
                }
            }
            finally
            {
                Directory.Delete(output, recursive: true);
            }
        }
    }

    [Test]
    public async Task TradeJournal_IsExportedToDiskInsteadOfDiscardedWhenTheProcessExits()
    {
        // ITradeJournal.Snapshot() was never called anywhere in the codebase - every signal
        // evaluation, rejection reason, and safety-state change accumulated in-memory for the
        // whole run and was then simply thrown away. This proves the export actually reaches
        // disk and isn't just a value nobody reads, same as before this fix.
        BarInterval interval = BarInterval.Minutes(1);
        DateTimeOffset start = new(2026, 7, 13, 8, 0, 0, TimeSpan.Zero);
        Candle[] eurUsdCandles = BuildFlatSeries(EurUsd, interval, start, count: 20, price: 1.10m);
        var stream = new MultiInstrumentCandleStream(new Dictionary<InstrumentKey, Candle[]>
        {
            [EurUsd] = eurUsdCandles
        });
        var engine = new StreamingComparativeEngine(
            stream,
            [new StrategyFactoryEntry("strategy-eurusd", new OneShotBuyAgent(interval, 1.10m), EurUsd)]);

        string output = CreateTempOutputDirectory();
        try
        {
            await RunAsync(engine, output, EurUsd, interval, start, evaluationTo: start.AddMinutes(20));

            string journalPath = Path.Combine(output, "strategies", "strategy_eurusd", "journal.ndjson");
            Assert.That(File.Exists(journalPath), Is.True);
            string[] lines = await File.ReadAllLinesAsync(journalPath);
            Assert.That(lines, Is.Not.Empty);
            Assert.That(lines, Has.Some.Contains("SignalEvaluated"),
                "The exported journal must contain the same structured decision events " +
                "ITradeJournal.Append records during the run (e.g. SignalEvaluated), not just a " +
                "placeholder file.");
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    [Test]
    public async Task MarketReplayChunks_RemainStrictlyChronologicalUnderConcurrency()
    {
        // ChunkedReplayWriter derives each chunk's declared FromTime/ToTime (and
        // FromSequence/ToSequence) from the first/last row physically written to it. If the
        // committer's chronological merge were racy, concurrently-produced rows could land in
        // the file out of AvailableAt/Sequence order without anything throwing - this would
        // silently corrupt Dashboard replay scrubbing rather than fail loudly, so it has to be
        // checked against the actual output bytes, not just trade counts.
        BarInterval interval = BarInterval.Minutes(1);
        DateTimeOffset start = new(2026, 7, 13, 8, 0, 0, TimeSpan.Zero);

        (InstrumentKey Instrument, int Count, DateTimeOffset Start, decimal Price)[] specs =
        [
            (EurUsd, 40, start, 1.10m),
            (GbpUsd, 25, start.AddMinutes(5), 1.30m),
            (NzdUsd, 33, start.AddMinutes(2), 0.66m),
            (AudUsd, 18, start.AddMinutes(11), 0.66m)
        ];

        var candlesByInstrument = specs.ToDictionary(
            spec => spec.Instrument,
            spec => BuildFlatSeries(spec.Instrument, interval, spec.Start, spec.Count, spec.Price));
        var stream = new MultiInstrumentCandleStream(candlesByInstrument);
        var engine = new StreamingComparativeEngine(
            stream,
            [.. specs.Select(spec => new StrategyFactoryEntry(
                $"strategy-{spec.Instrument.Value}", new OneShotBuyAgent(interval, spec.Price), spec.Instrument))]);

        string output = CreateTempOutputDirectory();
        try
        {
            await RunAsync(engine, output, EurUsd, interval, start, evaluationTo: start.AddMinutes(60));

            (long Sequence, DateTimeOffset AvailableAt, string? Instrument)[] rows = ReadMarketReplayRows(output);

            Assert.That(rows, Is.Not.Empty);
            for (int i = 1; i < rows.Length; i++)
            {
                Assert.That(rows[i].Sequence, Is.GreaterThan(rows[i - 1].Sequence),
                    $"Row {i} sequence {rows[i].Sequence} did not increase from row {i - 1}'s " +
                    $"{rows[i - 1].Sequence} - the committer let output race ahead of true commit order.");
                Assert.That(rows[i].AvailableAt, Is.GreaterThanOrEqualTo(rows[i - 1].AvailableAt),
                    $"Row {i} at {rows[i].AvailableAt:o} appears before row {i - 1} at " +
                    $"{rows[i - 1].AvailableAt:o} - concurrent instruments were committed out of " +
                    "chronological order.");
            }
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    [Test]
    public async Task TiedTimestamps_TieBreakIsInstrumentDeterministicAcrossRuns()
    {
        // Two instruments quoting the exact same candle timestamps throughout is the case most
        // likely to expose a nondeterministic tie-break: both producers finish evaluating their
        // frame for time T at roughly the same moment, so whichever happens to reach the
        // committer's buffer first is a pure scheduling race unless the committer's winner
        // selection enforces the tie-break itself (by instrument value) rather than by arrival
        // order.
        BarInterval interval = BarInterval.Minutes(1);
        DateTimeOffset start = new(2026, 7, 13, 8, 0, 0, TimeSpan.Zero);
        Candle[] eurUsdCandles = BuildFlatSeries(EurUsd, interval, start, count: 20, price: 1.10m);
        Candle[] gbpUsdCandles = BuildFlatSeries(GbpUsd, interval, start, count: 20, price: 1.30m);

        for (int iteration = 0; iteration < 10; iteration++)
        {
            var stream = new MultiInstrumentCandleStream(new Dictionary<InstrumentKey, Candle[]>
            {
                [EurUsd] = eurUsdCandles,
                [GbpUsd] = gbpUsdCandles
            });
            var engine = new StreamingComparativeEngine(
                stream,
                [
                    new StrategyFactoryEntry("strategy-eurusd", new OneShotBuyAgent(interval, 1.10m), EurUsd),
                    new StrategyFactoryEntry("strategy-gbpusd", new OneShotBuyAgent(interval, 1.30m), GbpUsd)
                ]);

            string output = CreateTempOutputDirectory();
            try
            {
                await RunAsync(engine, output, EurUsd, interval, start, evaluationTo: start.AddMinutes(20));

                (long Sequence, DateTimeOffset AvailableAt, string? Instrument)[] rows = ReadMarketReplayRows(output);
                var firstTimestamp = rows[0].AvailableAt;
                string[] instrumentsAtFirstTimestamp = rows
                    .Where(row => row.AvailableAt == firstTimestamp)
                    .Select(row => row.Instrument!)
                    .ToArray();

                Assert.That(instrumentsAtFirstTimestamp, Has.Length.EqualTo(2),
                    "Both instruments share candle timestamps, so the first AvailableAt must " +
                    "contain exactly one row per instrument.");
                Assert.That(instrumentsAtFirstTimestamp[0], Is.EqualTo(EurUsd.Value),
                    $"Iteration {iteration}: EUR/USD (ordinal-earlier) must always win the tie " +
                    "against GBP/USD, regardless of which producer's task happened to finish first.");
            }
            finally
            {
                Directory.Delete(output, recursive: true);
            }
        }
    }

    [Test]
    public async Task VeryDisparateStreamLengths_CompletesWithoutHangingOrStarvingShortInstruments()
    {
        // One instrument with far more candles than the other two - both a backpressure check
        // (the long stream's channel should throttle it, not let it run unboundedly ahead) and a
        // starvation check (the short streams must still get their own trade/liquidation
        // promptly, not wait behind the long one because the committer favours whichever
        // producer is fastest).
        BarInterval interval = BarInterval.Minutes(1);
        DateTimeOffset start = new(2026, 7, 13, 8, 0, 0, TimeSpan.Zero);
        Candle[] longStream = BuildFlatSeries(EurUsd, interval, start, count: 400, price: 1.10m);
        Candle[] shortStreamA = BuildFlatSeries(GbpUsd, interval, start, count: 6, price: 1.30m);
        Candle[] shortStreamB = BuildFlatSeries(NzdUsd, interval, start, count: 6, price: 0.66m);

        var stream = new MultiInstrumentCandleStream(new Dictionary<InstrumentKey, Candle[]>
        {
            [EurUsd] = longStream,
            [GbpUsd] = shortStreamA,
            [NzdUsd] = shortStreamB
        });
        var engine = new StreamingComparativeEngine(
            stream,
            [
                new StrategyFactoryEntry("strategy-eurusd", new OneShotBuyAgent(interval, 1.10m), EurUsd),
                new StrategyFactoryEntry("strategy-gbpusd", new OneShotBuyAgent(interval, 1.30m), GbpUsd),
                new StrategyFactoryEntry("strategy-nzdusd", new OneShotBuyAgent(interval, 0.66m), NzdUsd)
            ]);

        string output = CreateTempOutputDirectory();
        try
        {
            Task<ComparativeSimulationResult> runTask =
                RunAsync(engine, output, EurUsd, interval, start, evaluationTo: start.AddMinutes(400));
            Task completed = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(30)));

            Assert.That(completed, Is.SameAs(runTask),
                "The run did not complete within 30s - the short instruments were likely " +
                "starved behind the long one, or the committer/producers deadlocked.");

            ComparativeSimulationResult result = await runTask;
            StrategySimulationResult eurUsd = result.Strategies.Single(s => s.StrategyId == "strategy-eurusd");
            StrategySimulationResult gbpUsd = result.Strategies.Single(s => s.StrategyId == "strategy-gbpusd");
            StrategySimulationResult nzdUsd = result.Strategies.Single(s => s.StrategyId == "strategy-nzdusd");

            Assert.Multiple(() =>
            {
                Assert.That(eurUsd.Result.Trades, Has.Count.EqualTo(1));
                Assert.That(gbpUsd.Result.Trades, Has.Count.EqualTo(1),
                    "The short GBP/USD stream must still complete/liquidate rather than being " +
                    "starved by the much longer EUR/USD stream.");
                Assert.That(nzdUsd.Result.Trades, Has.Count.EqualTo(1),
                    "The short USD/JPY stream must still complete/liquidate rather than being " +
                    "starved by the much longer EUR/USD stream.");
                Assert.That(gbpUsd.Result.OpenPositions, Is.Empty);
                Assert.That(nzdUsd.Result.OpenPositions, Is.Empty);
            });
        }
        finally
        {
            if (Directory.Exists(output))
                Directory.Delete(output, recursive: true);
        }
    }

    [Test]
    public async Task StopFailedStrategyOnly_FailingInstrumentDoesNotBlockOrCorruptOthers()
    {
        // A mid-run agent exception on one instrument must not hang the run, corrupt the other
        // instruments' state (they run on entirely separate tasks/sessions, so there is no
        // shared mutable state to corrupt - this proves it), or silently stop the healthy
        // instruments alongside the failed one.
        BarInterval interval = BarInterval.Minutes(1);
        DateTimeOffset start = new(2026, 7, 13, 8, 0, 0, TimeSpan.Zero);
        Candle[] eurUsdCandles = BuildFlatSeries(EurUsd, interval, start, count: 20, price: 1.10m);
        Candle[] gbpUsdCandles = BuildFlatSeries(GbpUsd, interval, start, count: 20, price: 1.30m);
        Candle[] nzdUsdCandles = BuildFlatSeries(NzdUsd, interval, start, count: 20, price: 0.66m);

        var stream = new MultiInstrumentCandleStream(new Dictionary<InstrumentKey, Candle[]>
        {
            [EurUsd] = eurUsdCandles,
            [GbpUsd] = gbpUsdCandles,
            [NzdUsd] = nzdUsdCandles
        });
        var engine = new StreamingComparativeEngine(
            stream,
            [
                new StrategyFactoryEntry("strategy-eurusd", new OneShotBuyAgent(interval, 1.10m), EurUsd),
                new StrategyFactoryEntry("strategy-gbpusd", new ThrowingAgent(failOnCallNumber: 2), GbpUsd),
                new StrategyFactoryEntry("strategy-nzdusd", new OneShotBuyAgent(interval, 0.66m), NzdUsd)
            ]);

        string output = CreateTempOutputDirectory();
        try
        {
            ComparativeSimulationResult result = await RunAsync(
                engine, output, EurUsd, interval, start, evaluationTo: start.AddMinutes(20),
                failurePolicy: StrategyFailurePolicy.StopFailedStrategyOnly);

            StrategySimulationResult eurUsd = result.Strategies.Single(s => s.StrategyId == "strategy-eurusd");
            StrategySimulationResult gbpUsd = result.Strategies.Single(s => s.StrategyId == "strategy-gbpusd");
            StrategySimulationResult nzdUsd = result.Strategies.Single(s => s.StrategyId == "strategy-nzdusd");

            Assert.Multiple(() =>
            {
                Assert.That(gbpUsd.IsComplete, Is.False, "The throwing agent's instrument must be marked failed.");
                Assert.That(gbpUsd.FailedAtSequence, Is.Not.Null);
                Assert.That(eurUsd.IsComplete, Is.True,
                    "EUR/USD runs on its own task/session - GBP/USD failing must not affect it.");
                Assert.That(nzdUsd.IsComplete, Is.True,
                    "USD/JPY runs on its own task/session - GBP/USD failing must not affect it.");
                Assert.That(eurUsd.Result.Trades, Has.Count.EqualTo(1));
                Assert.That(nzdUsd.Result.Trades, Has.Count.EqualTo(1));

                Assert.That(gbpUsd.FailureRecord, Is.Not.Null,
                    "The failure must carry richer context than the bare exception string.");
                Assert.That(gbpUsd.FailureRecord!.Exception, Does.Contain("Injected failure for concurrency test"));
                Assert.That(gbpUsd.FailureRecord.StrategyName, Is.EqualTo(gbpUsd.StrategyName));
                Assert.That(eurUsd.FailureRecord, Is.Null, "A healthy strategy must not carry a failure record.");
            });

            string failureJsonPath = Path.Combine(output, "strategies", "strategy_gbpusd", "failure.json");
            Assert.That(File.Exists(failureJsonPath), Is.True,
                "A failed strategy's context must be written to disk, not only kept in memory - " +
                "this is what makes it findable after the process has already exited.");
            string failureJson = await File.ReadAllTextAsync(failureJsonPath);
            Assert.That(failureJson, Does.Contain("Injected failure for concurrency test"));

            string healthyFailureJsonPath = Path.Combine(output, "strategies", "strategy_eurusd", "failure.json");
            Assert.That(File.Exists(healthyFailureJsonPath), Is.False,
                "A healthy strategy must not get a failure.json at all.");
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    [Test]
    public async Task StopEntireComparison_CancelsOtherProducersRatherThanRunningThemToCompletion()
    {
        // The default policy: one instrument failing must stop the whole comparison. The
        // meaningful concurrency assertion here isn't just "it throws" (a single-threaded engine
        // would too) - it's that the *other* producers actually get cancelled instead of running
        // their own streams out to completion in the background. EUR/USD is given a much longer
        // stream than GBP/USD's failure point; if cancellation wasn't propagated, EUR/USD's agent
        // would still be invoked for every one of its 300 candles before the Task.WhenAll below
        // could observe the failure.
        BarInterval interval = BarInterval.Minutes(1);
        DateTimeOffset start = new(2026, 7, 13, 8, 0, 0, TimeSpan.Zero);
        Candle[] eurUsdCandles = BuildFlatSeries(EurUsd, interval, start, count: 300, price: 1.10m);
        Candle[] gbpUsdCandles = BuildFlatSeries(GbpUsd, interval, start, count: 10, price: 1.30m);

        var stream = new MultiInstrumentCandleStream(new Dictionary<InstrumentKey, Candle[]>
        {
            [EurUsd] = eurUsdCandles,
            [GbpUsd] = gbpUsdCandles
        });
        var countingAgent = new CountingAgent(interval);
        var engine = new StreamingComparativeEngine(
            stream,
            [
                new StrategyFactoryEntry("strategy-eurusd", countingAgent, EurUsd),
                new StrategyFactoryEntry("strategy-gbpusd", new ThrowingAgent(failOnCallNumber: 2), GbpUsd)
            ]);

        string output = CreateTempOutputDirectory();
        try
        {
            Task<ComparativeSimulationResult> runTask = RunAsync(
                engine, output, EurUsd, interval, start, evaluationTo: start.AddMinutes(300),
                failurePolicy: StrategyFailurePolicy.StopEntireComparison);
            Task completed = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(15)));

            Assert.That(completed, Is.SameAs(runTask),
                "StopEntireComparison must fail fast, not hang waiting for the long stream.");

            InvalidOperationException? thrown = null;
            try
            {
                await runTask;
            }
            catch (InvalidOperationException exception)
            {
                thrown = exception;
            }

            Assert.That(thrown, Is.Not.Null, "The failed strategy must propagate out of RunAsync.");
            Assert.That(countingAgent.CallCount, Is.LessThan(300),
                "EUR/USD's producer must have been cancelled before exhausting its own 300-candle " +
                "stream - if this reaches 300, GBP/USD's failure never actually cancelled it and " +
                "the two instruments merely raced to their own natural completion.");
        }
        finally
        {
            if (Directory.Exists(output))
                Directory.Delete(output, recursive: true);
        }
    }

    [Test]
    public async Task ExternalCancellation_StopsAllProducersAndCommitterPromptly()
    {
        // A caller-supplied token (e.g. the user cancelling a running job from the dashboard)
        // must tear down every producer and the committer promptly, not hang waiting for
        // long-running instrument streams to finish naturally.
        BarInterval interval = BarInterval.Minutes(1);
        DateTimeOffset start = new(2026, 7, 13, 8, 0, 0, TimeSpan.Zero);
        Candle[] eurUsdCandles = BuildFlatSeries(EurUsd, interval, start, count: 500, price: 1.10m);
        Candle[] gbpUsdCandles = BuildFlatSeries(GbpUsd, interval, start, count: 500, price: 1.30m);
        Candle[] nzdUsdCandles = BuildFlatSeries(NzdUsd, interval, start, count: 500, price: 0.66m);

        var stream = new MultiInstrumentCandleStream(new Dictionary<InstrumentKey, Candle[]>
        {
            [EurUsd] = eurUsdCandles,
            [GbpUsd] = gbpUsdCandles,
            [NzdUsd] = nzdUsdCandles
        });
        var engine = new StreamingComparativeEngine(
            stream,
            [
                new StrategyFactoryEntry("strategy-eurusd", new OneShotBuyAgent(interval, 1.10m), EurUsd),
                new StrategyFactoryEntry("strategy-gbpusd", new OneShotBuyAgent(interval, 1.30m), GbpUsd),
                new StrategyFactoryEntry("strategy-nzdusd", new OneShotBuyAgent(interval, 0.66m), NzdUsd)
            ]);

        string output = CreateTempOutputDirectory();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(25));
        try
        {
            Task<ComparativeSimulationResult> runTask = RunAsync(
                engine, output, EurUsd, interval, start, evaluationTo: start.AddMinutes(500),
                cancellationToken: cts.Token);
            Task completed = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(15)));

            Assert.That(completed, Is.SameAs(runTask),
                "External cancellation must stop the run within seconds, not hang.");
            // Catch (not Throws): TaskCanceledException is the concrete type in practice
            // (e.g. surfacing through JSON serialization's own cancellation checks during
            // manifest writes) and it is an OperationCanceledException subtype, not the exact
            // base type - Assert.ThrowsAsync<T> requires an exact match, Assert.CatchAsync<T>
            // accepts subtypes.
            Assert.CatchAsync<OperationCanceledException>(async () => await runTask);
        }
        finally
        {
            if (Directory.Exists(output))
                Directory.Delete(output, recursive: true);
        }
    }

    private static string CreateTempOutputDirectory()
    {
        string output = Path.Combine(
            Path.GetTempPath(), "tradinghub-concurrent-multi-instrument", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        return output;
    }

    private static (long Sequence, DateTimeOffset AvailableAt, string? Instrument)[] ReadMarketReplayRows(
        string output)
    {
        string marketDirectory = Path.Combine(output, "market");
        string[] chunkFiles = Directory.Exists(marketDirectory)
            ? [.. Directory.GetFiles(marketDirectory, "chunk-*.json.gz").OrderBy(path => path, StringComparer.Ordinal)]
            : [];

        var rows = new List<(long Sequence, DateTimeOffset AvailableAt, string? Instrument)>();
        foreach (string chunkFile in chunkFiles)
        {
            using FileStream file = File.OpenRead(chunkFile);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using JsonDocument document = JsonDocument.Parse(gzip);
            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                rows.Add((
                    element.GetProperty("sequence").GetInt64(),
                    element.GetProperty("availableAt").GetDateTimeOffset(),
                    element.TryGetProperty("instrument", out JsonElement instrumentElement)
                        ? instrumentElement.GetString()
                        : null));
            }
        }

        return [.. rows];
    }

    private static async Task<ComparativeSimulationResult> RunAsync(
        StreamingComparativeEngine engine,
        string output,
        InstrumentKey templateInstrument,
        BarInterval interval,
        DateTimeOffset start,
        DateTimeOffset evaluationTo,
        StrategyFailurePolicy failurePolicy = StrategyFailurePolicy.StopEntireComparison,
        CancellationToken cancellationToken = default)
    {
        var runtime = new BacktestRuntimeOptions
        {
            BaseInterval = interval,
            AnalysisBaseInterval = interval,
            AnalysisIntervals = [interval],
            WarmupDays = 0,
            TradingConditions = new TradingConditionOptions { Enabled = false },
            ReplayChunkSize = 20,
            ProgressPublishIntervalMilliseconds = 50,
            StrategyFailurePolicy = failurePolicy,
            PositionSizing = new PositionSizingOptions
            {
                Mode = PositionSizingMode.FixedQuantity,
                FixedQuantity = 1_000m,
                MinimumQuantity = 1m,
                QuantityStep = 1m,
                Leverage = 20m
            }
        };
        return await engine.RunAsync(
            new StreamingComparativeEngineOptions
            {
                SimulationId = Guid.NewGuid(),
                Instrument = templateInstrument,
                EvaluationFrom = start,
                EvaluationTo = evaluationTo,
                StreamFrom = start,
                AnalysisIntervals = runtime.AnalysisIntervals,
                Runtime = runtime,
                SimulationOptions = new SimulationOptions
                {
                    StartingBalance = 100_000m,
                    BaseCurrency = "USD",
                    Leverage = 20m,
                    CommissionRate = 0m,
                    SpreadBasisPoints = 0m,
                    SlippageBasisPoints = 0m,
                    CloseOpenPositionsAtEnd = true,
                    BaseCandleGapPolicy = BaseCandleGapPolicy.Throw
                },
                OutputDirectory = output,
                InputStreamId = "concurrent-multi-instrument-test",
                AnnotationOptions = new ChartAnnotationOptions
                {
                    AtrPeriod = 3,
                    RsiPeriod = 3,
                    BollingerPeriod = 3,
                    HeavyAnalysisEveryCandles = 1
                }
            },
            new HistoricalCandleRequest(templateInstrument, interval, start, evaluationTo),
            cancellationToken);
    }

    private static Candle[] BuildFlatSeries(
        InstrumentKey instrument, BarInterval interval, DateTimeOffset start, int count, decimal price) =>
        Enumerable.Range(0, count)
            .Select(index => TestCandles.Create(
                instrument, start.AddMinutes(index), interval, price, price, price, price))
            .ToArray();

    /// <summary>Enters once, then stays flat - identical intent to the sibling class in
    /// MultiInstrumentPortfolioClockTests, duplicated locally since that one is a private
    /// nested type.</summary>
    private sealed class OneShotBuyAgent(BarInterval interval, decimal referencePrice) : ITradingAgent
    {
        private bool _submitted;

        public string Name => "Concurrent-test one-shot entry";
        public IReadOnlySet<BarInterval> RequiredIntervals { get; } = new HashSet<BarInterval> { interval };
        public BarInterval TriggerInterval => interval;
        public AgentExitManagementMode ExitManagementMode => AgentExitManagementMode.Bracket;

        public Task<AgentDecision> EvaluateAsync(
            AgentMarketContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_submitted)
            {
                return Task.FromResult(new AgentDecision
                {
                    Action = AgentAction.Observe,
                    Instrument = context.Instrument,
                    Confidence = 0m,
                    CreatedAt = context.Timestamp,
                    Reason = "Already submitted"
                });
            }
            _submitted = true;
            return Task.FromResult(new AgentDecision
            {
                DecisionId = $"entry:{context.StrategyId}",
                SetupId = $"setup:{context.StrategyId}",
                Action = AgentAction.Buy,
                Instrument = context.Instrument,
                SuggestedQuantity = 1_000m,
                ReferencePrice = referencePrice,
                StopLossPrice = referencePrice * 0.99m,
                TakeProfitPrice = referencePrice * 1.05m,
                ExpectedRewardRisk = 5m,
                Confidence = 70m,
                CreatedAt = context.Timestamp,
                Reason = "Deterministic concurrent-test entry"
            });
        }
    }

    /// <summary>Never trades; throws on its Nth evaluation to simulate a strategy-logic bug on
    /// one instrument without touching any other instrument's state (there is none shared).</summary>
    private sealed class ThrowingAgent(int failOnCallNumber) : ITradingAgent
    {
        private int _callCount;

        public string Name => "Failure-injection agent";
        public IReadOnlySet<BarInterval> RequiredIntervals { get; } = new HashSet<BarInterval> { BarInterval.Minutes(1) };
        public BarInterval TriggerInterval => BarInterval.Minutes(1);
        public AgentExitManagementMode ExitManagementMode => AgentExitManagementMode.Bracket;

        public Task<AgentDecision> EvaluateAsync(
            AgentMarketContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _callCount) == failOnCallNumber)
                throw new InvalidOperationException("Injected failure for concurrency test.");

            return Task.FromResult(new AgentDecision
            {
                Action = AgentAction.Observe,
                Instrument = context.Instrument,
                Confidence = 0m,
                CreatedAt = context.Timestamp,
                Reason = "Waiting to fail"
            });
        }
    }

    /// <summary>Never trades; counts how many times it was actually invoked, so a test can prove
    /// a long-running instrument's producer was cancelled mid-stream rather than left to finish
    /// on its own.</summary>
    private sealed class CountingAgent(BarInterval interval) : ITradingAgent
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public string Name => "Call-counting agent";
        public IReadOnlySet<BarInterval> RequiredIntervals { get; } = new HashSet<BarInterval> { interval };
        public BarInterval TriggerInterval => interval;
        public AgentExitManagementMode ExitManagementMode => AgentExitManagementMode.Bracket;

        public Task<AgentDecision> EvaluateAsync(
            AgentMarketContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(new AgentDecision
            {
                Action = AgentAction.Observe,
                Instrument = context.Instrument,
                Confidence = 0m,
                CreatedAt = context.Timestamp,
                Reason = "Counting only"
            });
        }
    }

    private sealed class MultiInstrumentCandleStream(
        IReadOnlyDictionary<InstrumentKey, Candle[]> candlesByInstrument) : IHistoricalCandleStream
    {
        public async IAsyncEnumerable<MarketCandle> StreamAsync(
            HistoricalCandleRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Candle[] source = candlesByInstrument.GetValueOrDefault(request.Instrument, []);
            foreach (Candle candle in source
                         .Where(item => item.OpenTime >= request.From && item.OpenTime < request.To)
                         .OrderBy(item => item.OpenTime))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return MarketCandle.FromMid(candle);
                await Task.Yield();
            }
        }
    }
}
