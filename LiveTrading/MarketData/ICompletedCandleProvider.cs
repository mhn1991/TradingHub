using Brokers.Models;

namespace LiveTrading.MarketData;

public interface ICompletedCandleProvider
{
    Task<IReadOnlyList<Candle>> GetCompletedCandlesAsync(
        InstrumentKey instrument,
        BarInterval interval,
        DateTimeOffset fromExclusive,
        DateTimeOffset toInclusive,
        CancellationToken cancellationToken);
}
