using Brokers.Abstractions;
using Brokers.Models;

namespace Simulator.Models;

public enum OcoFillPolicy
{
    StopLossFirst,
    TakeProfitFirst
}

public sealed record SimulationOptions
{
    public BrokerKind ModelledBroker { get; init; } = BrokerKind.Oanda;
    public string AccountId { get; init; } = "simulated-account";
    public string BaseCurrency { get; init; } = "USD";
    public decimal StartingBalance { get; init; } = 100_000m;
    public decimal Leverage { get; init; } = 20m;
    public decimal CommissionRate { get; init; } = 0.001m;
    public decimal SpreadBasisPoints { get; init; } = 1m;
    public decimal SlippageBasisPoints { get; init; } = 0.5m;
    public bool EnforceMarginRequirements { get; init; } = true;
    public OcoFillPolicy OcoFillPolicy { get; init; } = OcoFillPolicy.StopLossFirst;
    public IReadOnlyDictionary<string, decimal> QuoteToBaseCurrencyRates { get; init; } =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
    public int CandleCapacity { get; init; } = 2_000;
    public int LedgerCapacity { get; init; } = 20_000;
    public int OrderEventCapacity { get; init; } = 4_096;
}

public enum LedgerEntryType
{
    Deposit,
    Commission,
    RealisedProfitLoss,
    MarginReserved,
    MarginReleased
}

public sealed record LedgerEntry
{
    public required long Sequence { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required LedgerEntryType Type { get; init; }
    public required decimal Amount { get; init; }
    public required string Currency { get; init; }
    public string? OrderId { get; init; }
    public string? Description { get; init; }
}

public sealed record SimulationResult
{
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset EndedAt { get; init; }
    public required decimal StartingBalance { get; init; }
    public required decimal FinalBalance { get; init; }
    public required decimal FinalEquity { get; init; }
    public required decimal UnrealizedProfitLoss { get; init; }
    public required decimal NetProfit { get; init; }
    public required decimal TotalCommission { get; init; }
    public required int SubmittedOrders { get; init; }
    public required int FilledOrders { get; init; }
    public required int RejectedOrders { get; init; }
    public required IReadOnlyList<BrokerPosition> OpenPositions { get; init; }
    public required IReadOnlyList<LedgerEntry> Ledger { get; init; }
}
