namespace DBManager.Abstractions.Analytics;

public interface IAnalyticsStore
{
    Task<DurableResult> RecordExecutionQualityAsync(RecordExecutionQuality command, CancellationToken cancellationToken);

    Task<DurableResult> RecordModelMonitoringWindowAsync(
        RecordModelMonitoringWindow command, CancellationToken cancellationToken);
}

/// <summary>
/// Bulk COPY import through the Research lane (section 15's <c>IResearchBulkStore</c>, section
/// 19.2). Deliberately no per-row idempotency guarantee — COPY is an unconditional append; dedup
/// is the caller's concern, unlike the Critical-lane stores elsewhere in this layer.
/// </summary>
public interface IResearchBulkStore
{
    Task<long> WriteCandidateOutcomesAsync(
        IAsyncEnumerable<CandidateOutcomeRecord> outcomes, CancellationToken cancellationToken);
}
