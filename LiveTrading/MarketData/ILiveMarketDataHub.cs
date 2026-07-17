using System.Threading.Channels;
using Brokers.Models;

namespace LiveTrading.MarketData;

public interface ILiveMarketDataHub
{
    ChannelReader<LiveMarketEvent> Events { get; }

    bool TryGetLatestQuote(InstrumentKey instrument, out LiveQuoteSnapshot quote);

    MarketDataHealthSnapshot Health { get; }
}
