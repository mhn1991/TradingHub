using TradingHub.Domain.Markets;

namespace TradingHub.Simulation.MarketData;

internal static class CsvBarMapper
{
    public static PriceBar ToCanonical(CsvBarDto source, CsvHistoricalBarOptions options)
    {
        var bar = new PriceBar
        {
            InstrumentId = options.InstrumentId,
            OpenTime = source.OpenTime,
            Period = options.Period,
            Bid = new Ohlc(source.BidOpen, source.BidHigh, source.BidLow, source.BidClose),
            Ask = new Ohlc(source.AskOpen, source.AskHigh, source.AskLow, source.AskClose),
            Volume = source.Volume
        };
        bar.EnsureValid();
        return bar;
    }
}
