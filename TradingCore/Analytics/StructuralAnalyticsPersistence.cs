using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using ChartAnnotator.Confluence;
using ChartAnnotator.Engine;
using ChartAnnotator.Liquidity;
using ChartAnnotator.Models;
using ChartAnnotator.SupplyDemand;

namespace TradingCore.Analytics;

public enum StructuralAnalyticsEntity
{
    SupplyDemandZone,
    SupplyDemandZoneEvent,
    ZoneCandidateRelationship,
    ZoneTradeRelationship,
    ZoneOutcome,
    LiquidityPool,
    LiquidityPoolEvent,
    LiquiditySweep,
    LiquidityCandidateRelationship,
    LiquidityTradeRelationship,
    LiquidityOutcome
}

/// <summary>
/// Provider-neutral persistence envelope. <see cref="PayloadJson"/> retains the complete immutable
/// domain contract while the indexed columns support causal/replay and attribution queries.
/// </summary>
public sealed record StructuralAnalyticsRecord(
    StructuralAnalyticsEntity Entity,
    Guid StableId,
    string Instrument,
    string Interval,
    string ProfileHash,
    DateTimeOffset OriginatedAt,
    DateTimeOffset ConfirmedAt,
    DateTimeOffset AvailableAt,
    long SnapshotVersion,
    string State,
    string PayloadJson)
{
    public Guid? RelatedEntityId { get; init; }
    public Guid? CandidateId { get; init; }
    public Guid? TradeId { get; init; }
    public DateTimeOffset RecordedAt { get; init; } = DateTimeOffset.UtcNow;
}

public interface IStructuralAnalyticsStore
{
    ValueTask WriteBatchAsync(
        IReadOnlyList<StructuralAnalyticsRecord> records,
        CancellationToken cancellationToken);
}

public sealed record StructuralAnalyticsWriterOptions
{
    public int Capacity { get; init; } = 4096;
    public int MaximumBatchSize { get; init; } = 256;
    public int MaximumWriteAttempts { get; init; } = 3;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    public void Validate()
    {
        if (Capacity < 1 || MaximumBatchSize < 1 || MaximumBatchSize > Capacity ||
            MaximumWriteAttempts < 1 || RetryDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(StructuralAnalyticsWriterOptions));
    }
}

/// <summary>
/// One bounded, single-reader async writer. Producers never perform a database call and may use
/// <see cref="TryEnqueue(StructuralAnalyticsRecord)"/> when the chronological analysis path must remain non-blocking.
/// </summary>
public sealed class StructuralAnalyticsWriter : IAsyncDisposable
{
    private readonly IStructuralAnalyticsStore _store;
    private readonly StructuralAnalyticsWriterOptions _options;
    private readonly Channel<StructuralAnalyticsRecord> _channel;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private long _accepted;
    private long _dropped;
    private long _persisted;
    private long _failed;

    public StructuralAnalyticsWriter(
        IStructuralAnalyticsStore store,
        StructuralAnalyticsWriterOptions? options = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? new StructuralAnalyticsWriterOptions();
        _options.Validate();
        _channel = Channel.CreateBounded<StructuralAnalyticsRecord>(new BoundedChannelOptions(_options.Capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            // Wait mode makes TryWrite return false when the bounded queue is full. The
            // producer still never waits, and the drop is observable in DroppedCount.
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _worker = Task.Run(ConsumeAsync);
    }

    public long AcceptedCount => Interlocked.Read(ref _accepted);
    public long DroppedCount => Interlocked.Read(ref _dropped);
    public long PersistedCount => Interlocked.Read(ref _persisted);
    public long FailedCount => Interlocked.Read(ref _failed);
    public Exception? LastFailure { get; private set; }
    public Task Completion => _worker;

    public bool TryEnqueue(StructuralAnalyticsRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (_channel.Writer.TryWrite(record))
        {
            Interlocked.Increment(ref _accepted);
            return true;
        }

        Interlocked.Increment(ref _dropped);
        return false;
    }

    public int TryEnqueue(AnalysisSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        int accepted = 0;
        foreach (StructuralAnalyticsRecord record in StructuralAnalyticsRecordFactory.FromSnapshot(snapshot))
            accepted += TryEnqueue(record) ? 1 : 0;
        return accepted;
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        finally
        {
            _shutdown.Cancel();
            _shutdown.Dispose();
        }
    }

    private async Task ConsumeAsync()
    {
        var batch = new List<StructuralAnalyticsRecord>(_options.MaximumBatchSize);
        await foreach (StructuralAnalyticsRecord record in _channel.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
        {
            batch.Add(record);
            while (batch.Count < _options.MaximumBatchSize && _channel.Reader.TryRead(out StructuralAnalyticsRecord? next))
                batch.Add(next);

            await WriteWithRetryAsync(batch).ConfigureAwait(false);
            batch.Clear();
        }
    }

    private async Task WriteWithRetryAsync(IReadOnlyList<StructuralAnalyticsRecord> batch)
    {
        for (int attempt = 1; attempt <= _options.MaximumWriteAttempts; attempt++)
        {
            try
            {
                await _store.WriteBatchAsync(batch, _shutdown.Token).ConfigureAwait(false);
                Interlocked.Add(ref _persisted, batch.Count);
                return;
            }
            catch (Exception exception) when (attempt < _options.MaximumWriteAttempts && !_shutdown.IsCancellationRequested)
            {
                LastFailure = exception;
                await Task.Delay(_options.RetryDelay, _shutdown.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (!_shutdown.IsCancellationRequested)
            {
                LastFailure = exception;
                Interlocked.Add(ref _failed, batch.Count);
                return;
            }
        }
    }
}

/// <summary>
/// PostgreSQL implementation intended to receive an <c>NpgsqlDataSource</c> through its standard
/// <see cref="DbDataSource"/> base type. Every batch owns its connection and transaction; no
/// shared context or synchronous database call is used.
/// </summary>
public sealed class PostgresStructuralAnalyticsStore(DbDataSource dataSource) : IStructuralAnalyticsStore
{
    private static readonly IReadOnlyDictionary<StructuralAnalyticsEntity, string> Tables =
        new Dictionary<StructuralAnalyticsEntity, string>
        {
            [StructuralAnalyticsEntity.SupplyDemandZone] = "analytics.supply_demand_zones",
            [StructuralAnalyticsEntity.SupplyDemandZoneEvent] = "analytics.supply_demand_zone_events",
            [StructuralAnalyticsEntity.ZoneCandidateRelationship] = "analytics.zone_candidate_relationships",
            [StructuralAnalyticsEntity.ZoneTradeRelationship] = "analytics.zone_trade_relationships",
            [StructuralAnalyticsEntity.ZoneOutcome] = "analytics.zone_outcomes",
            [StructuralAnalyticsEntity.LiquidityPool] = "analytics.liquidity_pools",
            [StructuralAnalyticsEntity.LiquidityPoolEvent] = "analytics.liquidity_pool_events",
            [StructuralAnalyticsEntity.LiquiditySweep] = "analytics.liquidity_sweeps",
            [StructuralAnalyticsEntity.LiquidityCandidateRelationship] = "analytics.liquidity_candidate_relationships",
            [StructuralAnalyticsEntity.LiquidityTradeRelationship] = "analytics.liquidity_trade_relationships",
            [StructuralAnalyticsEntity.LiquidityOutcome] = "analytics.liquidity_outcomes"
        };

    public async ValueTask WriteBatchAsync(
        IReadOnlyList<StructuralAnalyticsRecord> records,
        CancellationToken cancellationToken)
    {
        if (records.Count == 0) return;
        await using DbConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (StructuralAnalyticsRecord record in records)
        {
            string table = Tables[record.Entity];
            await using DbCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                INSERT INTO {table}
                    (stable_id, instrument, interval_name, profile_hash, originated_at, confirmed_at,
                     available_at, snapshot_version, state, related_entity_id, candidate_id, trade_id,
                     payload, recorded_at)
                VALUES
                    (@stable_id, @instrument, @interval_name, @profile_hash, @originated_at, @confirmed_at,
                     @available_at, @snapshot_version, @state, @related_entity_id, @candidate_id, @trade_id,
                     CAST(@payload AS jsonb), @recorded_at)
                ON CONFLICT (stable_id, snapshot_version) DO UPDATE SET
                    state = EXCLUDED.state,
                    related_entity_id = EXCLUDED.related_entity_id,
                    candidate_id = EXCLUDED.candidate_id,
                    trade_id = EXCLUDED.trade_id,
                    payload = EXCLUDED.payload,
                    recorded_at = EXCLUDED.recorded_at;
                """;
            AddParameter(command, "stable_id", record.StableId);
            AddParameter(command, "instrument", record.Instrument);
            AddParameter(command, "interval_name", record.Interval);
            AddParameter(command, "profile_hash", record.ProfileHash);
            AddParameter(command, "originated_at", record.OriginatedAt);
            AddParameter(command, "confirmed_at", record.ConfirmedAt);
            AddParameter(command, "available_at", record.AvailableAt);
            AddParameter(command, "snapshot_version", record.SnapshotVersion);
            AddParameter(command, "state", record.State);
            AddParameter(command, "related_entity_id", record.RelatedEntityId);
            AddParameter(command, "candidate_id", record.CandidateId);
            AddParameter(command, "trade_id", record.TradeId);
            AddParameter(command, "payload", record.PayloadJson);
            AddParameter(command, "recorded_at", record.RecordedAt);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}

public static class StructuralAnalyticsRecordFactory
{
    public static IEnumerable<StructuralAnalyticsRecord> FromSnapshot(AnalysisSnapshot snapshot)
    {
        foreach (var zone in snapshot.SupplyDemand.Zones)
        {
            yield return Create(
                StructuralAnalyticsEntity.SupplyDemandZone, zone.ZoneId, snapshot, zone.ProfileHash,
                zone.BaseStartedAt, zone.ConfirmedAt, zone.AvailableAt, zone.State.ToString(),
                JsonSerializer.Serialize(zone, StructuralAnalyticsJsonContext.Default.SupplyDemandZone));
        }

        foreach (var zoneEvent in snapshot.SupplyDemand.RecentEvents)
        {
            yield return Create(
                StructuralAnalyticsEntity.SupplyDemandZoneEvent, zoneEvent.EventId, snapshot,
                snapshot.SupplyDemand.ProfileHash, zoneEvent.OccurredAt, zoneEvent.AvailableAt,
                zoneEvent.AvailableAt, zoneEvent.StateAfter.ToString(),
                JsonSerializer.Serialize(zoneEvent, StructuralAnalyticsJsonContext.Default.SupplyDemandZoneEvent)) with
            { RelatedEntityId = zoneEvent.ZoneId };
        }

        foreach (var pool in snapshot.Liquidity.Pools)
        {
            yield return Create(
                StructuralAnalyticsEntity.LiquidityPool, pool.PoolId, snapshot, pool.ProfileHash,
                pool.OriginatedAt, pool.ConfirmedAt, pool.AvailableAt, pool.State.ToString(),
                JsonSerializer.Serialize(pool, StructuralAnalyticsJsonContext.Default.LiquidityPool));
        }

        foreach (var poolEvent in snapshot.Liquidity.RecentEvents)
        {
            yield return Create(
                StructuralAnalyticsEntity.LiquidityPoolEvent, poolEvent.EventId, snapshot,
                snapshot.Liquidity.ProfileHash, poolEvent.OccurredAt, poolEvent.AvailableAt,
                poolEvent.AvailableAt, poolEvent.StateAfter.ToString(),
                JsonSerializer.Serialize(poolEvent, StructuralAnalyticsJsonContext.Default.LiquidityEvent)) with
            { RelatedEntityId = poolEvent.PoolId };
        }

        foreach (var sweep in snapshot.Liquidity.RecentSweeps)
        {
            yield return Create(
                StructuralAnalyticsEntity.LiquiditySweep, sweep.SweepId, snapshot,
                snapshot.Liquidity.ProfileHash, sweep.SweepStartedAt, sweep.ConfirmedAt,
                sweep.AvailableAt, "Sweep",
                JsonSerializer.Serialize(sweep, StructuralAnalyticsJsonContext.Default.LiquiditySweepEvent)) with
            { RelatedEntityId = sweep.PoolId };
        }

        foreach (SupplyDemandLiquidityConfluence relationship in snapshot.SupplyDemandLiquidityConfluence.Relationships)
        {
            yield return Create(
                StructuralAnalyticsEntity.ZoneCandidateRelationship, relationship.ConfluenceId, snapshot,
                snapshot.SupplyDemand.ProfileHash, relationship.AvailableAt, relationship.AvailableAt,
                relationship.AvailableAt, relationship.Direction.ToString(),
                JsonSerializer.Serialize(
                    relationship,
                    StructuralAnalyticsJsonContext.Default.SupplyDemandLiquidityConfluence)) with
            { RelatedEntityId = relationship.ZoneId };
        }
    }

    public static StructuralAnalyticsRecord Relationship(
        StructuralAnalyticsEntity entity,
        Guid stableId,
        AnalysisSnapshot snapshot,
        Guid relatedEntityId,
        Guid? candidateId,
        Guid? tradeId,
        string state,
        string payloadJson)
    {
        if (entity is not (StructuralAnalyticsEntity.ZoneCandidateRelationship or
            StructuralAnalyticsEntity.ZoneTradeRelationship or StructuralAnalyticsEntity.ZoneOutcome or
            StructuralAnalyticsEntity.LiquidityCandidateRelationship or
            StructuralAnalyticsEntity.LiquidityTradeRelationship or StructuralAnalyticsEntity.LiquidityOutcome))
            throw new ArgumentOutOfRangeException(nameof(entity));

        return Create(entity, stableId, snapshot, string.Empty, snapshot.AvailableAt,
            snapshot.AvailableAt, snapshot.AvailableAt, state, payloadJson) with
        {
            RelatedEntityId = relatedEntityId,
            CandidateId = candidateId,
            TradeId = tradeId
        };
    }

    private static StructuralAnalyticsRecord Create(
        StructuralAnalyticsEntity entity,
        Guid id,
        AnalysisSnapshot snapshot,
        string profileHash,
        DateTimeOffset originatedAt,
        DateTimeOffset confirmedAt,
        DateTimeOffset availableAt,
        string state,
        string payloadJson) => new(
            entity,
            id,
            snapshot.Instrument.ToString(),
            snapshot.Interval.ToString(),
            profileHash,
            originatedAt,
            confirmedAt,
            availableAt,
            snapshot.Version,
            state,
            payloadJson);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SupplyDemandZone))]
[JsonSerializable(typeof(SupplyDemandZoneEvent))]
[JsonSerializable(typeof(LiquidityPool))]
[JsonSerializable(typeof(LiquidityEvent))]
[JsonSerializable(typeof(LiquiditySweepEvent))]
[JsonSerializable(typeof(SupplyDemandLiquidityConfluence))]
internal sealed partial class StructuralAnalyticsJsonContext : JsonSerializerContext
{
}
