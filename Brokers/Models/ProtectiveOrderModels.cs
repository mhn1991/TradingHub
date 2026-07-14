namespace Brokers.Models;

/// <summary>Broker-advertised protective-order mutation capabilities.</summary>
public sealed record TradingBrokerCapabilities
{
    public required bool SupportsNativeStopAmendment { get; init; }
    public required bool SupportsAtomicOrderReplacement { get; init; }
    public required bool SupportsDependentOcoAmendment { get; init; }

    public static TradingBrokerCapabilities Unsupported { get; } = new()
    {
        SupportsNativeStopAmendment = false,
        SupportsAtomicOrderReplacement = false,
        SupportsDependentOcoAmendment = false
    };
}

public enum ProtectiveStopAmendmentStatus
{
    Rejected,
    Accepted,
    Replaced,
    Pending,
    Unsupported,
    Unknown
}

public enum StopAmendmentReason
{
    BreakEven,
    StructureSwing,
    StructureZone,
    StructureChannel,
    AtrFallback,
    ProfitFloor,
    MfeGiveback,
    Manual,
    Other
}

/// <summary>
/// Broker-neutral request to reduce an existing position's protective-stop risk.
/// CurrentExecutablePrice and EffectiveFromExecutionSequence make validation and
/// simulator no-lookahead semantics explicit.
/// </summary>
public sealed record AmendProtectiveStopRequest
{
    public required InstrumentKey Instrument { get; init; }
    public required string PositionId { get; init; }
    public string? ExistingStopOrderId { get; init; }
    public required decimal CurrentStopPrice { get; init; }
    public required decimal NewStopPrice { get; init; }
    public required decimal CurrentExecutablePrice { get; init; }
    public required decimal PositionQuantity { get; init; }
    public required OrderSide PositionSide { get; init; }
    public required decimal MinimumPriceIncrement { get; init; }
    public required string ClientAmendmentId { get; init; }
    public required string Reason { get; init; }
    public required DateTimeOffset RequestedAt { get; init; }
    public required long EffectiveFromExecutionSequence { get; init; }
}

public sealed record ProtectiveStopAmendmentResult
{
    public required ProtectiveStopAmendmentStatus Status { get; init; }
    public required string ClientAmendmentId { get; init; }
    public string? PreviousStopOrderId { get; init; }
    public string? CurrentStopOrderId { get; init; }
    public required decimal RequestedStopPrice { get; init; }
    public decimal? AcceptedStopPrice { get; init; }
    public DateTimeOffset? AcceptedAt { get; init; }
    public long? EffectiveFromExecutionSequence { get; init; }
    public string? RejectionReason { get; init; }
    public required ExecutionCertainty Certainty { get; init; }
}
