namespace ExecutionManager;

/// <summary>
/// Controls execution-orchestration behaviour only. Risk thresholds belong to RiskManager,
/// while broker account/order-state checks belong to Brokers.Safety.
/// </summary>
public sealed record ExecutionOptions
{
    public bool TripSafetyOnExecutionFailure { get; init; } = true;
    public string ClientOrderIdPrefix { get; init; } = "agent";
}
