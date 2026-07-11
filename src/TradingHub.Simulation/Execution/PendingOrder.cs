using TradingHub.Domain.Trading;

namespace TradingHub.Simulation.Execution;

internal sealed class PendingOrder
{
    public required long Sequence { get; init; }

    public required string BrokerOrderId { get; init; }

    public required OrderRequest Request { get; init; }

    public required DateTimeOffset EligibleAt { get; init; }

    public decimal FilledQuantity { get; set; }

    public bool StopTriggered { get; set; }

    public decimal RemainingQuantity => Request.Quantity - FilledQuantity;
}

internal sealed record FillDecision
{
    public required OrderStatus Status { get; init; }

    public decimal Quantity { get; init; }

    public decimal? Price { get; init; }

    public decimal Fee { get; init; }

    public string? Reason { get; init; }
}
