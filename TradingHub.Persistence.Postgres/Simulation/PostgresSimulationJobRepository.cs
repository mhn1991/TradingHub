using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DBManager.Postgres;
using DBManager.Postgres.Simulation;
using Microsoft.EntityFrameworkCore;
using Simulator.Jobs;
using Simulator.Models;

namespace TradingHub.Persistence.Postgres.Simulation;

/// <summary>
/// PostgreSQL-owned simulation job projection. Progress is overwritten in-place while lifecycle
/// transitions remain append-only, avoiding a database event for every replay candle.
/// </summary>
public sealed class PostgresSimulationJobRepository(
    IDbContextFactory<TradingHubDbContext> contextFactory,
    TimeProvider timeProvider) : ISimulationJobRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public SimulationJobRecoveryReport LastRecoveryReport { get; private set; } = new();

    public async Task SaveAsync(SimulationJobSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            await using TradingHubDbContext context = await contextFactory
                .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await context.Database
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            SimulationJobEntity? job = await context.SimulationJobs
                .SingleOrDefaultAsync(row => row.SimulationId == snapshot.Id, cancellationToken)
                .ConfigureAwait(false);
            if (job is not null && snapshot.Revision <= job.Revision)
                return;

            short previousStatus = job?.Status ?? -1;
            string snapshotJson = JsonSerializer.Serialize(snapshot, JsonOptions);
            string resolvedConfigurationJson = JsonSerializer.Serialize(snapshot.Request, JsonOptions);
            string configurationHash = CalculateConfigurationHash(snapshot, resolvedConfigurationJson);

            if (job is null)
            {
                job = new SimulationJobEntity
                {
                    SimulationId = snapshot.Id,
                    JobKind = 0,
                    Status = (short)snapshot.Status,
                    Phase = (short)snapshot.Status,
                    Revision = snapshot.Revision,
                    RequestedAt = snapshot.CreatedAt,
                    ConfigurationHash = configurationHash,
                    ResolvedConfigurationJson = resolvedConfigurationJson,
                    SnapshotJson = snapshotJson
                };
                context.SimulationJobs.Add(job);
            }

            job.Status = (short)snapshot.Status;
            job.Phase = (short)snapshot.Status;
            job.Revision = snapshot.Revision;
            job.StartedAt = snapshot.StartedAt;
            job.CompletedAt = snapshot.CompletedAt;
            job.ConfigurationHash = configurationHash;
            job.ResolvedConfigurationJson = resolvedConfigurationJson;
            job.InputRequestId = snapshot.InputRequestId;
            job.InputHash = snapshot.InputHash;
            job.FailureCode = snapshot.Status == SimulationJobStatus.Failed
                ? snapshot.Error?.StartsWith("HostRestartedWhileRunning:", StringComparison.Ordinal) == true
                    ? "HostRestartedWhileRunning"
                    : "simulation_failed"
                : null;
            job.FailureDetail = snapshot.Error;
            job.HeartbeatAt = timeProvider.GetUtcNow();
            job.SnapshotJson = snapshotJson;

            SimulationJobProgressEntity? progress = await context.SimulationJobProgress
                .SingleOrDefaultAsync(row => row.SimulationId == snapshot.Id, cancellationToken)
                .ConfigureAwait(false);
            if (progress is null)
            {
                progress = new SimulationJobProgressEntity { SimulationId = snapshot.Id, CurrentPhase = string.Empty };
                context.SimulationJobProgress.Add(progress);
            }

            progress.MarketTime = snapshot.CurrentMarketTime;
            progress.ProcessedCandles = snapshot.ProcessedBaseCandles;
            progress.ProgressPercent = snapshot.ProgressPercent;
            progress.CandlesPerSecond = snapshot.CandlesPerSecond;
            progress.CurrentPhase = snapshot.Status.ToString();
            progress.UpdatedAt = timeProvider.GetUtcNow();

            if (previousStatus != (short)snapshot.Status)
            {
                context.SimulationJobEvents.Add(new SimulationJobEventEntity
                {
                    SimulationId = snapshot.Id,
                    JobRevision = snapshot.Revision,
                    Status = (short)snapshot.Status,
                    EventType = $"status:{snapshot.Status}",
                    OccurredAt = timeProvider.GetUtcNow(),
                    DetailsJson = JsonSerializer.Serialize(new
                    {
                        snapshot.Status,
                        snapshot.Error,
                        snapshot.IsComplete
                    }, JsonOptions)
                });
            }

            try
            {
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt == 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<SimulationJobSnapshot?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        string? json = await context.SimulationJobs.AsNoTracking()
            .Where(row => row.SimulationId == id)
            .Select(row => row.SnapshotJson)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return json is null ? null : Deserialize(json);
    }

    public async Task<IReadOnlyList<SimulationJobSnapshot>> ListAsync(
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        if (take < 1) throw new ArgumentOutOfRangeException(nameof(take));
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        string[] rows = await context.SimulationJobs.AsNoTracking()
            .OrderByDescending(row => row.RequestedAt)
            .Take(take)
            .Select(row => row.SnapshotJson)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(Deserialize).ToArray();
    }

    public async Task MarkInterruptedJobsAsync(CancellationToken cancellationToken = default)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        short completed = (short)SimulationJobStatus.Completed;
        short failed = (short)SimulationJobStatus.Failed;
        short cancelled = (short)SimulationJobStatus.Cancelled;
        string[] persistedJobs = await context.SimulationJobs.AsNoTracking()
            .Where(row => row.Status != completed && row.Status != failed && row.Status != cancelled)
            .Select(row => row.SnapshotJson)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        SimulationJobSnapshot[] jobs = persistedJobs.Select(Deserialize).ToArray();
        int interrupted = 0;
        foreach (SimulationJobSnapshot job in jobs)
        {
            if (job.IsComplete || job.Status is SimulationJobStatus.Completed or
                SimulationJobStatus.Failed or SimulationJobStatus.Cancelled)
                continue;

            await SaveAsync(job with
            {
                Revision = job.Revision + 1,
                Status = SimulationJobStatus.Failed,
                IsComplete = true,
                CompletedAt = timeProvider.GetUtcNow(),
                Error = "HostRestartedWhileRunning: The host restarted before this simulation " +
                        "could complete. Partial replay output remains available."
            }, cancellationToken).ConfigureAwait(false);
            interrupted++;
        }

        LastRecoveryReport = new SimulationJobRecoveryReport
        {
            ValidJobs = jobs.Length,
            InterruptedJobsMarkedFailed = interrupted
        };
    }

    private static SimulationJobSnapshot Deserialize(string json) =>
        JsonSerializer.Deserialize<SimulationJobSnapshot>(json, JsonOptions)
        ?? throw new InvalidDataException("The persisted simulation snapshot is empty.");

    private static string CalculateConfigurationHash(
        SimulationJobSnapshot snapshot,
        string resolvedConfigurationJson)
    {
        string value = snapshot.SimulationConfigurationId
                       ?? snapshot.InputHash
                       ?? resolvedConfigurationJson;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}
