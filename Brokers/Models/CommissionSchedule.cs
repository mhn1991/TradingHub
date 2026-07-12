namespace Brokers.Models;

public enum CommissionRateUnit
{
    Fraction,
    Percent,
    BasisPoints,
    MonetaryAmount
}

public sealed record CommissionSchedule
{
    public required InstrumentKey Instrument { get; init; }
    public decimal? Maker { get; init; }
    public decimal? Taker { get; init; }
    public decimal? Buyer { get; init; }
    public decimal? Seller { get; init; }
    public CommissionRateUnit RateUnit { get; init; } = CommissionRateUnit.Fraction;
    public bool IsDiscountEnabled { get; init; }
    public string? DiscountAsset { get; init; }
    public decimal? Discount { get; init; }
    public string Source { get; init; } = "Unknown";
}
