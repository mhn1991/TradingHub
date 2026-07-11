namespace Brokers;

/// <summary>
/// Identifies the price series used to build OHLC values.
/// </summary>
public enum CandlePriceBasis
{
    ProviderDefault = 0,
    Trades = 1,
    Midpoint = 2,
    Bid = 3,
    Ask = 4
}
