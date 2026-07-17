using Agent.Abstractions;
using Agent.Models;
using Agent.Strategies;
using Brokers.Models;
using ChartAnnotator.Engine;
using Simulator.Engine;
using Simulator.Jobs;
using Simulator.MarketData;
using Simulator.Models;
using Simulator.Services;
using TradingCore.MarketData;
using TradingCore.Pipeline;

namespace Simulator.Tests;

[TestFixture]
public sealed class Phase2StreamingAndWorkersTests
{
    [Test]
    public async Task First_Page_Yields_Before_Source_Completes()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new GatedPagedSource(gate.Task);
        var stream = new LowWatermarkPrefetchStream(source, capacity: 100, lowWatermark: 10);
        var request = new HistoricalCandleRequest(
            new InstrumentKey("FX:EUR/USD"),
            BarInterval.Minutes(1),
            new DateTimeOffset(2025, 1, 2, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2025, 1, 2, 1, 0, 0, TimeSpan.Zero),
            PageSize: 20);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var enumerator = stream.StreamAsync(request, cts.Token).GetAsyncEnumerator(cts.Token);
        Assert.That(await enumerator.MoveNextAsync(), Is.True);
        Assert.That(enumerator.Current.OpenTime, Is.Not.EqualTo(default(DateTimeOffset)));
        // The corrected hysteresis keeps filling toward capacity, so the second
        // page may already be in flight while rows from the first page are consumed.
        Assert.That(source.PagesRequested, Is.EqualTo(2));
        gate.SetResult();
        cts.Cancel();
    }

    [Test]
    public async Task LowWatermark_Hysteresis_FillsTowardCapacityAndReportsWaits()
    {
        InstrumentKey instrument = new("FX:EUR/USD");
        BarInterval interval = BarInterval.Minutes(1);
        DateTimeOffset start = new(2025, 6, 2, 0, 0, 0, TimeSpan.Zero);
        Candle[] candles = BuildCandles(instrument, interval, start, 300);
        var stream = new LowWatermarkPrefetchStream(
            new EnumerablePagedCandleSource(candles),
            capacity: 64,
            lowWatermark: 8);
        var request = new HistoricalCandleRequest(
            instrument,
            interval,
            start,
            start.AddMinutes(candles.Length),
            PageSize: 16);

        int received = 0;
        await foreach (MarketCandle _ in stream.StreamAsync(request))
        {
            received++;
            if (received < 100)
                await Task.Delay(1);
        }
        PrefetchDiagnostics diagnostics = stream.SnapshotDiagnostics();

        Assert.Multiple(() =>
        {
            Assert.That(received, Is.EqualTo(candles.Length));
            Assert.That(diagnostics.PeakUnread, Is.GreaterThan(8 + 1));
            Assert.That(diagnostics.PeakUnread, Is.LessThanOrEqualTo(64));
            Assert.That(diagnostics.PagesRequested, Is.GreaterThan(1));
            Assert.That(diagnostics.SourceWaits, Is.GreaterThan(0));
            Assert.That(diagnostics.ConsumerWaits, Is.GreaterThan(0));
            Assert.That(diagnostics.CurrentUnread, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Persistent_Workers_Process_Many_Frames_With_Bounded_Hosts()
    {
        InstrumentKey instrument = new("FX:EUR/USD");
        BarInterval baseInterval = BarInterval.Minutes(1);
        DateTimeOffset start = new(2025, 6, 2, 8, 0, 0, TimeSpan.Zero);
        const int count = 400;
        Candle[] candles = BuildCandles(instrument, baseInterval, start, count);

        ComparativeSimulationResult parallel = await RunAsync(
            candles, instrument, start, start.AddMinutes(count), StrategyExecutionMode.ParallelWorkers);
        ComparativeSimulationResult sequential = await RunAsync(
            candles, instrument, start, start.AddMinutes(count), StrategyExecutionMode.Sequential);

        Assert.That(parallel.InputHash, Is.EqualTo(sequential.InputHash));
        foreach (StrategySimulationResult left in sequential.Strategies)
        {
            StrategySimulationResult right = parallel.Strategies.Single(item => item.StrategyId == left.StrategyId);
            Assert.That(right.Result.Trades.Count, Is.EqualTo(left.Result.Trades.Count));
            Assert.That(right.Result.FinalBalance, Is.EqualTo(left.Result.FinalBalance));
            Assert.That(right.Metrics.ProcessedFrames, Is.EqualTo(left.Metrics.ProcessedFrames));
        }
    }

    [Test]
    public async Task Task_Worker_Mode_Matches_Sequential_Fingerprint_Counts()
    {
        InstrumentKey instrument = new("FX:EUR/USD");
        BarInterval baseInterval = BarInterval.Minutes(1);
        DateTimeOffset start = new(2025, 6, 2, 8, 0, 0, TimeSpan.Zero);
        const int count = 200;
        Candle[] candles = BuildCandles(instrument, baseInterval, start, count);

        ComparativeSimulationResult sequential = await RunAsync(
            candles, instrument, start, start.AddMinutes(count), StrategyExecutionMode.Sequential);
        ComparativeSimulationResult parallel = await RunAsync(
            candles, instrument, start, start.AddMinutes(count), StrategyExecutionMode.ParallelWorkers);

        Assert.That(parallel.InputHash, Is.EqualTo(sequential.InputHash));
        Assert.That(parallel.ProcessedBaseCandles, Is.EqualTo(sequential.ProcessedBaseCandles));
        foreach (StrategySimulationResult left in sequential.Strategies)
        {
            StrategySimulationResult right = parallel.Strategies.Single(item => item.StrategyId == left.StrategyId);
            Assert.That(right.Result.NetProfit, Is.EqualTo(left.Result.NetProfit));
        }
    }

    [Test]
    public async Task Job_Completion_Removes_Running_Entry_And_Await_Uses_Completion_Task()
    {
        InstrumentKey instrument = new("FX:EUR/USD");
        DateTimeOffset start = new(2025, 6, 2, 8, 0, 0, TimeSpan.Zero);
        Candle[] candles = BuildCandles(instrument, BarInterval.Minutes(1), start, 60);
        string temp = Path.Combine(Path.GetTempPath(), "th-phase2", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        await using var service = new BacktestApplicationService(
            new FileSimulationJobRepository(Path.Combine(temp, "jobs")));

        var request = new BacktestRequest
        {
            Instrument = instrument,
            From = start,
            To = start.AddHours(1),
            InlineCandles = candles,
            OutputDirectory = Path.Combine(temp, "out"),
            JobsDirectory = Path.Combine(temp, "jobs"),
            Runtime = new BacktestRuntimeOptions
            {
                WarmupDays = 0,
                PrefetchCapacity = 5_000,
                PrefetchLowWatermark = 500,
                SourcePageSize = 100,
                StrategyExecutionMode = StrategyExecutionMode.Sequential,
                ProgressPublishIntervalMilliseconds = 50,
                ReplayChunkSize = 50
            }
        };

        ComparativeSimulationResult result = await service.RunToCompletionAsync(request);
        Assert.That(result.ProcessedBaseCandles, Is.GreaterThan(0));
        SimulationJobSnapshot? snapshot = await service.GetAsync(result.SimulationId);
        Assert.That(snapshot, Is.Not.Null);
        Assert.That(snapshot!.IsComplete, Is.True);
        Assert.That(snapshot.Revision, Is.GreaterThan(0));
    }

    [Test]
    public async Task Async_Pause_Gate_Blocks_Until_Resume()
    {
        var gate = new AsyncPauseGate();
        gate.Pause();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
        Assert.ThrowsAsync<TaskCanceledException>(async () =>
            await gate.WaitIfPausedAsync(cts.Token));
        gate.Resume();
        await gate.WaitIfPausedAsync();
    }

    [Test]
    public void NearestToOpen_Policy_Is_Mapped()
    {
        var runtime = new BacktestRuntimeOptions
        {
            AmbiguousIntrabarPolicy = AmbiguousIntrabarPolicy.NearestToOpenFirst
        };
        Assert.That(runtime.ToOcoFillPolicy(), Is.EqualTo(OcoFillPolicy.NearestToOpenFirst));
    }

    /// <summary>
    /// Multi-agent architecture Phase 6: the simulator's equivalent of Live's new
    /// MarketSequence-based stale-candidate rejection (<c>LiveDecisionEpochCoordinator
    /// .SubmitCandidate</c>) is <see cref="StrategyWorkerHost"/>'s existing, unmodified
    /// out-of-order guard - since every frame for one strategy is processed strictly
    /// sequentially through one persistent worker (no concurrent-dispatch race to produce a
    /// "late" result in the first place), a stale/out-of-order frame is a hard, immediate
    /// failure rather than something that could silently enter a later decision batch.
    /// </summary>
    [Test]
    public async Task StrategyWorkerHost_OutOfOrderFrame_ThrowsInsteadOfSilentlyAccepting()
    {
        InstrumentKey instrument = new("FX:EUR/USD");
        BarInterval interval = BarInterval.Minutes(1);
        StrategySimulationSession session = StrategySimulationSession.Create(
            "always-observe",
            new AlwaysObserveTestAgent(interval),
            new SimulationOptions
            {
                StartingBalance = 100_000m,
                Leverage = 20m,
                CommissionRate = 0m,
                SpreadBasisPoints = 0m,
                SlippageBasisPoints = 0m,
                CloseOpenPositionsAtEnd = false
            },
            dataQualityOptions: new MarketDataQualityOptions { RequireIndicatorsReady = false, RejectGaps = false });
        var key = new AgentInstanceKey(AgentInstanceKey.SimulatorDeploymentId, instrument, session.StrategyId, Guid.Empty, 0);
        await using var host = new StrategyWorkerHost(session, channelCapacity: 4, key);

        DateTimeOffset openTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        MarketFrame FrameAt(long sequence) => new()
        {
            Sequence = sequence,
            AvailableAt = openTime.AddMinutes(sequence),
            ExecutionCandle = MarketCandle.FromMid(new Candle
            {
                Instrument = instrument,
                Interval = interval,
                OpenTime = openTime.AddMinutes(sequence - 1),
                CloseTime = openTime.AddMinutes(sequence),
                Prices = new Ohlc(1.1m, 1.1005m, 1.0995m, 1.1002m),
                IsComplete = true
            }),
            ClosedIntervals = new HashSet<BarInterval>(),
            Snapshots = new Dictionary<BarInterval, ChartAnnotator.Models.AnalysisSnapshot>(),
            InputStreamId = "test-stream",
            IsWarmup = false,
            IsLastCandle = false
        };

        Task<StrategyFrameResult> first = await host.EnqueueAsync(FrameAt(5));
        await first;

        // A frame at or before the last accepted sequence must never be silently admitted -
        // exactly the guarantee LiveDecisionEpochCoordinator's MarketSequence check now provides
        // on the live side for a late, concurrently-dispatched result.
        Task<StrategyFrameResult> stale = await host.EnqueueAsync(FrameAt(3));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await stale);
    }

    private sealed class AlwaysObserveTestAgent(BarInterval interval) : ITradingAgent
    {
        public string Name => "always-observe";
        public IReadOnlySet<BarInterval> RequiredIntervals { get; } = new HashSet<BarInterval> { interval };
        public BarInterval TriggerInterval => interval;
        public AgentExitManagementMode ExitManagementMode => AgentExitManagementMode.ProtectiveStopAndStrategyExit;

        public Task<AgentDecision> EvaluateAsync(AgentMarketContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentDecision
            {
                Action = AgentAction.Observe,
                Instrument = context.Instrument,
                Confidence = 0m,
                CreatedAt = context.Timestamp,
                Reason = "no setup"
            });
    }

    private static async Task<ComparativeSimulationResult> RunAsync(
        IReadOnlyList<Candle> candles,
        InstrumentKey instrument,
        DateTimeOffset from,
        DateTimeOffset to,
        StrategyExecutionMode mode)
    {
        string output = Path.Combine(Path.GetTempPath(), "th-phase2-eng", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var strategyOptions = new ProgressiveStrategyOptions { Quantity = 1_000m, MinimumRewardRisk = 1.2m };
        StrategyFactoryEntry[] strategies =
        [
            new("legacy", new LegacyProgressiveAgent(strategyOptions), instrument),
            new("improved", new ImprovedProgressiveAgent(strategyOptions), instrument)
        ];
        var engine = new StreamingComparativeEngine(new EnumerablePagedCandleSource(candles), strategies);
        var runtime = new BacktestRuntimeOptions
        {
            BaseInterval = BarInterval.Minutes(1),
            AnalysisIntervals = [BarInterval.Minutes(5), BarInterval.Minutes(15), BarInterval.Hours(1)],
            WarmupDays = 0,
            PrefetchCapacity = 8_000,
            PrefetchLowWatermark = 1_000,
            SourcePageSize = 500,
            StrategyExecutionMode = mode,
            StrategyChannelCapacity = 4,
            ProgressPublishIntervalMilliseconds = 200,
            ReplayChunkSize = 500
        };
        return await engine.RunAsync(new StreamingComparativeEngineOptions
        {
            SimulationId = Guid.NewGuid(),
            Instrument = instrument,
            EvaluationFrom = from,
            EvaluationTo = to,
            StreamFrom = from,
            AnalysisIntervals = runtime.AnalysisIntervals,
            Runtime = runtime,
            SimulationOptions = new SimulationOptions
            {
                BaseCurrency = "USD",
                StartingBalance = 100_000m,
                CloseOpenPositionsAtEnd = true,
                BaseCandleGapPolicy = ChartAnnotator.MarketData.BaseCandleGapPolicy.ResetIncompleteBuckets
            },
            OutputDirectory = output,
            InputStreamId = "phase2-test",
            AnnotationOptions = new ChartAnnotationOptions
            {
                AtrPeriod = 5,
                RsiPeriod = 5,
                BollingerPeriod = 5,
                HeavyAnalysisEveryCandles = 12
            }
        }, new HistoricalCandleRequest(instrument, BarInterval.Minutes(1), from, to));
    }

    private static Candle[] BuildCandles(InstrumentKey instrument, BarInterval interval, DateTimeOffset start, int count)
    {
        var candles = new Candle[count];
        decimal price = 1.1m;
        for (int i = 0; i < count; i++)
        {
            decimal open = price;
            decimal close = price + (decimal)Math.Sin(i / 11.0) * 0.001m + 0.00001m;
            DateTimeOffset openTime = start.AddMinutes(i);
            candles[i] = new Candle
            {
                Instrument = instrument,
                Interval = interval,
                OpenTime = openTime,
                CloseTime = openTime.AddMinutes(1),
                Prices = new Ohlc(open, Math.Max(open, close) + 0.0003m, Math.Min(open, close) - 0.0003m, close),
                IsComplete = true
            };
            price = close;
        }
        return candles;
    }

    private sealed class GatedPagedSource(Task release) : IPagedHistoricalCandleSource
    {
        public int PagesRequested { get; private set; }

        public async ValueTask<HistoricalCandlePage> ReadPageAsync(
            HistoricalPageRequest request,
            CancellationToken cancellationToken = default)
        {
            PagesRequested++;
            if (PagesRequested > 1)
                await release.WaitAsync(cancellationToken).ConfigureAwait(false);

            var candles = new List<MarketCandle>();
            DateTimeOffset cursor = request.Cursor;
            for (int i = 0; i < Math.Min(request.PageSize, 20) && cursor < request.To; i++)
            {
                candles.Add(MarketCandle.FromMid(new Candle
                {
                    Instrument = request.Instrument,
                    Interval = request.BaseInterval,
                    OpenTime = cursor,
                    CloseTime = request.BaseInterval.AddTo(cursor),
                    Prices = new Ohlc(1m, 1.1m, 0.9m, 1m),
                    IsComplete = true
                }));
                cursor = request.BaseInterval.AddTo(cursor);
            }

            return new HistoricalCandlePage
            {
                Candles = candles,
                NextCursor = cursor,
                IsComplete = cursor >= request.To,
                PageNumber = request.PageNumber
            };
        }
    }
}
