namespace Brokers.Models;

public enum OrderSide
{
    Buy,
    Sell,
    Unknown
}

public enum OrderStatus
{
    Unknown,
    Pending,
    Open,
    PartiallyFilled,
    Filled,
    Cancelled,
    Replaced,
    Rejected,
    Expired
}

public sealed record BrokerOrder
{
    public required string BrokerOrderId { get; init; }
    public string? ClientOrderId { get; init; }
    public string? StrategyId { get; init; }
    public string? DecisionId { get; init; }
    public string? SetupId { get; init; }
    public string? PortfolioReservationId { get; init; }
    public string? RiskClusterId { get; init; }
    public bool ReduceOnly { get; init; }
    public required InstrumentKey Instrument { get; init; }
    public string? NativeInstrument { get; init; }
    public required OrderSide Side { get; init; }
    public required string Type { get; init; }
    public required string Status { get; init; }
    public OrderStatus NormalizedStatus { get; init; }
    public decimal? Quantity { get; init; }
    public decimal? FilledQuantity { get; init; }
    public decimal? Price { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
}
