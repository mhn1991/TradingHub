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

[TestFixture]
public sealed class MarketAnalysisActorTests
{
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");
    private static readonly BarInterval M1 = BarInterval.Minutes(1);
    private const int WarmupCandles = 10;

    private static Candle Candle(DateTimeOffset openTime, decimal close = 1.1002m) => new()
    {
        Instrument = Instrument,
        Interval = M1,
        OpenTime = openTime,
        CloseTime = openTime.AddMinutes(1),
        Prices = new Ohlc(1.1m, 1.1005m, 1.0995m, close),
        Volume = new MarketVolume(100m, VolumeKind.Unknown),
        IsComplete = true
    };

    private static LiveMarketDefinition Market() => new()
    {
        Instrument = Instrument,
        ExecutionInterval = M1,
        AnalysisBaseInterval = M1,
        AnalysisIntervals = new HashSet<BarInterval> { M1 }
    };

    private static MarketAnalysisActor BuildActor(
        FakeCompletedCandleProvider provider, FakeTimeProvider clock, out MultiTimeframeAggregator aggregator)
    {
        LiveMarketDefinition market = Market();
        aggregator = new MultiTimeframeAggregator(Instrument, market.AnalysisIntervals);
        IChartAnnotator annotator = new ChartAnnotationEngine(
            new ChartAnnotationOptions { AtrPeriod = 3, RsiPeriod = 3, BollingerPeriod = 3, HeavyAnalysisEveryCandles = 1 });
        IMarketDataQualityGate qualityGate = new MarketDataQualityGate(
            new MarketDataQualityOptions { RejectGaps = true, RequireIndicatorsReady = false });
        return new MarketAnalysisActor(
            market, aggregator, annotator, qualityGate, provider, clock,
            NullLogger<MarketAnalysisActor>.Instance, WarmupCandles);
    }

    private static Channel<LiveMarketEvent> InputChannel() => Channel.CreateBounded<LiveMarketEvent>(
        new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });

    private static Channel<MarketAnalysisUpdate> OutputChannel() => Channel.CreateBounded<MarketAnalysisUpdate>(
        new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });

    [Test]
    public async Task RunAsync_WarmsUpFromHistoryAndBecomesReadyAfterEnoughSamples()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 2, 8, 0, 0, TimeSpan.Zero));
        var provider = new FakeCompletedCandleProvider();
        DateTimeOffset start = clock.GetUtcNow().AddMinutes(-WarmupCandles);
        provider.Seed(Instrument, M1, Enumerable.Range(0, WarmupCandles).Select(i => Candle(start.AddMinutes(i))));

        MarketAnalysisActor actor = BuildActor(provider, clock, out _);
        Channel<LiveMarketEvent> input = InputChannel();
        Channel<MarketAnalysisUpdate> output = OutputChannel();
        input.Writer.Complete();

        await actor.RunAsync(input.Reader, output.Writer, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(actor.State, Is.EqualTo(LiveMarketState.Ready));
            Assert.That(actor.Readiness?.Ready, Is.True);
        });
    }

    [Test]
    public async Task RunAsync_InsufficientWarmUpData_RemainsWarmingUp()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 2, 8, 0, 0, TimeSpan.Zero));
        var provider = new FakeCompletedCandleProvider();
        MarketAnalysisActor actor = BuildActor(provider, clock, out _);
        Channel<LiveMarketEvent> input = InputChannel();
        Channel<MarketAnalysisUpdate> output = OutputChannel();
        input.Writer.Complete();

        await actor.RunAsync(input.Reader, output.Writer, CancellationToken.None);

        Assert.That(actor.State, Is.EqualTo(LiveMarketState.WarmingUp));
    }

    [Test]
    public async Task RunAsync_LiveCandleAfterWarmUp_PublishesExactlyOneUpdate()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 2, 8, 0, 0, TimeSpan.Zero));
        var provider = new FakeCompletedCandleProvider();
        DateTimeOffset start = clock.GetUtcNow().AddMinutes(-WarmupCandles);
        provider.Seed(Instrument, M1, Enumerable.Range(0, WarmupCandles).Select(i => Candle(start.AddMinutes(i))));

        MarketAnalysisActor actor = BuildActor(provider, clock, out _);
        Channel<LiveMarketEvent> input = InputChannel();
        Channel<MarketAnalysisUpdate> output = OutputChannel();

        DateTimeOffset liveOpen = clock.GetUtcNow();
        await input.Writer.WriteAsync(new CandleClosedMarketEvent
        {
            Instrument = Instrument,
            ReceivedAt = clock.GetUtcNow(),
            Kind = LiveMarketEventKind.CandleClosed,
            Candle = Candle(liveOpen),
            Interval = M1
        });
        input.Writer.Complete();
        // Readiness requires each snapshot's AvailableAt (the candle's close time) to be <= now -
        // a real production TimeProvider would naturally have advanced past it by processing time.
        clock.Advance(TimeSpan.FromMinutes(1));

        await actor.RunAsync(input.Reader, output.Writer, CancellationToken.None);

        Assert.That(output.Reader.Count, Is.EqualTo(1));
        MarketAnalysisUpdate update = await output.Reader.ReadAsync();
        Assert.That(update.Health.DuplicateCandleCount, Is.EqualTo(0));
    }

    [Test]
    public async Task RunAsync_DuplicateLiveCandle_IsDroppedAndCounted()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 2, 8, 0, 0, TimeSpan.Zero));
        var provider = new FakeCompletedCandleProvider();
        DateTimeOffset start = clock.GetUtcNow().AddMinutes(-WarmupCandles);
        provider.Seed(Instrument, M1, Enumerable.Range(0, WarmupCandles).Select(i => Candle(start.AddMinutes(i))));

        MarketAnalysisActor actor = BuildActor(provider, clock, out _);
        Channel<LiveMarketEvent> input = InputChannel();
        Channel<MarketAnalysisUpdate> output = OutputChannel();

        DateTimeOffset liveOpen = clock.GetUtcNow();
        LiveMarketEvent CandleEvent(DateTimeOffset openTime) => new CandleClosedMarketEvent
        {
            Instrument = Instrument,
            ReceivedAt = clock.GetUtcNow(),
            Kind = LiveMarketEventKind.CandleClosed,
            Candle = Candle(openTime),
            Interval = M1
        };
        await input.Writer.WriteAsync(CandleEvent(liveOpen));
        await input.Writer.WriteAsync(CandleEvent(liveOpen)); // exact duplicate OpenTime - dropped silently, no update
        // A second, genuinely new candle is needed to observe the accumulated duplicate count -
        // the dropped duplicate itself produces no update to inspect.
        await input.Writer.WriteAsync(CandleEvent(liveOpen.AddMinutes(1)));
        input.Writer.Complete();
        clock.Advance(TimeSpan.FromMinutes(2)); // past both live candles' close times, see readiness note above

        await actor.RunAsync(input.Reader, output.Writer, CancellationToken.None);

        Assert.That(output.Reader.Count, Is.EqualTo(2), "The duplicate candle must not produce its own update.");
        MarketAnalysisUpdate first = await output.Reader.ReadAsync();
        MarketAnalysisUpdate second = await output.Reader.ReadAsync();
        Assert.Multiple(() =>
        {
            Assert.That(first.Health.DuplicateCandleCount, Is.EqualTo(0));
            Assert.That(second.Health.DuplicateCandleCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task RunAsync_ChronologicalGap_MarksDegradedWithoutCrashingTheActor()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 2, 8, 0, 0, TimeSpan.Zero));
        var provider = new FakeCompletedCandleProvider();
        DateTimeOffset start = clock.GetUtcNow().AddMinutes(-WarmupCandles);
        provider.Seed(Instrument, M1, Enumerable.Range(0, WarmupCandles).Select(i => Candle(start.AddMinutes(i))));

        MarketAnalysisActor actor = BuildActor(provider, clock, out _);
        Channel<LiveMarketEvent> input = InputChannel();
        Channel<MarketAnalysisUpdate> output = OutputChannel();

        // Skip ten minutes ahead - MultiTimeframeAggregator.Apply must reject this as a gap under
        // the default BaseCandleGapPolicy.Throw, which the actor catches and reports as Degraded
        // rather than letting propagate and crash the actor's RunAsync loop.
        DateTimeOffset gapOpen = clock.GetUtcNow().AddMinutes(10);
        await input.Writer.WriteAsync(new CandleClosedMarketEvent
        {
            Instrument = Instrument,
            ReceivedAt = clock.GetUtcNow(),
            Kind = LiveMarketEventKind.CandleClosed,
            Candle = Candle(gapOpen),
            Interval = M1
        });
        input.Writer.Complete();

        await actor.RunAsync(input.Reader, output.Writer, CancellationToken.None);

        Assert.That(actor.State, Is.EqualTo(LiveMarketState.Degraded));
    }

    [Test]
    public async Task RunAsync_StreamFault_MarksStale_AndRecoveryReWarmsUpToReady()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 2, 8, 0, 0, TimeSpan.Zero));
        var provider = new FakeCompletedCandleProvider();
        DateTimeOffset start = clock.GetUtcNow().AddMinutes(-WarmupCandles);
        provider.Seed(Instrument, M1, Enumerable.Range(0, WarmupCandles).Select(i => Candle(start.AddMinutes(i))));

        MarketAnalysisActor actor = BuildActor(provider, clock, out _);
        Channel<LiveMarketEvent> input = InputChannel();
        Channel<MarketAnalysisUpdate> output = OutputChannel();

        await input.Writer.WriteAsync(new StreamFaultMarketEvent
        {
            Instrument = Instrument,
            ReceivedAt = clock.GetUtcNow(),
            Kind = LiveMarketEventKind.StreamFault,
            Reason = "test"
        });
        await input.Writer.WriteAsync(new StreamRecoveredMarketEvent
        {
            Instrument = Instrument,
            ReceivedAt = clock.GetUtcNow(),
            Kind = LiveMarketEventKind.StreamRecovered
        });
        input.Writer.Complete();

        await actor.RunAsync(input.Reader, output.Writer, CancellationToken.None);

        Assert.That(actor.State, Is.EqualTo(LiveMarketState.Ready));
    }
}
