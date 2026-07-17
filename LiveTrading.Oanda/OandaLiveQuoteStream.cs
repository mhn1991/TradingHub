using System.Runtime.CompilerServices;
using Brokers.Models;
using Brokers.Oanda;
using LiveTrading.MarketData;

namespace LiveTrading.Oanda;

/// <summary>
/// Thin translation layer over <see cref="OandaBrokerClient.StreamPricesAsync"/> - deliberately
/// carries no retry/reconnect logic of its own (that lives in <c>LiveTrading.Feed.
/// ReconnectingQuoteFeed</c>, broker-neutral). Mirrors <c>StreamPricesAsync</c>'s own documented
/// contract: the enumeration completes (does not throw) when the broker closes the stream: the
/// caller is expected to reconnect.
/// </summary>
public sealed class OandaLiveQuoteStream(
    OandaBrokerClient broker,
    OandaInstrumentMap instrumentMap,
    TimeProvider timeProvider) : ILiveQuoteStream
{
    public async IAsyncEnumerable<LiveQuoteEvent> ReadAsync(
        IReadOnlyList<InstrumentKey> instruments,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instruments);
        string[] native = instruments.Select(instrumentMap.ToNative).ToArray();

        await foreach (OandaPriceTick tick in broker
            .StreamPricesAsync(native, cancellationToken)
            .ConfigureAwait(false))
        {
            InstrumentKey instrument = instrumentMap.FromNative(tick.Instrument);
            yield return new LiveQuoteEvent(
                instrument,
                new LiveQuoteSnapshot
                {
                    Instrument = instrument,
                    Bid = tick.Bid,
                    Ask = tick.Ask,
                    BrokerTime = tick.Timestamp,
                    ReceivedAt = timeProvider.GetUtcNow(),
                    IsTradeable = tick.IsTradeable,
                    IsStale = false
                });
        }
    }
}
