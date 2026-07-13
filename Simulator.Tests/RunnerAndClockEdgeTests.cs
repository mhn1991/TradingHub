using Brokers.Models;
using ChartAnnotator.MarketData;
using Simulator.Abstractions;
using Simulator.Engine;
using Simulator.Time;

namespace Simulator.Tests;

[TestFixture]
public sealed class RunnerAndClockEdgeTests
{
    private static readonly InstrumentKey Instrument = new("FX:GBP/USD");
    private static readonly BarInterval Five = BarInterval.Minutes(5);
    private static readonly DateTimeOffset Start = SimulationTestHarness.DefaultStart;

    [Test]
    public void HistoricalClock_RejectsBackwardMovementAndNormalizesUtc()
    {
        var clock = new HistoricalSimulationClock();
        var offsetTime = new DateTimeOffset(2026, 1, 1, 2, 0, 0, TimeSpan.FromHours(2));
        clock.AdvanceTo(offsetTime);

        Assert.That(clock.UtcNow.Offset, Is.EqualTo(TimeSpan.Zero));
        Assert.That(
            () => clock.AdvanceTo(clock.UtcNow.AddTicks(-1)),
            Throws.InvalidOperationException.With.Message.Contains("backwards"));
    }

    [Test]
    public void HistoricalClock_AllowsSameTimestamp()
    {
        var clock = new HistoricalSimulationClock();
        clock.AdvanceTo(Start);

        Assert.That(() => clock.AdvanceTo(Start), Throws.Nothing);
    }

    [Test]
    public async Task EnumerableCandleSource_SortsInputChronologically()
    {
        Candle later = CandleAt(1);
        Candle earlier = CandleAt(0);
        var source = new EnumerableCandleSource([later, earlier]);
        var result = new List<Candle>();

        await foreach (Candle candle in source.ReadAsync())
        {
            result.Add(candle);
        }

        Assert.That(result.Select(candle => candle.OpenTime),
            Is.EqualTo(new[] { earlier.OpenTime, later.OpenTime }));
    }

    [Test]
    public void EnumerableCandleSource_RejectsNullInput()
    {
        Assert.That(
            () => new EnumerableCandleSource(null!),
            Throws.TypeOf<ArgumentNullException>());
    }

    [Test]
    public async Task EnumerableCandleSource_ObservesCancellation()
    {
        var source = new EnumerableCandleSource([CandleAt(0)]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.That(
            async () =>
            {
                await foreach (Candle _ in source.ReadAsync(cancellation.Token))
                {
                }
            },
            Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task EmptyHistoricalSource_IsRejected()
    {
        var agent = new RecordingTradingAgent([Five], Five);
        await using SimulationSession session = SimulationFactory.CreateHistorical(
            Instrument,
            [],
            [Five],
            agent);

        Assert.That(
            async () => await session.Runner.RunAsync(),
            Throws.InvalidOperationException.With.Message.Contains("did not provide any candles"));
    }

    [Test]
    public void MissingAgentInterval_IsRejectedAtConstruction()
    {
        BarInterval fifteen = BarInterval.Minutes(15);
        var agent = new RecordingTradingAgent([Five, fifteen], Five);

        Assert.That(
            () => SimulationFactory.CreateHistorical(
                Instrument,
                [CandleAt(0)],
                [Five],
                agent),
            Throws.ArgumentException.With.Message.Contains("required interval"));
    }

    [Test]
    public async Task CancelledRun_StopsBeforeProcessingFirstCandle()
    {
        var agent = new RecordingTradingAgent([Five], Five);
        await using SimulationSession session = SimulationFactory.CreateHistorical(
            Instrument,
            [CandleAt(0)],
            [Five],
            agent);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.That(
            async () => await session.Runner.RunAsync(cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(agent.Contexts, Is.Empty);
    }

    [Test]
    public async Task IncompleteExecutionCandle_IsRejected()
    {
        var agent = new RecordingTradingAgent([Five], Five);
        Candle incomplete = CandleAt(0) with { IsComplete = false };
        await using SimulationSession session = SimulationFactory.CreateHistorical(
            Instrument,
            [incomplete],
            [Five],
            agent);

        Assert.That(
            async () => await session.Runner.RunAsync(),
            Throws.ArgumentException.With.Message.Contains("complete"));
    }

    [Test]
    public async Task DuplicateCandleTimes_AreRejected()
    {
        var agent = new RecordingTradingAgent([Five], Five);
        await using SimulationSession session = SimulationFactory.CreateHistorical(
            Instrument,
            [CandleAt(0), CandleAt(0)],
            [Five],
            agent);

        Assert.That(
            async () => await session.Runner.RunAsync(),
            Throws.InvalidOperationException.With.Message.Contains("discontinuous"));
    }

    [Test]
    public async Task ResultUsesFirstOpenAndLastCloseTimes()
    {
        var agent = new RecordingTradingAgent([Five], Five);
        await using SimulationSession session = SimulationFactory.CreateHistorical(
            Instrument,
            [CandleAt(0), CandleAt(1), CandleAt(2)],
            [Five],
            agent);

        var result = await session.Runner.RunAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.StartedAt, Is.EqualTo(Start));
            Assert.That(result.EndedAt, Is.EqualTo(Start.AddMinutes(15)));
            Assert.That(agent.Contexts, Has.Count.EqualTo(3));
        });
    }

    [Test]
    public void AggregatorRejectsUnknownIntervalLookup()
    {
        var aggregator = new MultiTimeframeAggregator(Instrument, [Five]);

        Assert.That(
            () => aggregator.GetCandles(BarInterval.Minutes(15)),
            Throws.TypeOf<KeyNotFoundException>());
    }

    private static Candle CandleAt(int index) => TestCandles.Create(
        Instrument,
        Start.AddMinutes(index * 5),
        Five,
        100m + index,
        101m + index,
        99m + index,
        100m + index);
}
