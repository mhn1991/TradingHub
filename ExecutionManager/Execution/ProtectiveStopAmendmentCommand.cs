using Brokers.Models;

namespace ExecutionManager;

public sealed record ProtectiveStopAmendmentCommand
{
    public required InstrumentKey Instrument { get; init; }
    public required string StrategyId { get; init; }
    public required string SetupId { get; init; }
    public required string PositionId { get; init; }
    public string? ExistingStopOrderId { get; init; }
    public required OrderSide PositionSide { get; init; }
    public required decimal PositionQuantity { get; init; }
    public required decimal EntryPrice { get; init; }
    public required decimal InitialStopPrice { get; init; }
    public required decimal CurrentStopPrice { get; init; }
    public required decimal ProposedStopPrice { get; init; }
    public required decimal CurrentExecutablePrice { get; init; }
    public required decimal MinimumPriceIncrement { get; init; }
    public required decimal OpenProfitR { get; init; }
    public required StopAmendmentReason AmendmentReason { get; init; }
    public required string Reason { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required long RequestedSequence { get; init; }
    public required long EffectiveFromExecutionSequence { get; init; }
    public string? ClientAmendmentId { get; init; }
}
