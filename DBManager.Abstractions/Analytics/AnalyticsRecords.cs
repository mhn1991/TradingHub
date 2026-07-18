using DBManager.Abstractions.Decision;

namespace DBManager.Abstractions.Analytics;

public enum TerminalEvent
{
    TargetReached,
    StopReached,
    HorizonExpired,
    ManuallyClosed
}

/// <summary>Immutable record for bulk COPY import (section 19.2, section 15's <c>IResearchBulkStore</c>).</summary>
public sealed record CandidateOutcomeRecord
{
    public required Guid OutcomeId { get; init; }
    public required Guid CandidateId { get; init; }
    public required DateTimeOffset DecisionTime { get; init; }
    public required DateTimeOffset HorizonEnd { get; init; }
    public required bool TargetReached { get; init; }
    public required bool StopReached { get; init; }
    public required TerminalEvent FirstTerminalEvent { get; init; }
    public required decimal MfePrice { get; init; }
    public required decimal MaePrice { get; init; }
    public required decimal MfeR { get; init; }
    public required decimal MaeR { get; init; }
    public required decimal MaximumAchievableR { get; init; }
    public required TimeSpan TimeToMfe { get; init; }
    public required TimeSpan TimeToMae { get; init; }
    public required decimal SpreadAdjustedR { get; init; }
    public required int OutcomeVersion { get; init; }
}

public sealed record RecordExecutionQuality
{
    public required Guid ExecutionQualityId { get; init; }
    public required Guid OrderId { get; init; }
    public required decimal DecisionBid { get; init; }
    public required decimal DecisionAsk { get; init; }
    public required decimal SubmissionBid { get; init; }
    public required decimal SubmissionAsk { get; init; }
    public required decimal ExpectedFillPrice { get; init; }
    public required decimal ActualFillPrice { get; init; }
    public required decimal ExpectedSpread { get; init; }
    public required decimal ActualSpread { get; init; }
    public required decimal ExpectedSlippage { get; init; }
    public required decimal ActualSlippage { get; init; }
    public required int SubmissionLatencyMs { get; init; }
    public int? AckLatencyMs { get; init; }
    public int? FillLatencyMs { get; init; }
    public required short Session { get; init; }
    public required Regime Regime { get; init; }
}

public sealed record RecordModelMonitoringWindow
{
    public required Guid WindowId { get; init; }
    public required Guid ArtifactId { get; init; }
    public required DateTimeOffset WindowStart { get; init; }
    public required DateTimeOffset WindowEnd { get; init; }
    public required long SampleCount { get; init; }
    public required string DriftMetricsJson { get; init; }
    public required string CalibrationMetricsJson { get; init; }
}
