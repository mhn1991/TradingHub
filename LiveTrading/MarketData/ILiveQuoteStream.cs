using Brokers.Models;

namespace LiveTrading.MarketData;

public sealed record LiveQuoteEvent(InstrumentKey Instrument, LiveQuoteSnapshot Quote);

public interface ILiveQuoteStream
{
    IAsyncEnumerable<LiveQuoteEvent> ReadAsync(
        IReadOnlyList<InstrumentKey> instruments,
        CancellationToken cancellationToken);
}
