namespace TradingObservability.Abstractions.Reporting;

/// <summary>Read-only, environment-neutral access to operator-safe structured reports.</summary>
public interface ITradingReportQueryService
{
    Task<SimulationOperationalReport?> GetSimulationAsync(Guid simulationId, CancellationToken cancellationToken = default);
    Task<ExperimentOperationalReport?> GetExperimentAsync(Guid experimentId, CancellationToken cancellationToken = default);
    Task<LiveSessionOperationalReport?> GetLiveSessionAsync(Guid runtimeSessionId, CancellationToken cancellationToken = default);
    Task<DeploymentOperationalReport?> GetDeploymentAsync(Guid deploymentId, DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken cancellationToken = default);
    Task<CandidateExplanationReport?> GetCandidateAsync(Guid candidateId, CancellationToken cancellationToken = default);
    Task<PositionExplanationReport?> GetPositionAsync(Guid positionId, CancellationToken cancellationToken = default);
    Task<AgentOperationalReport> GetAgentAsync(Guid agentInstanceId, DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ReasonFrequencyReport>> GetRejectionReasonsAsync(ReportQueryRange range, CancellationToken cancellationToken = default);
}

public sealed record ReportQueryRange(
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    Guid? DeploymentId = null,
    Guid? SimulationId = null,
    Guid? AgentInstanceId = null,
    int Limit = 50);

public sealed record TimelineReportItem(
    DateTimeOffset OccurredAt,
    string Type,
    string ReasonCode,
    string? DetailsJson = null);

public sealed record ReasonFrequencyReport(string ReasonCode, long Count);

public sealed record ActivityAggregateReport(
    long Evaluations,
    long NoSetup,
    long Warmup,
    long Buy,
    long Sell,
    long Hold,
    long CandidatesCreated,
    long CandidatesRejected,
    long StateTransitions,
    long Timeouts,
    long Errors,
    double MeanEvaluationMilliseconds,
    double P95EvaluationMilliseconds);

public sealed record SimulationOperationalReport(
    Guid SimulationId,
    Guid? ExperimentId,
    string Status,
    string Phase,
    long Revision,
    string ConfigurationHash,
    string? InputHash,
    DateTimeOffset RequestedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    long ProcessedCandles,
    decimal ProgressPercent,
    decimal CandlesPerSecond,
    ActivityAggregateReport Activity,
    IReadOnlyList<ReasonFrequencyReport> TopReasons,
    IReadOnlyList<TimelineReportItem> Timeline,
    IReadOnlyList<SimulationBlobReport> Blobs,
    IReadOnlyList<string> CompletenessWarnings);

public sealed record SimulationBlobReport(
    Guid BlobId,
    string Kind,
    string ContentHash,
    long SizeBytes,
    string MediaType,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RetentionUntil);

public sealed record ExperimentOperationalReport(
    Guid ExperimentId,
    string Name,
    string Status,
    string ConfigurationHash,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ExperimentRunReport> Runs,
    IReadOnlyList<ExperimentComparisonReport> Comparisons);

public sealed record ExperimentRunReport(
    Guid ExperimentRunId,
    Guid SimulationId,
    Guid ProfileRevisionId,
    string? VariantId,
    Guid? SharedAnalysisGroupId,
    DateTimeOffset? LearningFrom,
    DateTimeOffset? LearningTo,
    DateTimeOffset? EmbargoFrom,
    DateTimeOffset? EmbargoTo,
    DateTimeOffset? EvaluationFrom,
    DateTimeOffset? EvaluationTo);

public sealed record ExperimentComparisonReport(
    Guid BaselineSimulationId,
    Guid CandidateSimulationId,
    string MetricsJson,
    DateTimeOffset CreatedAt);

public sealed record LiveSessionOperationalReport(
    Guid RuntimeSessionId,
    string SessionKind,
    Guid? DeploymentId,
    string HostInstanceId,
    string ConfigurationHash,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string Status,
    string? TerminationReason,
    ActivityAggregateReport Activity,
    long CandidateCount,
    long OrderCount,
    long OpenPositionCount,
    IReadOnlyList<TimelineReportItem> Incidents,
    IReadOnlyList<ReasonFrequencyReport> TopReasons);

public sealed record DeploymentOperationalReport(
    Guid DeploymentId,
    string AccountAlias,
    string Environment,
    Guid PolicyRevisionId,
    string ExecutionMode,
    string Status,
    string DeploymentHash,
    DateTimeOffset StartedAt,
    DateTimeOffset? StoppedAt,
    DateTimeOffset RangeFrom,
    DateTimeOffset RangeTo,
    long CandidateCount,
    long ApprovedPortfolioCount,
    long RejectedPortfolioCount,
    long OrderCount,
    long FillCount,
    long OpenPositionCount,
    decimal RealisedPnl,
    decimal UnrealisedPnl,
    IReadOnlyList<TimelineReportItem> Incidents,
    IReadOnlyList<ReasonFrequencyReport> TopReasons);

public sealed record CandidateExplanationReport(
    Guid CandidateId,
    Guid DecisionId,
    Guid DeploymentId,
    Guid PolicyRevisionId,
    long InstrumentId,
    string StrategyId,
    string SetupId,
    string Direction,
    string Status,
    DateTimeOffset DecisionTime,
    decimal ReferenceMid,
    decimal StopPrice,
    decimal? TargetPrice,
    decimal RawConfidence,
    string DeterministicContextJson,
    string SetupCalibrationJson,
    string MetaLabelJson,
    IReadOnlyList<CandidateStageReport> Funnel,
    SizingExplanationReport? Sizing,
    PortfolioExplanationReport? Portfolio,
    IReadOnlyList<OrderExplanationReport> Orders,
    PositionSummaryReport? Position,
    OutcomeExplanationReport? Outcome,
    IReadOnlyList<string> CompletenessWarnings);

public sealed record CandidateStageReport(
    DateTimeOffset OccurredAt,
    string Stage,
    string Outcome,
    string ReasonCode,
    decimal? ConfidenceBefore,
    decimal? ConfidenceAfter,
    decimal? RiskMultiplier,
    string DetailsJson);

public sealed record SizingExplanationReport(
    bool Approved,
    string ReasonCode,
    decimal BaseRiskAmount,
    decimal RawQuantity,
    decimal NormalizedQuantity,
    decimal EstimatedMargin,
    string AuditJson);

public sealed record PortfolioExplanationReport(
    bool Approved,
    string ReasonCode,
    decimal RequestedRisk,
    decimal ApprovedRisk,
    decimal RequestedQuantity,
    decimal ApprovedQuantity,
    string SnapshotJson);

public sealed record OrderExplanationReport(
    Guid OrderId,
    string ClientOrderId,
    string? MaskedBrokerOrderId,
    string? MaskedBrokerTradeId,
    string State,
    string SubmissionCertainty,
    decimal RequestedQuantity,
    decimal FilledQuantity,
    decimal? AverageFillPrice,
    int FillCount,
    decimal SpreadCost,
    decimal SlippageCost,
    DateTimeOffset CreatedAt);

public sealed record PositionSummaryReport(
    Guid PositionId,
    string State,
    decimal OriginalQuantity,
    decimal RemainingQuantity,
    decimal AverageEntryPrice,
    decimal CurrentStopPrice,
    decimal? TargetPrice,
    decimal RealisedPnl,
    decimal UnrealisedPnl,
    decimal? MfeR,
    decimal? MaeR,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt);

public sealed record OutcomeExplanationReport(
    bool TargetReached,
    bool StopReached,
    string FirstTerminalEvent,
    decimal MfeR,
    decimal MaeR,
    decimal SpreadAdjustedR,
    DateTimeOffset CalculatedAt);

public sealed record PositionExplanationReport(
    PositionSummaryReport Position,
    IReadOnlyList<TimelineReportItem> PositionEvents,
    IReadOnlyList<ManagementActionReport> ManagementActions);

public sealed record ManagementActionReport(
    DateTimeOffset OccurredAt,
    string Action,
    string ReasonCode,
    decimal CurrentR,
    decimal MfeR,
    decimal MaeR,
    decimal? PreviousStop,
    decimal? RequestedStop,
    decimal? ConfirmedStop,
    decimal? RequestedReduction,
    decimal? ConfirmedReduction,
    string DetailsJson);

public sealed record AgentOperationalReport(
    Guid AgentInstanceId,
    DateTimeOffset RangeFrom,
    DateTimeOffset RangeTo,
    ActivityAggregateReport Activity,
    IReadOnlyList<ReasonFrequencyReport> TopReasons,
    IReadOnlyList<TimelineReportItem> StateChangesAndIncidents);
