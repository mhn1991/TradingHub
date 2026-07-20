namespace LiveTradingHost.Api;

public sealed record LiveEngineStatusDto
{
    public required int Revision { get; init; }
    public required string EngineState { get; init; }
    public required ConnectionsHealthDto Connections { get; init; }
    public required IReadOnlyList<MarketStatusDto> Markets { get; init; }
    public required IReadOnlyList<AgentStatusDto> Agents { get; init; }
    public required IReadOnlyList<AnalysisProfileStatusDto> AnalysisProfiles { get; init; }
    public DecisionEpochStatusDto? LastDecisionEpoch { get; init; }
    public LiveRuntimeStatusDto? Runtime { get; init; }
    public required DateTimeOffset AsOf { get; init; }
    public string? Message { get; init; }
}

public sealed record AnalysisProfileStatusDto
{
    public required string ProfileHash { get; init; }
    public required string Instrument { get; init; }
    public required IReadOnlyList<string> RequiredIntervals { get; init; }
    public required long LastSnapshotVersion { get; init; }
    public DateTimeOffset? LastAvailableAt { get; init; }
    public required double LastBuildDurationMilliseconds { get; init; }
    public required long CacheHits { get; init; }
    public required long CacheMisses { get; init; }
    public required long FailureCount { get; init; }
    public required int DependentAgentCount { get; init; }
}

public sealed record DecisionEpochStatusDto
{
    public required long Epoch { get; init; }
    public required int ExpectedAgents { get; init; }
    public required int CompletedAgents { get; init; }
    public required int FailedAgents { get; init; }
    public required int TimedOutAgents { get; init; }
    public required int MissingAgents { get; init; }
    public required int CandidateCount { get; init; }
    public required double DurationMilliseconds { get; init; }
}

public sealed record ConnectionsHealthDto
{
    public required bool QuoteStreamConnected { get; init; }
    public DateTimeOffset? LastQuoteStreamFaultAt { get; init; }
    public required bool RestReachable { get; init; }
    public required string LeaseState { get; init; }
}

public sealed record MarketStatusDto
{
    public required string Instrument { get; init; }
    public decimal? Bid { get; init; }
    public decimal? Ask { get; init; }
    public decimal? Spread { get; init; }
    public DateTimeOffset? LastM1CloseAt { get; init; }
    public required bool Ready { get; init; }
    public string? Regime { get; init; }
    public required string State { get; init; }
}

/// <summary>One row per registered <see
/// cref="LiveTrading.Agents.AgentInstanceState"/>. <see cref="LastMetaLabelRiskMultiplier"/> is the
/// visible proof point for the acceptance criterion "meta multiplier never exceeds one."</summary>
public sealed record AgentStatusDto
{
    public Guid AgentInstanceId { get; init; }
    public string DeploymentId { get; init; } = string.Empty;
    public required string StrategyId { get; init; }
    public required string Instrument { get; init; }
    public Guid PolicyBundleId { get; init; }
    public int PolicyRevision { get; init; }
    public string AnalysisProfileHash { get; init; } = string.Empty;
    public required string Mode { get; init; }
    public string? LastStatus { get; init; }
    public string Health { get; init; } = "Unknown";
    public long LastEvaluatedEpoch { get; init; }
    public long LastSnapshotVersion { get; init; }
    public DateTimeOffset? LastEvaluatedAt { get; init; }
    public long Evaluations { get; init; }
    public long Timeouts { get; init; }
    public double AverageEvaluationDurationMilliseconds { get; init; }
    public double P95EvaluationDurationMilliseconds { get; init; }
    public int MailboxDepth { get; init; }
    public required long CandidatesObserved { get; init; }
    public required long CandidatesBuy { get; init; }
    public required long CandidatesSell { get; init; }
    public required long RejectedBySetupCalibration { get; init; }
    public required long RejectedByMetaLabel { get; init; }
    public required long RejectedByTradingCondition { get; init; }
    public long RejectedByDataQuality { get; init; }
    public string? LastDecisionId { get; init; }
    public string? LastCandidateId { get; init; }
    public string? LastAction { get; init; }
    public decimal? LastReferencePrice { get; init; }
    public decimal? LastStopLossPrice { get; init; }
    public decimal? LastTakeProfitPrice { get; init; }
    public decimal? LastMetaLabelProbability { get; init; }
    public decimal? LastMetaLabelRiskMultiplier { get; init; }
    public string? LastError { get; init; }
    public string? LastRejection { get; init; }
}

public sealed record LiveRuntimeStatusDto
{
    public required bool BrokerWritesEnabled { get; init; }
    public required bool AutomaticExecutionEnabled { get; init; }
    public required bool PartialCloseEnabled { get; init; }
    public required bool DynamicStopReplacementEnabled { get; init; }
    public required bool CanOpenNewEntries { get; init; }
    public required string SafetyState { get; init; }
    public string? SafetyMessage { get; init; }
    public decimal? Equity { get; init; }
    public decimal? MarginUsed { get; init; }
    public required int PendingManualCandidates { get; init; }
    public required int ActiveReservations { get; init; }
    public required int Orders { get; init; }
    public required int Positions { get; init; }
    public required int ReconciliationDifferences { get; init; }
    public DateTimeOffset? LastReconciledAt { get; init; }
    public required LiveShadowStatusDto Shadow { get; init; }
}

public sealed record LiveShadowStatusDto
{
    public required bool Enabled { get; init; }
    public required long CandidateCount { get; init; }
    public required long AdmittedCount { get; init; }
    public required long RejectedCount { get; init; }
    public required int OpenPositionCount { get; init; }
    public required long CompletedCount { get; init; }
    public required long AmbiguousCount { get; init; }
    public required decimal NetProfitLoss { get; init; }
    public required decimal UnrealizedProfitLoss { get; init; }
    public required decimal NetR { get; init; }
    public string? LastPlaybookId { get; init; }
    public string? LastCandidateId { get; init; }
    public string? LastRejection { get; init; }
    public decimal? LastStopPrice { get; init; }
    public decimal? LastTargetPrice { get; init; }
    public int? LastPolicyRevision { get; init; }
}


public sealed record LiveManualCandidateDto
{
    public required string CandidateId { get; init; }
    public required string Fingerprint { get; init; }
    public required string State { get; init; }
    public required DateTimeOffset DecisionTime { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required string StrategyId { get; init; }
    public required string Instrument { get; init; }
    public required string Action { get; init; }
    public decimal? ReferencePrice { get; init; }
    public decimal? StopLossPrice { get; init; }
    public decimal? TakeProfitPrice { get; init; }
    public required decimal Quantity { get; init; }
    public required decimal EstimatedStopRisk { get; init; }
    public required decimal EstimatedMargin { get; init; }
    public required decimal PortfolioScore { get; init; }
    public required decimal MetaProbability { get; init; }
    public required decimal CombinedRiskMultiplier { get; init; }
    public string? ReviewedBy { get; init; }
    public string? ReviewReason { get; init; }
}

public sealed record LiveOrderDto
{
    public required string ClientOrderId { get; init; }
    public string? BrokerOrderId { get; init; }
    public string? BrokerTradeId { get; init; }
    public required string StrategyId { get; init; }
    public required string Instrument { get; init; }
    public required string Side { get; init; }
    public required decimal RequestedQuantity { get; init; }
    public required decimal FilledQuantity { get; init; }
    public decimal? AverageFillPrice { get; init; }
    public decimal? ProtectiveStopPrice { get; init; }
    public decimal? TakeProfitPrice { get; init; }
    public required string State { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public string? LastReason { get; init; }
}

public sealed record LivePositionDto
{
    public required string PositionId { get; init; }
    public string? BrokerTradeId { get; init; }
    public required string StrategyId { get; init; }
    public required string Instrument { get; init; }
    public required string Side { get; init; }
    public required decimal InitialQuantity { get; init; }
    public required decimal Quantity { get; init; }
    public decimal? AveragePrice { get; init; }
    public decimal? InitialStopPrice { get; init; }
    public decimal? ProtectiveStopPrice { get; init; }
    public decimal? TakeProfitPrice { get; init; }
    public required decimal MaximumFavourableExcursionR { get; init; }
    public required decimal MaximumAdverseExcursionR { get; init; }
    public string? LastManagementAction { get; init; }
    public string? LastManagementReason { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record LiveCapabilitiesDto
{
    public required bool AtomicStopOnFillImplemented { get; init; }
    public required bool ExplicitFullCloseImplemented { get; init; }
    public required bool PartialCloseImplemented { get; init; }
    public required bool PartialClosePracticeCertified { get; init; }
    public required bool PartialCloseEnabled { get; init; }
    public required bool DynamicStopReplacementImplemented { get; init; }
    public required bool DynamicStopReplacementPracticeCertified { get; init; }
    public required bool DynamicStopReplacementEnabled { get; init; }
}
