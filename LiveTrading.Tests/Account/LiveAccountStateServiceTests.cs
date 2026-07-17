using Brokers.Models;
using LiveTrading.Account;
using LiveTrading.MarketData;
using LiveTrading.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace LiveTrading.Tests.Account;

[TestFixture]
public sealed class LiveAccountStateServiceTests
{
    [Test]
    public void TryGetQuote_ComputesStalenessFromReceiveTime()
    {
        DateTimeOffset now = new(2026, 7, 17, 10, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        var service = new LiveAccountStateService(
            new FakeBrokerClient(),
            clock,
            new LiveAccountStateOptions { MaximumQuoteAge = TimeSpan.FromSeconds(30) });
        var instrument = new InstrumentKey("FX:EUR/USD");
        service.ApplyQuote(new LiveQuoteSnapshot
        {
            Instrument = instrument,
            Bid = 1.1000m,
            Ask = 1.1002m,
            BrokerTime = now,
            ReceivedAt = now,
            IsTradeable = true,
            IsStale = false
        });

        Assert.That(service.TryGetQuote(instrument, out LiveQuoteSnapshot fresh), Is.True);
        Assert.That(fresh.IsStale, Is.False);

        clock.Advance(TimeSpan.FromSeconds(31));

        Assert.That(service.TryGetQuote(instrument, out LiveQuoteSnapshot stale), Is.True);
        Assert.That(stale.IsStale, Is.True);
    }

    [Test]
    public void ApplyQuote_RejectsInvalidExecutablePrices()
    {
        DateTimeOffset now = new(2026, 7, 17, 10, 0, 0, TimeSpan.Zero);
        var service = new LiveAccountStateService(
            new FakeBrokerClient(),
            new FakeTimeProvider(now),
            new LiveAccountStateOptions());

        Assert.Throws<ArgumentOutOfRangeException>(() => service.ApplyQuote(new LiveQuoteSnapshot
        {
            Instrument = new InstrumentKey("FX:EUR/USD"),
            Bid = 1.1002m,
            Ask = 1.1000m,
            BrokerTime = now,
            ReceivedAt = now,
            IsTradeable = true,
            IsStale = false
        }));
    }
}
