namespace Brokers;

/// <summary>
/// Canonical OHLC candle. Provider-specific volume meanings are kept in separate nullable fields.
/// </summary>
public sealed record Candle
{
    public Candle(
        string instrumentId,
        CandleTimeframe timeframe,
        CandlePriceBasis priceBasis,
        DateTimeOffset openTime,
        DateTimeOffset closeTime,
        decimal open,
        decimal high,
        decimal low,
        decimal close,
        bool isComplete,
        decimal? baseAssetVolume = null,
        decimal? quoteAssetVolume = null,
        long? tickCount = null,
        long? tradeCount = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instrumentId);

        if (!timeframe.IsValid)
        {
            throw new ArgumentException("The timeframe is not initialized.", nameof(timeframe));
        }

        if (openTime >= closeTime)
        {
            throw new ArgumentException("The candle open time must precede its close time.", nameof(openTime));
        }

        if (high < open || high < close || high < low)
        {
            throw new ArgumentException("The high value is inconsistent with the candle prices.", nameof(high));
        }

        if (low > open || low > close)
        {
            throw new ArgumentException("The low value is inconsistent with the candle prices.", nameof(low));
        }

        if (baseAssetVolume < 0 || quoteAssetVolume < 0 || tickCount < 0 || tradeCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(baseAssetVolume), "Candle volume/count values cannot be negative.");
        }

        InstrumentId = instrumentId;
        Timeframe = timeframe;
        PriceBasis = priceBasis;
        OpenTime = openTime.ToUniversalTime();
        CloseTime = closeTime.ToUniversalTime();
        Open = open;
        High = high;
        Low = low;
        Close = close;
        IsComplete = isComplete;
        BaseAssetVolume = baseAssetVolume;
        QuoteAssetVolume = quoteAssetVolume;
        TickCount = tickCount;
        TradeCount = tradeCount;
    }

    public string InstrumentId { get; }

    public CandleTimeframe Timeframe { get; }

    public CandlePriceBasis PriceBasis { get; }

    public DateTimeOffset OpenTime { get; }

    /// <summary>
    /// Exclusive UTC end of the candle interval.
    /// </summary>
    public DateTimeOffset CloseTime { get; }

    public decimal Open { get; }

    public decimal High { get; }

    public decimal Low { get; }

    public decimal Close { get; }

    public bool IsComplete { get; }

    /// <summary>Binance base-asset volume; absent when the provider does not expose it.</summary>
    public decimal? BaseAssetVolume { get; }

    /// <summary>Binance quote-asset volume; absent when the provider does not expose it.</summary>
    public decimal? QuoteAssetVolume { get; }

    /// <summary>OANDA price/tick count; not interchangeable with traded asset volume.</summary>
    public long? TickCount { get; }

    public long? TradeCount { get; }
}
