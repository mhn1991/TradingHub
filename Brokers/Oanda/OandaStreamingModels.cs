namespace Brokers.Oanda;

public sealed record OandaInstrumentInfo(
    string Name,
    string DisplayName,
    string Type);

public sealed record OandaPriceTick(
    string Instrument,
    DateTimeOffset Timestamp,
    decimal Bid,
    decimal Ask,
    bool IsTradeable)
{
    public decimal Midpoint => (Bid + Ask) / 2m;
}
