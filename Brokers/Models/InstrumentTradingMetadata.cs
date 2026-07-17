namespace Brokers.Models;

/// <summary>
/// Broker-authoritative execution and margin constraints for one instrument. QuantityStep and
/// PriceIncrement are decimal increments, not decimal-place counts.
/// </summary>
public sealed record InstrumentTradingMetadata
{
    public required InstrumentKey Instrument { get; init; }
    public required decimal MinimumQuantity { get; init; }
    public decimal? MaximumOrderQuantity { get; init; }
    public required decimal QuantityStep { get; init; }
    public required decimal PriceIncrement { get; init; }
    public decimal? PipSize { get; init; }
    public decimal? MarginRate { get; init; }
    public int PricePrecision { get; init; }
    public int QuantityPrecision { get; init; }

    public void Validate()
    {
        if (Instrument.IsEmpty || MinimumQuantity <= 0m || QuantityStep <= 0m ||
            PriceIncrement <= 0m || MaximumOrderQuantity is <= 0m || PipSize is <= 0m ||
            MarginRate is <= 0m or > 1m || PricePrecision < 0 || QuantityPrecision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(InstrumentTradingMetadata));
        }
    }
}
