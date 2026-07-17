namespace Brokers.Models;

public enum StandardOrderType
{
    Market,
    Limit,
    Stop,
    StopLimit
}

public enum StandardTimeInForce
{
    GoodTillCancelled,
    ImmediateOrCancel,
    FillOrKill,
    Day,
    GoodTillDate
}

public enum QuantityUnit
{
    Units,
    BaseAsset,
    QuoteCurrency,
    Contracts
}

public readonly record struct OrderQuantity
{
    public OrderQuantity(decimal value, QuantityUnit unit)
    {
        if (value <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Order quantity must be positive.");
        }

        Value = value;
        Unit = unit;
    }

    public decimal Value { get; }
    public QuantityUnit Unit { get; }
}

public sealed record StopLossInstruction(decimal Price);
public sealed record TakeProfitInstruction(decimal Price);

public sealed record PlaceOrderRequest
{
    public required InstrumentKey Instrument { get; init; }
    public required OrderSide Side { get; init; }
    public required StandardOrderType Type { get; init; }
    public required OrderQuantity Quantity { get; init; }
    public decimal? LimitPrice { get; init; }
    public decimal? StopPrice { get; init; }
    public StandardTimeInForce TimeInForce { get; init; } = StandardTimeInForce.GoodTillCancelled;
    public DateTimeOffset? ExpireAt { get; init; }
    public string? ClientOrderId { get; init; }
    public string? StrategyId { get; init; }
    public string? DecisionId { get; init; }
    public string? SetupId { get; init; }
    public string? PortfolioReservationId { get; init; }
    public string? RiskClusterId { get; init; }
    /// <summary>Rejects or clamps any fill that would increase or reverse exposure.</summary>
    public bool ReduceOnly { get; init; }
    public StopLossInstruction? StopLoss { get; init; }
    public TakeProfitInstruction? TakeProfit { get; init; }
}

public enum SubmissionStatus
{
    Rejected,
    Accepted,
    Pending,
    PartiallyFilled,
    Filled
}

public enum ExecutionCertainty
{
    NotSent,
    Rejected,
    Accepted,
    Unknown
}

public sealed record OrderSubmission
{
    public required string ClientOrderId { get; init; }
    public string? BrokerOrderId { get; init; }
    public required SubmissionStatus Status { get; init; }
    public required ExecutionCertainty Certainty { get; init; }
    public string? RejectionReason { get; init; }
}

public enum OrderPositionEffect
{
    Unknown,
    OpenOrIncrease,
    Reduce,
    Close
}

public enum OrderEventType
{
    Accepted,
    Rejected,
    Triggered,
    PartiallyFilled,
    Filled,
    Cancelled,
    Replaced,
    Expired
}

public sealed record OrderEvent
{
    public string? BrokerTransactionId { get; init; }
    public string? BrokerTradeId { get; init; }
    public required string BrokerOrderId { get; init; }
    public string? ClientOrderId { get; init; }
    public required InstrumentKey Instrument { get; init; }
    public required OrderEventType Type { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public decimal? FillPrice { get; init; }
    public decimal? FillQuantity { get; init; }
    public decimal? RemainingQuantity { get; init; }
    public decimal? Fee { get; init; }
    public decimal? AppliedSpread { get; init; }
    public decimal? AppliedSlippage { get; init; }
    public string? ExecutionModelVersion { get; init; }
    public OrderPositionEffect PositionEffect { get; init; } = OrderPositionEffect.Unknown;
    public decimal? PositionQuantityAfter { get; init; }
    public decimal? RealizedProfitLoss { get; init; }
    public string? Message { get; init; }
}
