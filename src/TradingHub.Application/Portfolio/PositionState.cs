namespace TradingHub.Application.Portfolio;

public sealed class PositionState
{
    public PositionState(string instrumentId)
    {
        InstrumentId = instrumentId;
    }

    public string InstrumentId { get; }

    public decimal NetQuantity { get; internal set; }

    public decimal AveragePrice { get; internal set; }

    public decimal UnrealizedPnl { get; internal set; }
}

public sealed record EquityPoint
{
    public required DateTimeOffset Timestamp { get; init; }

    public required decimal Balance { get; init; }

    public required decimal UnrealizedPnl { get; init; }

    public required decimal Equity { get; init; }
}
