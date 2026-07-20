namespace TradingObservability.Abstractions;

public enum TradingTelemetryType : short
{
    RuntimeLifecycle,
    AgentStateTransition,
    DataQualityStateChange,
    MarketHealthStateChange,
    SafetyStateChange,
    ReconciliationStateChange,
    PersistenceStateChange,
    TrainingPhaseChange,
    Incident
}

public enum TradingTelemetrySeverity : short
{
    Trace,
    Information,
    Warning,
    Error,
    Critical
}

public enum RuntimeSessionKind : short
{
    Simulation,
    Experiment,
    Research,
    LiveShadow,
    LiveExecutable
}

public enum RuntimeSessionStatus : short
{
    Starting,
    Running,
    Completed,
    Failed,
    Cancelled,
    Abandoned
}

public sealed record TelemetryCorrelation
{
    public required Guid RuntimeSessionId { get; init; }
    public Guid? ExperimentId { get; init; }
    public Guid? SimulationId { get; init; }
    public Guid? ResearchRunId { get; init; }
    public Guid? DeploymentId { get; init; }
    public Guid? AgentInstanceId { get; init; }
    public Guid? PolicyRevisionId { get; init; }
    public long? InstrumentId { get; init; }
    public Guid? DecisionId { get; init; }
    public string? CandidateId { get; init; }
    public Guid? ReservationId { get; init; }
    public Guid? OrderCommandId { get; init; }
    public Guid? OrderId { get; init; }
    public Guid? FillId { get; init; }
    public Guid? PositionId { get; init; }
}

public sealed record TradingTelemetryEvent
{
    public required Guid EventId { get; init; }
    public required TradingTelemetryType Type { get; init; }
    public required TradingTelemetrySeverity Severity { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required DateTimeOffset ObservedAt { get; init; }
    public required string ReasonCode { get; init; }
    public required TelemetryCorrelation Correlation { get; init; }
    public required string PayloadJson { get; init; }
}

public interface ITradingTelemetryWriter
{
    ValueTask WriteAsync(TradingTelemetryEvent telemetryEvent, CancellationToken cancellationToken);
    ValueTask FlushAsync(CancellationToken cancellationToken);
}

public sealed record RuntimeSessionRegistration
{
    public required Guid RuntimeSessionId { get; init; }
    public required RuntimeSessionKind Kind { get; init; }
    public Guid? SimulationId { get; init; }
    public Guid? DeploymentId { get; init; }
    public Guid? ResearchRunId { get; init; }
    public required string HostInstanceId { get; init; }
    public required string MachineName { get; init; }
    public required int ProcessId { get; init; }
    public required string ConfigurationHash { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
}

public interface IRuntimeSessionStore
{
    Task StartAsync(RuntimeSessionRegistration registration, CancellationToken cancellationToken);
    Task EndAsync(Guid runtimeSessionId, RuntimeSessionStatus status, string? reason,
        CancellationToken cancellationToken);
}

public sealed record AgentActivityWindow
{
    public required Guid RuntimeSessionId { get; init; }
    public required Guid AgentInstanceId { get; init; }
    public required long InstrumentId { get; init; }
    public required string StrategyId { get; init; }
    public string? PlaybookId { get; init; }
    public required DateTimeOffset WindowStart { get; init; }
    public required DateTimeOffset WindowEnd { get; init; }
    public long EvaluationsObserved { get; init; }
    public long NoSetupCount { get; init; }
    public long WarmupCount { get; init; }
    public long BuyCount { get; init; }
    public long SellCount { get; init; }
    public long HoldCount { get; init; }
    public long CandidatesCreated { get; init; }
    public long CandidatesRejected { get; init; }
    public long StateTransitions { get; init; }
    public long TimeoutCount { get; init; }
    public long ErrorCount { get; init; }
    public required string ReasonCountsJson { get; init; }
    public double MeanEvaluationMilliseconds { get; init; }
    public double MinEvaluationMilliseconds { get; init; }
    public double MaxEvaluationMilliseconds { get; init; }
    public double? MeanCandidateConfidence { get; init; }
    public long? LatestSnapshotVersion { get; init; }
    public DateTimeOffset? LatestMarketTime { get; init; }
}

public interface IAgentActivityStore
{
    Task UpsertAsync(AgentActivityWindow window, CancellationToken cancellationToken);
}
