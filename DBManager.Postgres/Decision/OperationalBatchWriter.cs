using System.Threading.Channels;
using DBManager.Abstractions;
using DBManager.Abstractions.Decision;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace DBManager.Postgres.Decision;

public sealed class OperationalBatchWriterOptions
{
    /// <summary>Section 10.2: 50,000-100,000 records, Wait (never drop decision/candidate events).</summary>
    public int Capacity { get; init; } = 75_000;
    public int MaxBatchSize { get; init; } = 250;
    public TimeSpan MaxBatchDelay { get; init; } = TimeSpan.FromMilliseconds(25);
    public int MaxRetryAttempts { get; init; } = 3;
}

/// <summary>
/// Lane B operational diagnostics: bounded-channel producer + background batch writer
/// (section 9.2 Lane B, section 16). Writes through the <see cref="PersistenceLane.Critical"/>
/// data source because only <c>trading_runtime</c> has write access to the <c>decision</c> schema
/// (section 25.1) — config-schema writes use the migrator connection instead (see
/// <c>PolicyConfigurationStore</c>), but decision-schema writes are the live host's own job.
/// Uses raw Npgsql batching per section 14.3's explicit guidance for high-volume append-only
/// writes, not EF Core.
/// </summary>
public sealed class OperationalBatchWriter : IOperationalDiagnosticsWriter, IOperationalBatchWriterDiagnostics
{
    private readonly Channel<OperationalRecord> _channel;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<OperationalBatchWriter> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly OperationalBatchWriterOptions _options;

    private long _enqueued;
    private long _batchesWritten;
    private long _recordsWritten;
    private long _recordsFailedPermanently;
    private TimeSpan _lastBatchLatency;

    public OperationalBatchWriter(
        [FromKeyedServices(PersistenceLane.Critical)] NpgsqlDataSource dataSource,
        ILogger<OperationalBatchWriter> logger,
        TimeProvider timeProvider,
        OperationalBatchWriterOptions? options = null)
    {
        _dataSource = dataSource;
        _logger = logger;
        _timeProvider = timeProvider;
        _options = options ?? new OperationalBatchWriterOptions();
        _channel = Channel.CreateBounded<OperationalRecord>(new BoundedChannelOptions(_options.Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    public OperationalBatchWriterMetrics Metrics => new()
    {
        QueueDepth = _channel.Reader.Count,
        QueueCapacity = _options.Capacity,
        Enqueued = Interlocked.Read(ref _enqueued),
        BatchesWritten = Interlocked.Read(ref _batchesWritten),
        RecordsWritten = Interlocked.Read(ref _recordsWritten),
        RecordsFailedPermanently = Interlocked.Read(ref _recordsFailedPermanently),
        LastBatchLatency = _lastBatchLatency
    };

    public ValueTask EnqueueEvaluationAsync(AgentEvaluationRecord record, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _enqueued);
        return _channel.Writer.WriteAsync(new EvaluationRecordEnvelope(record), cancellationToken);
    }

    public ValueTask EnqueueCandidateAsync(TradeCandidateRecord record, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _enqueued);
        return _channel.Writer.WriteAsync(new CandidateRecordEnvelope(record), cancellationToken);
    }

    public ValueTask EnqueueStageEventAsync(CandidateStageEventRecord record, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _enqueued);
        return _channel.Writer.WriteAsync(new StageEventRecordEnvelope(record), cancellationToken);
    }

    /// <summary>Drains the channel and flushes bounded batches. Call from a hosted-service wrapper.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        List<OperationalRecord> batch = new(_options.MaxBatchSize);
        using PeriodicTimer timer = new(_options.MaxBatchDelay);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                batch.Clear();
                while (batch.Count < _options.MaxBatchSize && _channel.Reader.TryRead(out OperationalRecord? record))
                    batch.Add(record);

                if (batch.Count == 0)
                {
                    Task readTask = _channel.Reader.WaitToReadAsync(cancellationToken).AsTask();
                    Task timerTask = timer.WaitForNextTickAsync(cancellationToken).AsTask();
                    await Task.WhenAny(readTask, timerTask).ConfigureAwait(false);
                    continue;
                }

                await WriteBatchWithRetryAsync(batch, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            // Drain and flush whatever remains so a graceful shutdown doesn't drop diagnostics.
            List<OperationalRecord> remaining = new();
            while (_channel.Reader.TryRead(out OperationalRecord? record))
                remaining.Add(record);
            if (remaining.Count > 0)
                await WriteBatchWithRetryAsync(remaining, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task WriteBatchWithRetryAsync(List<OperationalRecord> batch, CancellationToken cancellationToken)
    {
        DateTimeOffset start = _timeProvider.GetUtcNow();
        for (int attempt = 1; attempt <= _options.MaxRetryAttempts; attempt++)
        {
            try
            {
                await WriteBatchAsync(batch, cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _batchesWritten);
                Interlocked.Add(ref _recordsWritten, batch.Count);
                _lastBatchLatency = _timeProvider.GetUtcNow() - start;
                return;
            }
            catch (Exception ex) when (attempt < _options.MaxRetryAttempts)
            {
                TimeSpan delay = TimeSpan.FromMilliseconds(50 * attempt + Random.Shared.Next(0, 50));
                _logger.LogWarning(ex,
                    "Operational batch write failed (attempt {Attempt}/{MaxAttempts}), retrying in {Delay}.",
                    attempt, _options.MaxRetryAttempts, delay);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Operational batch write failed permanently after {MaxAttempts} attempts; {Count} diagnostic records lost.",
                    _options.MaxRetryAttempts, batch.Count);
                Interlocked.Add(ref _recordsFailedPermanently, batch.Count);
                return;
            }
        }
    }

    private async Task WriteBatchAsync(List<OperationalRecord> batch, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlBatch npgsqlBatch = new(connection, transaction);

        foreach (OperationalRecord record in batch)
        {
            switch (record)
            {
                case EvaluationRecordEnvelope evaluation:
                    AddDecisionKeyCommand(npgsqlBatch, evaluation.Record.DecisionId,
                        evaluation.Record.DecisionTime, evaluation.Record.DeploymentId);
                    AddEvaluationCommand(npgsqlBatch, evaluation.Record);
                    break;
                case CandidateRecordEnvelope candidate:
                    AddCandidateKeyCommand(npgsqlBatch, candidate.Record.CandidateId,
                        candidate.Record.DecisionId, candidate.Record.DecisionTime);
                    AddCandidateCommand(npgsqlBatch, candidate.Record);
                    break;
                case StageEventRecordEnvelope stageEvent:
                    AddStageEventCommand(npgsqlBatch, stageEvent.Record);
                    break;
            }
        }

        await npgsqlBatch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddDecisionKeyCommand(
        NpgsqlBatch batch, Guid decisionId, DateTimeOffset decisionTime, Guid deploymentId)
    {
        NpgsqlBatchCommand command = new(
            "INSERT INTO decision.decision_keys (decision_id, decision_time, deployment_id) " +
            "VALUES ($1, $2, $3) ON CONFLICT (decision_id) DO NOTHING");
        command.Parameters.Add(new NpgsqlParameter { Value = decisionId });
        command.Parameters.Add(new NpgsqlParameter { Value = decisionTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        command.Parameters.Add(new NpgsqlParameter { Value = deploymentId });
        batch.BatchCommands.Add(command);
    }

    private static void AddEvaluationCommand(NpgsqlBatch batch, AgentEvaluationRecord record)
    {
        NpgsqlBatchCommand command = new(
            """
            INSERT INTO decision.agent_evaluations
                (decision_id, deployment_id, policy_revision_id, instrument_id, strategy_id,
                 decision_time, received_at, action, state_before, state_after, trigger_interval,
                 snapshot_version, raw_confidence, mtf_alignment, regime, primary_reason_code,
                 diagnostics, persisted_at)
            VALUES
                ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17::jsonb, $18)
            ON CONFLICT (decision_id) DO NOTHING
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = record.DecisionId });
        command.Parameters.Add(new NpgsqlParameter { Value = record.DeploymentId });
        command.Parameters.Add(new NpgsqlParameter { Value = record.PolicyRevisionId });
        command.Parameters.Add(new NpgsqlParameter { Value = record.InstrumentId });
        command.Parameters.Add(new NpgsqlParameter { Value = record.StrategyId });
        command.Parameters.Add(new NpgsqlParameter { Value = record.DecisionTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        command.Parameters.Add(new NpgsqlParameter { Value = record.ReceivedAt, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        command.Parameters.Add(new NpgsqlParameter { Value = (short)record.Action });
        command.Parameters.Add(new NpgsqlParameter { Value = (short)record.StateBefore });
        command.Parameters.Add(new NpgsqlParameter { Value = (short)record.StateAfter });
        command.Parameters.Add(new NpgsqlParameter { Value = (short)record.TriggerInterval });
        command.Parameters.Add(new NpgsqlParameter { Value = record.SnapshotVersion });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)record.RawConfidence ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)record.MtfAlignment ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = record.Regime is null ? DBNull.Value : (short)record.Regime.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = record.PrimaryReasonCode });
        command.Parameters.Add(new NpgsqlParameter { Value = record.DiagnosticsJson });
        command.Parameters.Add(new NpgsqlParameter { Value = DateTimeOffset.UtcNow, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        batch.BatchCommands.Add(command);
    }

    private static void AddCandidateKeyCommand(
        NpgsqlBatch batch, Guid candidateId, Guid decisionId, DateTimeOffset candidateTime)
    {
        NpgsqlBatchCommand command = new(
            "INSERT INTO decision.candidate_keys (candidate_id, decision_id, candidate_time, current_status) " +
            "VALUES ($1, $2, $3, $4) ON CONFLICT (candidate_id) DO NOTHING");
        command.Parameters.Add(new NpgsqlParameter { Value = candidateId });
        command.Parameters.Add(new NpgsqlParameter { Value = decisionId });
        command.Parameters.Add(new NpgsqlParameter { Value = candidateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        command.Parameters.Add(new NpgsqlParameter { Value = (short)CandidateStatus.Open });
        batch.BatchCommands.Add(command);
    }

    private static void AddCandidateCommand(NpgsqlBatch batch, TradeCandidateRecord record)
    {
        NpgsqlBatchCommand command = new(
            """
            INSERT INTO decision.trade_candidates
                (candidate_id, decision_id, setup_id, deployment_id, policy_revision_id, instrument_id,
                 strategy_id, direction, decision_time, reference_bid, reference_ask, reference_mid,
                 stop_price, target_price, raw_confidence, mtf_alignment, regime,
                 setup_calibration_audit, meta_label_audit, trading_condition_audit,
                 status, expires_at, created_at)
            VALUES
                ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17,
                 $18::jsonb, $19::jsonb, $20::jsonb, $21, $22, $23)
            ON CONFLICT (candidate_id) DO NOTHING
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = record.CandidateId });
        command.Parameters.Add(new NpgsqlParameter { Value = record.DecisionId });
        command.Parameters.Add(new NpgsqlParameter { Value = record.SetupId });
        command.Parameters.Add(new NpgsqlParameter { Value = record.DeploymentId });
        command.Parameters.Add(new NpgsqlParameter { Value = record.PolicyRevisionId });
        command.Parameters.Add(new NpgsqlParameter { Value = record.InstrumentId });
        command.Parameters.Add(new NpgsqlParameter { Value = record.StrategyId });
        command.Parameters.Add(new NpgsqlParameter { Value = (short)record.Direction });
        command.Parameters.Add(new NpgsqlParameter { Value = record.DecisionTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)record.ReferenceBid ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)record.ReferenceAsk ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = record.ReferenceMid });
        command.Parameters.Add(new NpgsqlParameter { Value = record.StopPrice });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)record.TargetPrice ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = record.RawConfidence });
        command.Parameters.Add(new NpgsqlParameter { Value = record.MtfAlignment });
        command.Parameters.Add(new NpgsqlParameter { Value = (short)record.Regime });
        command.Parameters.Add(new NpgsqlParameter { Value = record.SetupCalibrationAuditJson });
        command.Parameters.Add(new NpgsqlParameter { Value = record.MetaLabelAuditJson });
        command.Parameters.Add(new NpgsqlParameter { Value = record.TradingConditionAuditJson });
        command.Parameters.Add(new NpgsqlParameter { Value = (short)CandidateStatus.Open });
        command.Parameters.Add(new NpgsqlParameter { Value = record.ExpiresAt, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        command.Parameters.Add(new NpgsqlParameter { Value = DateTimeOffset.UtcNow, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        batch.BatchCommands.Add(command);
    }

    private static void AddStageEventCommand(NpgsqlBatch batch, CandidateStageEventRecord record)
    {
        NpgsqlBatchCommand command = new(
            """
            INSERT INTO decision.candidate_stage_events
                (candidate_id, occurred_at, stage, outcome, reason_code, risk_multiplier,
                 confidence_before, confidence_after, details)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9::jsonb)
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = record.CandidateId });
        command.Parameters.Add(new NpgsqlParameter { Value = record.OccurredAt, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        command.Parameters.Add(new NpgsqlParameter { Value = (short)record.Stage });
        command.Parameters.Add(new NpgsqlParameter { Value = (short)record.Outcome });
        command.Parameters.Add(new NpgsqlParameter { Value = record.ReasonCode });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)record.RiskMultiplier ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)record.ConfidenceBefore ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)record.ConfidenceAfter ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = record.DetailsJson });
        batch.BatchCommands.Add(command);
    }

    private abstract record OperationalRecord;

    private sealed record EvaluationRecordEnvelope(AgentEvaluationRecord Record) : OperationalRecord;

    private sealed record CandidateRecordEnvelope(TradeCandidateRecord Record) : OperationalRecord;

    private sealed record StageEventRecordEnvelope(CandidateStageEventRecord Record) : OperationalRecord;
}
