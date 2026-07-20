using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DBManager.Abstractions.Management;
using DBManager.Postgres;
using DBManager.Postgres.Operations;
using LiveTrading.Persistence;
using Microsoft.EntityFrameworkCore;

namespace TradingHub.Persistence.Postgres.Live;

/// <summary>
/// Durable live event journal and engine checkpoint store. Call completion means PostgreSQL has
/// committed the write; failures propagate so the live safety layer can pause entries.
/// </summary>
public sealed class PostgresLiveTradingPersistence(
    IDbContextFactory<TradingHubDbContext> contextFactory,
    TimeProvider timeProvider,
    Guid deploymentId) : ILiveTradingPersistence, ILiveTradingPersistenceDiagnostics
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private long _enqueued;
    private long _completed;
    private long _failed;

    public LivePersistenceMetrics Metrics => new(
        int.MaxValue,
        0,
        Interlocked.Read(ref _enqueued),
        Interlocked.Read(ref _completed),
        Interlocked.Read(ref _failed));

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // All writes are committed inline, so there is no shutdown queue to drain.
        }
    }

    public async ValueTask AppendAsync<T>(
        string streamName,
        T payload,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamName);
        ArgumentNullException.ThrowIfNull(payload);
        Interlocked.Increment(ref _enqueued);
        try
        {
            string json = JsonSerializer.Serialize(payload, JsonOptions);
            await using TradingHubDbContext context = await contextFactory
                .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            context.LiveEventJournal.Add(new LiveEventJournalEntity
            {
                EventId = Guid.NewGuid(),
                DeploymentId = deploymentId,
                StreamName = Sanitize(streamName),
                PayloadType = payload.GetType().FullName ?? typeof(T).Name,
                PayloadJson = json,
                PayloadHash = Hash(json),
                OccurredAt = timeProvider.GetUtcNow()
            });
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _completed);
        }
        catch
        {
            Interlocked.Increment(ref _failed);
            throw;
        }
    }

    public async ValueTask SaveCheckpointAsync(
        LiveEngineCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        Interlocked.Increment(ref _enqueued);
        try
        {
            string json = JsonSerializer.Serialize(checkpoint, JsonOptions);
            await using TradingHubDbContext context = await contextFactory
                .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            long latestSequence = await context.Checkpoints.AsNoTracking()
                .Where(row => row.DeploymentId == deploymentId && row.CheckpointType == CheckpointType.EngineState)
                .Select(row => (long?)row.Sequence)
                .MaxAsync(cancellationToken).ConfigureAwait(false) ?? 0L;
            context.Checkpoints.Add(new CheckpointEntity
            {
                CheckpointId = Guid.NewGuid(),
                DeploymentId = deploymentId,
                CheckpointType = CheckpointType.EngineState,
                Sequence = latestSequence + 1,
                ContentHash = Hash(json),
                PayloadJson = json,
                CreatedAt = timeProvider.GetUtcNow()
            });
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _completed);
        }
        catch
        {
            Interlocked.Increment(ref _failed);
            throw;
        }
    }

    public async Task<LiveEngineCheckpoint?> LoadCheckpointAsync(CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        CheckpointEntity? row = await context.Checkpoints.AsNoTracking()
            .Where(item => item.DeploymentId == deploymentId && item.CheckpointType == CheckpointType.EngineState)
            .OrderByDescending(item => item.Sequence)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (row is null) return null;
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(row.ContentHash),
                Encoding.ASCII.GetBytes(Hash(row.PayloadJson))))
            throw new InvalidDataException("The latest live-engine checkpoint failed its content-hash check.");
        return JsonSerializer.Deserialize<LiveEngineCheckpoint>(row.PayloadJson, JsonOptions)
               ?? throw new InvalidDataException("The latest live-engine checkpoint is empty.");
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Sanitize(string value)
    {
        string sanitized = new(value.Where(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_').ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "events" : sanitized;
    }
}
