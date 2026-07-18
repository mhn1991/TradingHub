using DBManager.Abstractions.Decision;

namespace DBManager.Abstractions.Execution;

/// <summary>
/// Section 13.2 + 12.5's first transaction: validates the reservation, creates the durable order
/// command, and creates the local order in <see cref="OrderState.SubmissionPending"/> — all before
/// any broker HTTP call. A duplicate <see cref="IdempotencyKey"/> returns the existing order rather
/// than creating another one (section 13.2).
/// </summary>
public sealed record CreateOrderSubmissionIntent
{
    public required Guid CommandId { get; init; }
    public required string IdempotencyKey { get; init; }
    public required Guid CandidateId { get; init; }
    public required Guid ReservationId { get; init; }
    public required OrderCommandType CommandType { get; init; }
    public required string ClientOrderId { get; init; }
    public required Guid BrokerAccountId { get; init; }
    public required long InstrumentId { get; init; }
    public required string StrategyId { get; init; }
    public required TradeDirection Direction { get; init; }
    public required decimal RequestedQuantity { get; init; }
    public decimal? RequestedStop { get; init; }
    public decimal? RequestedTarget { get; init; }
    public required decimal InitialStopPrice { get; init; }
    public decimal? InitialTargetPrice { get; init; }
}

public enum SubmissionOutcome
{
    Accepted,
    Rejected,
    Unknown
}

/// <summary>
/// Section 12.5's second transaction — the synchronous broker HTTP response, applied outside any
/// open transaction. <see cref="SubmissionOutcome.Unknown"/> must retain the reservation rather
/// than releasing it ("never retry blindly").
/// </summary>
public sealed record ApplyImmediateSubmissionResult
{
    public required Guid OrderId { get; init; }
    public required SubmissionOutcome Outcome { get; init; }
    public string? BrokerOrderId { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public string? ReasonCode { get; init; }
}

/// <summary>The broker transaction stream event to apply atomically (section 13.1).</summary>
public sealed record ApplyBrokerEvent
{
    public required Guid BrokerAccountId { get; init; }
    public required string BrokerTransactionId { get; init; }
    public required DateTimeOffset BrokerTime { get; init; }
    public required BrokerTransactionType TransactionType { get; init; }
    public string? ClientOrderId { get; init; }
    public string? BrokerOrderId { get; init; }
    public string? BrokerTradeId { get; init; }
    public long? InstrumentId { get; init; }
    public decimal? Quantity { get; init; }
    public decimal? Price { get; init; }
    public decimal? Commission { get; init; }
    public decimal? Financing { get; init; }
    public string? ReasonCode { get; init; }
    public required string RawPayloadJson { get; init; }
}
