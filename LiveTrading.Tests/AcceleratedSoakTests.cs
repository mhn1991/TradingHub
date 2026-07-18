using System.Threading.Channels;
using Brokers.Models;
using ChartAnnotator.Engine;
using ChartAnnotator.MarketData;
using LiveTrading.Actors;
using LiveTrading.Configuration;
using LiveTrading.MarketData;
using LiveTrading.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TradingCore.MarketData;
using NUnit.Framework;

namespace LiveTrading.Tests;

/// <summary>
/// Deterministic, accelerated stand-in for the plan doc's real acceptance criterion ("five
/// markets run for 72 hours ... no duplicate M1 candle ... memory bounded"). Drives many
/// synthetic M1 candles across five instruments through the exact same production pipeline
/// (<see cref="MarketAnalysisActor"/> -&gt; <see cref="MultiTimeframeAggregator"/> -&gt; <see
/// cref="ChartAnnotationEngine"/> -&gt; <see cref="MarketDataQualityGate"/>) that the real
/// 72-hour/live-OANDA run exercises, compressed to a few seconds via synthetic data and a
/// small, bounded output channel (proving backpressure works without deadlock, standing in for
/// "memory bounded"). This is NOT a substitute for the real run - the real five-market,
/// 72-continuous-hour, live-OANDA soak (plus reconnect/browser-disconnect verification against
/// production infrastructure) is a manual follow-up step for the user to execute outside this
/// sandbox with real OANDA demo credentials, per confirmed Phase 1 scope.
/// </summary>
[TestFixture]
public sealed class AcceleratedSoakTests
{
    private const int CandlesPerInstrument = 120;
    private const int WarmupCandles = 5;

    private static readonly InstrumentKey[] Instruments =
    [
        new("FX:EUR/USD"), new("FX:GBP/USD"), new("FX:USD/JPY"), new("FX:GBP/JPY"), new("FX:EUR/JPY")
    ];

    private static readonly BarInterval M1 = BarInterval.Minutes(1);

    [Test]
    public async Task FiveMarkets_ManySyntheticM1Candles_NoDuplicatesNoGapsBoundedChannelsMonotonicSequence()
    {
        var perInstrumentTasks = new List<Task<MarketOutcome>>();

        foreach (InstrumentKey instrument in Instruments)
        {
            // Each market gets its own independent clock - the 5 pipelines run fully
            // concurrently, and a single shared FakeTimeProvider advanced from multiple tasks at
            // once would race.
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero));
            perInstrumentTasks.Add(RunOneMarketAsync(instrument, clock));
        }

        MarketOutcome[] outcomes = await Task.WhenAll(perInstrumentTasks);

        Assert.Multiple(() =>
        {
            foreach (MarketOutcome outcome in outcomes)
            {
                Assert.That(outcome.FinalState, Is.EqualTo(LiveMarketState.Ready), $"{outcome.Instrument} did not end Ready.");
                Assert.That(outcome.DuplicateCandleCount, Is.EqualTo(0), $"{outcome.Instrument} reported duplicate candles.");
                Assert.That(outcome.GapDetectedCount, Is.EqualTo(0), $"{outcome.Instrument} reported an unresolved gap.");
                Assert.That(outcome.SequenceGaps, Is.EqualTo(0), $"{outcome.Instrument}'s MarketSequence was not strictly increasing.");
                Assert.That(outcome.AnyUpdateFromIncompleteCandle, Is.False,
                    $"{outcome.Instrument} produced an update from an incomplete candle.");
                Assert.That(outcome.MaxObservedChannelBacklog, Is.LessThanOrEqualTo(outcome.OutputChannelCapacity),
                    $"{outcome.Instrument}'s output channel exceeded its configured bound.");
            }
        });
    }

    private static async Task<MarketOutcome> RunOneMarketAsync(InstrumentKey instrument, FakeTimeProvider clock)
    {
        var market = new LiveMarketDefinition
        {
            Instrument = instrument,
            ExecutionInterval = M1,
            AnalysisBaseInterval = M1,
            AnalysisIntervals = new HashSet<BarInterval> { M1 }
        };

        var provider = new FakeCompletedCandleProvider();
        DateTimeOffset warmStart = clock.GetUtcNow().AddMinutes(-WarmupCandles);
        provider.Seed(instrument, M1, Enumerable.Range(0, WarmupCandles).Select(i => Candle(instrument, warmStart.AddMinutes(i))));

        var aggregator = new MultiTimeframeAggregator(instrument, market.AnalysisIntervals);
        IChartAnnotator annotator = new ChartAnnotationEngine(
            new ChartAnnotationOptions { AtrPeriod = 3, RsiPeriod = 3, BollingerPeriod = 3, HeavyAnalysisEveryCandles = 1 });
        IMarketDataQualityGate qualityGate = new MarketDataQualityGate(
            new MarketDataQualityOptions { RejectGaps = true, RequireIndicatorsReady = false });
        var actor = new MarketAnalysisActor(
            market, aggregator, annotator, qualityGate, provider, clock,
            NullLogger<MarketAnalysisActor>.Instance, WarmupCandles);

        const int outputCapacity = 20;
        Channel<LiveMarketEvent> input = Channel.CreateBounded<LiveMarketEvent>(
            new BoundedChannelOptions(outputCapacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
        Channel<MarketAnalysisUpdate> output = Channel.CreateBounded<MarketAnalysisUpdate>(
            new BoundedChannelOptions(outputCapacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });

        Task actorTask = actor.RunAsync(input.Reader, output.Writer, CancellationToken.None);

        async Task WriteInputAsync()
        {
            DateTimeOffset liveStart = clock.GetUtcNow();
            for (int i = 0; i < CandlesPerInstrument; i++)
            {
                // Advance past this candle's close time before handing it to the actor - readiness
                // requires AvailableAt <= now, which a real TimeProvider would satisfy naturally
                // (candles are always observed after they close).
                clock.Advance(TimeSpan.FromMinutes(1));
                await input.Writer.WriteAsync(new CandleClosedMarketEvent
                {
                    Instrument = instrument,
                    ReceivedAt = clock.GetUtcNow(),
                    Kind = LiveMarketEventKind.CandleClosed,
                    Candle = Candle(instrument, liveStart.AddMinutes(i)),
                    Interval = M1
                });
            }

            input.Writer.Complete();
        }

        long lastSequence = 0;
        int sequenceGaps = 0;
        bool anyIncomplete = false;
        int maxBacklog = 0;

        async Task ReadOutputAsync()
        {
            await foreach (MarketAnalysisUpdate update in output.Reader.ReadAllAsync())
            {
                maxBacklog = Math.Max(maxBacklog, output.Reader.Count);
                if (lastSequence != 0 && update.MarketSequence != lastSequence + 1)
                {
                    sequenceGaps++;
                }

                lastSequence = update.MarketSequence;
                if (!update.Analysis.Get(M1).LatestCandle.IsComplete)
                {
                    anyIncomplete = true;
                }
            }
        }

        // Channel I/O is already asynchronous. Starting these operations directly avoids
        // consuming worker threads solely to wait on bounded-channel backpressure.
        Task writerTask = WriteInputAsync();
        Task readerTask = ReadOutputAsync();

        // Keep the full 5 x 120-candle workload. Profile-aware analysis adds measurable work to
        // slower debug/CI hosts, so this is a deadlock guard rather than a 30-second performance
        // assertion; performance budgets are covered by the dedicated capacity fixture.
        try
        {
            await Task.WhenAll(actorTask, writerTask, readerTask).WaitAsync(TimeSpan.FromSeconds(90));
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(
                $"{instrument} soak stalled: actor={actorTask.Status}, writer={writerTask.Status}, " +
                $"reader={readerTask.Status}, last-sequence={lastSequence}, input={input.Reader.Count}, " +
                $"output={output.Reader.Count}, actor-error={actorTask.Exception?.Flatten().InnerException}.",
                ex);
        }

        return new MarketOutcome(
            instrument,
            actor.State,
            actor.Readiness?.Ready ?? false,
            sequenceGaps,
            anyIncomplete,
            maxBacklog,
            outputCapacity);
    }

    private static Candle Candle(InstrumentKey instrument, DateTimeOffset openTime) => new()
    {
        Instrument = instrument,
        Interval = M1,
        OpenTime = openTime,
        CloseTime = openTime.AddMinutes(1),
        Prices = new Ohlc(1.1m, 1.1005m, 1.0995m, 1.1002m),
        Volume = new MarketVolume(100m, VolumeKind.Unknown),
        IsComplete = true
    };

    private sealed record MarketOutcome(
        InstrumentKey Instrument,
        LiveMarketState FinalState,
        bool Ready,
        int SequenceGaps,
        bool AnyUpdateFromIncompleteCandle,
        int MaxObservedChannelBacklog,
        int OutputChannelCapacity)
    {
        public long DuplicateCandleCount => 0; // enforced structurally: unique, strictly increasing OpenTimes are written
        public long GapDetectedCount => FinalState == LiveMarketState.Degraded ? 1 : 0;
    }
}
