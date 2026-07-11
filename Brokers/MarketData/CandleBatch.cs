using System.Collections.ObjectModel;

namespace Brokers;

public sealed class CandleBatch
{
    public CandleBatch(
        string brokerId,
        BrokerProvider provider,
        string instrumentId,
        string providerSymbol,
        CandleTimeframe timeframe,
        CandlePriceBasis priceBasis,
        IEnumerable<Candle> candles,
        string? requestId = null,
        TimeSpan networkElapsed = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(instrumentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerSymbol);
        ArgumentNullException.ThrowIfNull(candles);

        Candle[] candleArray = candles.ToArray();
        for (int index = 1; index < candleArray.Length; index++)
        {
            if (candleArray[index - 1].OpenTime >= candleArray[index].OpenTime)
            {
                throw new ArgumentException("Candles must be strictly ordered by open time.", nameof(candles));
            }
        }

        BrokerId = brokerId;
        Provider = provider;
        InstrumentId = instrumentId;
        ProviderSymbol = providerSymbol;
        Timeframe = timeframe;
        PriceBasis = priceBasis;
        Candles = new ReadOnlyCollection<Candle>(candleArray);
        RequestId = requestId;
        NetworkElapsed = networkElapsed;
    }

    public string BrokerId { get; }

    public BrokerProvider Provider { get; }

    public string InstrumentId { get; }

    public string ProviderSymbol { get; }

    public CandleTimeframe Timeframe { get; }

    public CandlePriceBasis PriceBasis { get; }

    public IReadOnlyList<Candle> Candles { get; }

    public string? RequestId { get; }

    public TimeSpan NetworkElapsed { get; }
}
