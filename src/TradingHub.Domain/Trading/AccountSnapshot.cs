namespace TradingHub.Domain.Trading;

public sealed record CashBalance
{
    public required string Asset { get; init; }

    public required decimal Available { get; init; }

    public required decimal Reserved { get; init; }
}

public sealed record BrokerPositionSnapshot
{
    public required string InstrumentId { get; init; }

    public required decimal NetQuantity { get; init; }

    public required decimal AveragePrice { get; init; }

    public decimal UnrealizedPnl { get; init; }
}

public sealed record BrokerAccountSnapshot
{
    public required string AccountId { get; init; }

    public required DateTimeOffset ObservedAt { get; init; }

    public decimal? Balance { get; init; }

    public decimal? Equity { get; init; }

    public decimal? MarginAvailable { get; init; }

    public IReadOnlyList<CashBalance> CashBalances { get; init; } = [];

    public IReadOnlyList<BrokerPositionSnapshot> Positions { get; init; } = [];
}
