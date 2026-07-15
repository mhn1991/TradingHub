namespace Brokers.Models;

public sealed record BrokerPosition
{
    public required string PositionId { get; init; }
    public string? StrategyId { get; init; }
    public string? DecisionId { get; init; }
    public string? SetupId { get; init; }
    public string? PortfolioReservationId { get; init; }
    public string? RiskClusterId { get; init; }
    public required InstrumentKey Instrument { get; init; }
    public string? NativeInstrument { get; init; }
    public required OrderSide Side { get; init; }
    public required decimal Quantity { get; init; }
    public decimal? AveragePrice { get; init; }
    public decimal? UnrealizedProfitLoss { get; init; }
    public string? Currency { get; init; }
}
