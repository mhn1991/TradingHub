namespace TradingHub.Domain.Trading;

public sealed record ExecutionReport
{
    public required string OrderId { get; init; }

    public required string BrokerOrderId { get; init; }

    public required string InstrumentId { get; init; }

    public required OrderSide Side { get; init; }

    public required OrderStatus Status { get; init; }

    public decimal LastFillQuantity { get; init; }

    public decimal? LastFillPrice { get; init; }

    public decimal CumulativeFilledQuantity { get; init; }

    public decimal Fee { get; init; }

    public string? FeeAsset { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public string? Reason { get; init; }
}

public sealed record OrderSubmissionResult
{
    public required SubmissionOutcome Outcome { get; init; }

    public required OrderStatus Status { get; init; }

    public string? BrokerOrderId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public string? Reason { get; init; }

    public IReadOnlyList<ExecutionReport> ImmediateExecutions { get; init; } = [];
}

public sealed record OrderCancellationResult
{
    public required SubmissionOutcome Outcome { get; init; }

    public required OrderStatus Status { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public string? Reason { get; init; }
}
