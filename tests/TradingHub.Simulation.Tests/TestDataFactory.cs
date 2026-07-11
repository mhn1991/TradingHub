using TradingHub.Domain.Markets;

namespace TradingHub.Simulation.Tests;

internal static class TestDataFactory
{
    public static InstrumentDefinition CreateInstrument()
    {
        return new InstrumentDefinition
        {
            Id = "CRYPTO:BTC-USDT:SPOT",
            DisplaySymbol = "BTC/USDT",
            BaseAsset = "BTC",
            QuoteAsset = "USDT",
            AssetClass = AssetClass.CryptoSpot,
            PriceIncrement = 0.01m,
            QuantityIncrement = 0.001m,
            MinimumQuantity = 0.001m,
            MaximumQuantity = 100m
        };
    }

    public static PriceBar CreateBar(DateTimeOffset openTime, decimal bidOpen, decimal bidClose)
    {
        var bidHigh = Math.Max(bidOpen, bidClose) + 1m;
        var bidLow = Math.Min(bidOpen, bidClose) - 1m;
        return new PriceBar
        {
            InstrumentId = "CRYPTO:BTC-USDT:SPOT",
            OpenTime = openTime,
            Period = TimeSpan.FromMinutes(1),
            Bid = new Ohlc(bidOpen, bidHigh, bidLow, bidClose),
            Ask = new Ohlc(bidOpen + 0.10m, bidHigh + 0.10m, bidLow + 0.10m, bidClose + 0.10m),
            Volume = 100m
        };
    }
}
