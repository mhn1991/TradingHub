using Brokers.Abstractions;
using Brokers.Models;
using LiveTrading.MarketData;

namespace LiveTrading.Oanda;

/// <summary>
/// Held against the read-only <see cref="IBrokerClient"/> surface deliberately - order
/// placement/cancellation is not even visible on this type (see <c>Brokers.Abstractions.
/// ITradingBrokerClient</c> for where those live).
/// </summary>
public sealed class OandaCompletedCandleProvider(IBrokerClient broker) : ICompletedCandleProvider
{
    public async Task<IReadOnlyList<Candle>> GetCompletedCandlesAsync(
        InstrumentKey instrument,
        BarInterval interval,
        DateTimeOffset fromExclusive,
        DateTimeOffset toInclusive,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<Candle> candles = await broker.MarketData.GetCandlesAsync(
            new CandleQuery(instrument, interval, Limit: 500, From: fromExclusive, To: toInclusive),
            cancellationToken).ConfigureAwait(false);

        // GetCandlesAsync sorts ascending internally but does NOT filter completeness - OANDA can
        // return a still-forming trailing candle. This filter is a hard, load-bearing invariant,
        // not an optimization: nothing downstream may ever see an incomplete candle.
        return candles.Where(candle => candle.IsComplete).ToList();
    }
}
