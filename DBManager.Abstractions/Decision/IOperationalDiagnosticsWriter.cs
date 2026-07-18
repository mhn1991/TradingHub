namespace DBManager.Abstractions.Decision;

/// <summary>
/// Producer-facing enqueue API for Lane B (section 9.2/10.2): a channel write only, never a
/// database round trip, so an Agent evaluation never waits synchronously for PostgreSQL
/// (section 30 Phase 2 acceptance). The channel uses <c>BoundedChannelFullMode.Wait</c>, so
/// these calls can still await if the channel is momentarily full — records are never dropped
/// (section 10.2: "Drop policy: none for decision/candidate events").
/// </summary>
public interface IOperationalDiagnosticsWriter
{
    ValueTask EnqueueEvaluationAsync(AgentEvaluationRecord record, CancellationToken cancellationToken);

    ValueTask EnqueueCandidateAsync(TradeCandidateRecord record, CancellationToken cancellationToken);

    ValueTask EnqueueStageEventAsync(CandidateStageEventRecord record, CancellationToken cancellationToken);
}

public sealed record OperationalBatchWriterMetrics
{
    public required int QueueDepth { get; init; }
    public required int QueueCapacity { get; init; }
    public required long Enqueued { get; init; }
    public required long BatchesWritten { get; init; }
    public required long RecordsWritten { get; init; }
    public required long RecordsFailedPermanently { get; init; }
    public required TimeSpan LastBatchLatency { get; init; }
}

public interface IOperationalBatchWriterDiagnostics
{
    OperationalBatchWriterMetrics Metrics { get; }
}
