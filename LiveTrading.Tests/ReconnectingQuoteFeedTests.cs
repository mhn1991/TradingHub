using System.Threading.Channels;
using Brokers.Models;
using LiveTrading.Feed;
using LiveTrading.MarketData;
using LiveTrading.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace LiveTrading.Tests;

[TestFixture]
public sealed class ReconnectingQuoteFeedTests
{
    private static readonly InstrumentKey Eur = new("FX:EUR/USD");
    private static readonly InstrumentKey Gbp = new("FX:GBP/USD");

    private static LiveQuoteEvent Tick(InstrumentKey instrument, decimal bid = 1.1000m) => new(
        instrument,
        new LiveQuoteSnapshot
        {
            Instrument = instrument,
            Bid = bid,
            Ask = bid + 0.0002m,
            BrokerTime = DateTimeOffset.UtcNow,
            ReceivedAt = DateTimeOffset.UtcNow,
            IsTradeable = true,
            IsStale = false
        });

    private static (Channel<LiveMarketEvent> Channel, IReadOnlyDictionary<InstrumentKey, ChannelWriter<LiveMarketEvent>> Writers)
        SingleMarket(InstrumentKey instrument)
    {
        var channel = Channel.CreateBounded<LiveMarketEvent>(
            new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
        return (channel, new Dictionary<InstrumentKey, ChannelWriter<LiveMarketEvent>> { [instrument] = channel.Writer });
    }

    [Test]
    public async Task RunAsync_DispatchesTicksToTheirOwnInstrumentChannelOnly()
    {
        var stream = new FakeLiveQuoteStream();
        stream.ConnectionScripts.Add(() => [Tick(Eur), Tick(Gbp)]);

        var eurChannel = Channel.CreateBounded<LiveMarketEvent>(
            new BoundedChannelOptions(10) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
        var gbpChannel = Channel.CreateBounded<LiveMarketEvent>(
            new BoundedChannelOptions(10) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
        var writers = new Dictionary<InstrumentKey, ChannelWriter<LiveMarketEvent>>
        {
            [Eur] = eurChannel.Writer,
            [Gbp] = gbpChannel.Writer
        };

        var clock = new FakeTimeProvider();
        var feed = new ReconnectingQuoteFeed(stream, clock, new ReconnectingQuoteFeedOptions(), NullLogger<ReconnectingQuoteFeed>.Instance);
        using var cts = new CancellationTokenSource();
        Task runTask = feed.RunAsync([Eur, Gbp], writers, cts.Token);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        LiveMarketEvent eurEvent = await eurChannel.Reader.ReadAsync(timeout.Token);
        LiveMarketEvent gbpEvent = await gbpChannel.Reader.ReadAsync(timeout.Token);

        Assert.Multiple(() =>
        {
            Assert.That(eurEvent, Is.InstanceOf<LiveQuoteMarketEvent>());
            Assert.That(((LiveQuoteMarketEvent)eurEvent).Quote.Instrument, Is.EqualTo(Eur));
            Assert.That(gbpEvent, Is.InstanceOf<LiveQuoteMarketEvent>());
            Assert.That(((LiveQuoteMarketEvent)gbpEvent).Quote.Instrument, Is.EqualTo(Gbp));
        });

        await cts.CancelAsync();
        await SafeAwaitAsync(runTask);
    }

    [Test]
    public async Task RunAsync_ConnectionClosesThenReconnects_EmitsFaultThenRecovered()
    {
        var stream = new FakeLiveQuoteStream();
        stream.ConnectionScripts.Add(() => [Tick(Eur)]); // first attempt: one tick, then closes
        stream.ConnectionScripts.Add(() => [Tick(Eur)]); // second attempt (after reconnect): one tick

        (Channel<LiveMarketEvent> channel, var writers) = SingleMarket(Eur);
        var clock = new FakeTimeProvider();
        var options = new ReconnectingQuoteFeedOptions { InitialBackoff = TimeSpan.FromSeconds(1) };
        var feed = new ReconnectingQuoteFeed(stream, clock, options, NullLogger<ReconnectingQuoteFeed>.Instance);
        using var cts = new CancellationTokenSource();
        Task runTask = feed.RunAsync([Eur], writers, cts.Token);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        LiveMarketEvent firstTick = await channel.Reader.ReadAsync(timeout.Token);
        Assert.That(firstTick, Is.InstanceOf<LiveQuoteMarketEvent>());

        LiveMarketEvent fault = await channel.Reader.ReadAsync(timeout.Token);
        Assert.That(fault, Is.InstanceOf<StreamFaultMarketEvent>());

        clock.Advance(TimeSpan.FromSeconds(2)); // past InitialBackoff, triggers the reconnect

        LiveMarketEvent recovered = await channel.Reader.ReadAsync(timeout.Token);
        Assert.That(recovered, Is.InstanceOf<StreamRecoveredMarketEvent>());

        LiveMarketEvent secondTick = await channel.Reader.ReadAsync(timeout.Token);
        Assert.That(secondTick, Is.InstanceOf<LiveQuoteMarketEvent>());

        Assert.That(stream.ConnectionAttempts, Is.EqualTo(2));

        await cts.CancelAsync();
        await SafeAwaitAsync(runTask);
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
