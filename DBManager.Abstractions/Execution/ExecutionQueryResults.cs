using DBManager.Abstractions.Decision;

namespace DBManager.Abstractions.Execution;

public sealed record OrderSubmissionIntentResult
{
    public required DurableResult Result { get; init; }
    public required Guid OrderId { get; init; }
    public required Guid CommandId { get; init; }
}

public sealed record BrokerEventApplyResult
{
    public required DurableResult Result { get; init; }
    public Guid? OrderId { get; init; }
    public Guid? PositionId { get; init; }
}

public sealed record OrderDetail
{
    public required Guid OrderId { get; init; }
    public required Guid CandidateId { get; init; }
    public required Guid ReservationId { get; init; }
    public required Guid BrokerAccountId { get; init; }
    public required long InstrumentId { get; init; }
    public required string StrategyId { get; init; }
    public required string ClientOrderId { get; init; }
    public string? BrokerOrderId { get; init; }
    public string? BrokerTradeId { get; init; }
    public required TradeDirection Direction { get; init; }
    public required decimal RequestedQuantity { get; init; }
    public required decimal FilledQuantity { get; init; }
    public decimal? AverageFillPrice { get; init; }
    public required OrderState State { get; init; }
    public required SubmissionCertainty SubmissionCertainty { get; init; }
    public required long Version { get; init; }
}

public sealed record PositionDetail
{
    public required Guid PositionId { get; init; }
    public required Guid BrokerAccountId { get; init; }
    public required long InstrumentId { get; init; }
    public required string StrategyId { get; init; }
    public required string BrokerTradeId { get; init; }
    public required TradeDirection Direction { get; init; }
    public required PositionState State { get; init; }
    public required decimal RemainingQuantity { get; init; }
    public required decimal AverageEntryPrice { get; init; }
    public required decimal RealisedPnl { get; init; }
    public required long Version { get; init; }
}

public sealed record BrokerStreamCursorDetail
{
    public required Guid BrokerAccountId { get; init; }
    public string? LastAppliedTransactionId { get; init; }
    public DateTimeOffset? LastAppliedBrokerTime { get; init; }
    public required long ConnectionGeneration { get; init; }
}

/// <summary>Section 15's <c>LoadRecoveryStateAsync</c> — proves "restart after fill recovers correctly."</summary>
public sealed record RecoveryState
{
    public required Guid BrokerAccountId { get; init; }
    public required IReadOnlyList<OrderDetail> OpenOrders { get; init; }
    public required IReadOnlyList<PositionDetail> OpenPositions { get; init; }
    public required BrokerStreamCursorDetail? Cursor { get; init; }
}
