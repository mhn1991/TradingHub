namespace Brokers.Models;

/// <summary>
/// A broker-authoritative, ordered batch of normalized order/trade events newer than a durable
/// cursor. The cursor must advance even when no normalized trading event is returned because a
/// broker account may contain unrelated transactions such as financing or heartbeats.
/// </summary>
public sealed record BrokerEventHistoryBatch
{
    public required IReadOnlyList<OrderEvent> Events { get; init; }

    public required string LastTransactionId { get; init; }
}
