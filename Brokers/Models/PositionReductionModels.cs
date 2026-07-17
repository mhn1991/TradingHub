namespace Brokers.Models;

public enum PositionReductionStatus
{
    Rejected,
    Accepted,
    Filled,
    Partial,
    Unsupported,
    Unknown
}

/// <summary>
/// Explicit reduce-only mutation for one broker trade/position. Quantity is always absolute and
/// must not exceed the strategy-owned remaining quantity checked by the live registry.
/// </summary>
public sealed record ReduceBrokerPositionRequest
{
    public required InstrumentKey Instrument { get; init; }
    public required string PositionId { get; init; }
    public required OrderSide PositionSide { get; init; }
    public required decimal Quantity { get; init; }
    public required decimal OwnedQuantity { get; init; }
    public required string ClientRequestId { get; init; }
    public required DateTimeOffset RequestedAt { get; init; }
    public string? Reason { get; init; }

    public bool IsFullClose => Quantity == OwnedQuantity;

    public void Validate()
    {
        if (Instrument.IsEmpty || string.IsNullOrWhiteSpace(PositionId) ||
            string.IsNullOrWhiteSpace(ClientRequestId) || Quantity <= 0m || OwnedQuantity <= 0m ||
            Quantity > OwnedQuantity || !Enum.IsDefined(PositionSide))
        {
            throw new ArgumentException(
                "A position reduction requires an instrument, broker position/trade id, valid side, " +
                "positive owned quantity, and a reduction no larger than the owned quantity.");
        }
    }
}

public sealed record PositionReductionResult
{
    public required PositionReductionStatus Status { get; init; }
    public required string ClientRequestId { get; init; }
    public required string PositionId { get; init; }
    public string? BrokerOrderId { get; init; }
    public string? BrokerTransactionId { get; init; }
    public required decimal RequestedQuantity { get; init; }
    public decimal? FilledQuantity { get; init; }
    public decimal? FillPrice { get; init; }
    public required ExecutionCertainty Certainty { get; init; }
    public string? Reason { get; init; }
}
