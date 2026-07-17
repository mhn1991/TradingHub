using System.Threading.Channels;
using Brokers.Models;
using LiveTrading.MarketData;
using LiveTrading.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace LiveTrading.Tests;

[TestFixture]
public sealed class CompletedCandleCoordinatorTests
{
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");
    private static readonly BarInterval M1 = BarInterval.Minutes(1);

    private static Candle Candle(DateTimeOffset openTime, bool complete = true) => new()
    {
        Instrument = Instrument,
        Interval = M1,
        OpenTime = openTime,
        CloseTime = openTime.AddMinutes(1),
        Prices = new Ohlc(1.1m, 1.1005m, 1.0995m, 1.1002m),
        Volume = new MarketVolume(100m, VolumeKind.Unknown),
        IsComplete = complete
    };

    private static (CompletedCandleCoordinator Coordinator, FakeCompletedCandleProvider Provider, FakeTimeProvider Clock)
        Build(CompletedCandleCoordinatorOptions? options = null)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 2, 8, 0, 0, TimeSpan.Zero));
        var provider = new FakeCompletedCandleProvider();
        var coordinator = new CompletedCandleCoordinator(
            Instrument, M1, provider, clock,
            options ?? new CompletedCandleCoordinatorOptions { FinalizationDelay = TimeSpan.FromSeconds(1) },
            NullLogger<CompletedCandleCoordinator>.Instance);
        return (coordinator, provider, clock);
    }

    private static Channel<Candle> Channel() => System.Threading.Channels.Channel.CreateBounded<Candle>(
        new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });

    /// <summary>
    /// FakeTimeProvider.Advance only fires timers that already exist at the moment it's called -
    /// a chain of sequential `await Task.Delay(..., clock, ...)` calls needs one Advance per link,
    /// with a real (short, wall-clock) yield between each so the previous delay's continuation
    /// actually runs and schedules the next timer before the following Advance fires it. One
    /// coordinator loop iteration awaits two delays in sequence (wait-to-boundary, then
    /// FinalizationDelay), so this drives both.
    /// </summary>
    private static async Task AdvancePastOneCoordinatorTickAsync(FakeTimeProvider clock, TimeSpan finalizationDelay)
    {
        clock.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(50);
        clock.Advance(finalizationDelay);
        await Task.Delay(50);
    }

    [Test]
    public async Task RunAsync_PublishesNewlyCompletedCandlesInOrder()
    {
        (CompletedCandleCoordinator coordinator, FakeCompletedCandleProvider provider, FakeTimeProvider clock) = Build();
        DateTimeOffset start = clock.GetUtcNow();
        provider.Seed(Instrument, M1, [Candle(start), Candle(start.AddMinutes(1))]);

        Channel<Candle> channel = Channel();
        using var cts = new CancellationTokenSource();
        Task runTask = coordinator.RunAsync(channel.Writer, start.AddMinutes(-1), cts.Token);

        await AdvancePastOneCoordinatorTickAsync(clock, TimeSpan.FromSeconds(1));

        using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Candle first = await channel.Reader.ReadAsync(readTimeout.Token);
        Candle second = await channel.Reader.ReadAsync(readTimeout.Token);

        Assert.Multiple(() =>
        {
            Assert.That(first.OpenTime, Is.EqualTo(start));
            Assert.That(second.OpenTime, Is.EqualTo(start.AddMinutes(1)));
        });

        await cts.CancelAsync();
        await SafeAwaitAsync(runTask);
    }

    [Test]
    public async Task RunAsync_MissedMinute_DoesNotAdvanceCursorAndCatchesUpOnNextPoll()
    {
        (CompletedCandleCoordinator coordinator, FakeCompletedCandleProvider provider, FakeTimeProvider clock) = Build();
        DateTimeOffset start = clock.GetUtcNow();
        // Nothing seeded yet - the first poll returns zero candles ("missed minute").

        Channel<Candle> channel = Channel();
        using var cts = new CancellationTokenSource();
        Task runTask = coordinator.RunAsync(channel.Writer, start.AddMinutes(-1), cts.Token);

        await AdvancePastOneCoordinatorTickAsync(clock, TimeSpan.FromSeconds(1)); // first (empty) poll

        // Now seed the candle the first poll missed plus the next one, and advance to the next boundary.
        provider.Seed(Instrument, M1, [Candle(start), Candle(start.AddMinutes(1))]);
        await AdvancePastOneCoordinatorTickAsync(clock, TimeSpan.FromSeconds(1));

        using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Candle first = await channel.Reader.ReadAsync(readTimeout.Token);
        Candle second = await channel.Reader.ReadAsync(readTimeout.Token);

        Assert.Multiple(() =>
        {
            Assert.That(first.OpenTime, Is.EqualTo(start), "The candle missed on the first poll must still arrive.");
            Assert.That(second.OpenTime, Is.EqualTo(start.AddMinutes(1)));
        });

        await cts.CancelAsync();
        await SafeAwaitAsync(runTask);
    }

    [Test]
    public async Task RunAsync_TransientPollFailure_LeavesCursorUnchangedAndRetriesNextBoundary()
    {
        (CompletedCandleCoordinator coordinator, FakeCompletedCandleProvider provider, FakeTimeProvider clock) = Build();
        DateTimeOffset start = clock.GetUtcNow();
        provider.EnqueueFailure(new InvalidOperationException("transient"));
        provider.Seed(Instrument, M1, [Candle(start)]);

        Channel<Candle> channel = Channel();
        using var cts = new CancellationTokenSource();
        Task runTask = coordinator.RunAsync(channel.Writer, start.AddMinutes(-1), cts.Token);

        await AdvancePastOneCoordinatorTickAsync(clock, TimeSpan.FromSeconds(1)); // this poll fails
        await AdvancePastOneCoordinatorTickAsync(clock, TimeSpan.FromSeconds(1)); // this poll succeeds, re-fetching from the same cursor

        using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Candle candle = await channel.Reader.ReadAsync(readTimeout.Token);
        Assert.That(candle.OpenTime, Is.EqualTo(start));

        await cts.CancelAsync();
        await SafeAwaitAsync(runTask);
    }

    [Test]
    public async Task RunAsync_IncompleteCandleFromProvider_ThrowsRatherThanPublishing()
    {
        (CompletedCandleCoordinator coordinator, FakeCompletedCandleProvider provider, FakeTimeProvider clock) = Build();
        DateTimeOffset start = clock.GetUtcNow();
        provider.Seed(Instrument, M1, [Candle(start, complete: false)]);

        Channel<Candle> channel = Channel();
        using var cts = new CancellationTokenSource();
        Task runTask = coordinator.RunAsync(channel.Writer, start.AddMinutes(-1), cts.Token);

        await AdvancePastOneCoordinatorTickAsync(clock, TimeSpan.FromSeconds(1));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.That(async () => await runTask.WaitAsync(timeout.Token), Throws.InstanceOf<InvalidOperationException>());
    }

    private static async Task SafeAwaitAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // expected
        }
    }
}
