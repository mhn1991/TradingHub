using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DBManager.Postgres;
using DBManager.Postgres.Simulation;
using Microsoft.EntityFrameworkCore;
using Simulator.Experiments;
using Simulator.Experiments.Models;
using Simulator.Experiments.Persistence;

namespace TradingHub.Persistence.Postgres.Simulation;

public sealed class PostgresSimulationExperimentRepository(
    IDbContextFactory<TradingHubDbContext> contextFactory) : ISimulationExperimentRepository
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task SaveAsync(SimulationExperimentSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string document = JsonSerializer.Serialize(snapshot, Json);
        string configurationHash = CanonicalJsonHash.Compute(document);
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        SimulationExperimentEntity? row = await db.SimulationExperiments.SingleOrDefaultAsync(x => x.ExperimentId == snapshot.Id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            row = new SimulationExperimentEntity
            {
                ExperimentId = snapshot.Id,
                Revision = snapshot.Revision,
                Name = snapshot.Name,
                Status = snapshot.State.ToString(),
                ConfigurationHash = configurationHash,
                ResolvedConfigurationJson = document,
                CreatedAt = snapshot.CreatedAt,
                CreatedBy = "runtime"
            };
            db.SimulationExperiments.Add(row);
        }
        else
        {
            if (snapshot.Revision < row.Revision)
                return;
            if (snapshot.Revision == row.Revision &&
                !string.Equals(CanonicalJsonHash.Compute(row.ResolvedConfigurationJson), configurationHash, StringComparison.Ordinal))
                throw new InvalidOperationException($"Experiment {snapshot.Id:N} revision {snapshot.Revision} is immutable.");
            row.Revision = snapshot.Revision;
            row.Name = snapshot.Name;
            row.Status = snapshot.State.ToString();
            row.ConfigurationHash = configurationHash;
            row.ResolvedConfigurationJson = document;
        }

        await SyncRunsAsync(db, snapshot, cancellationToken).ConfigureAwait(false);
        await SyncComparisonsAsync(db, snapshot, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SimulationExperimentSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty) return null;
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        SimulationExperimentEntity? row = await db.SimulationExperiments.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ExperimentId == id, cancellationToken).ConfigureAwait(false);
        return row is null ? null : Deserialize(row);
    }

    public async Task<IReadOnlyList<SimulationExperimentSnapshot>> ListAsync(int take = 50, CancellationToken cancellationToken = default)
    {
        if (take < 1) throw new ArgumentOutOfRangeException(nameof(take));
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        List<SimulationExperimentEntity> rows = await db.SimulationExperiments.AsNoTracking()
            .OrderByDescending(x => x.CreatedAt).Take(take).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(Deserialize).ToArray();
    }

    public async Task<int> MarkInterruptedAsync(CancellationToken cancellationToken = default)
    {
        List<SimulationExperimentSnapshot> snapshots;
        await using (TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            List<SimulationExperimentEntity> rows = await db.SimulationExperiments
                .OrderByDescending(x => x.CreatedAt).Take(10_000).ToListAsync(cancellationToken).ConfigureAwait(false);
            snapshots = new List<SimulationExperimentSnapshot>(rows.Count);
            foreach (SimulationExperimentEntity row in rows)
            {
                string canonicalHash = CanonicalJsonHash.Compute(row.ResolvedConfigurationJson);
                if (string.Equals(canonicalHash, row.ConfigurationHash, StringComparison.Ordinal))
                {
                    snapshots.Add(Deserialize(row));
                    continue;
                }

                // Versions before canonical hashing computed the digest before PostgreSQL
                // jsonb reordered object keys. Validate the relational envelope and embedded
                // profile hashes once, then migrate the row to the stable hash format.
                SimulationExperimentSnapshot legacy = DeserializeDocument(row);
                ValidateLegacyEnvelope(row, legacy);
                row.ConfigurationHash = canonicalHash;
                snapshots.Add(legacy);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        int count = 0;
        foreach (SimulationExperimentSnapshot snapshot in snapshots.Where(x => !x.IsTerminal))
        {
            await SaveAsync(snapshot with
            {
                Revision = snapshot.Revision + 1,
                State = SimulationExperimentState.Interrupted,
                UpdatedAt = DateTimeOffset.UtcNow,
                Warnings = snapshot.Warnings.Append("InterruptedByServiceRestart").Distinct(StringComparer.Ordinal).ToArray()
            }, cancellationToken).ConfigureAwait(false);
            count++;
        }
        return count;
    }

    private static async Task SyncRunsAsync(TradingHubDbContext db, SimulationExperimentSnapshot snapshot, CancellationToken cancellationToken)
    {
        SimulationExperimentTimeline timeline = snapshot.Manifest.Timeline;
        foreach (SimulationExperimentProfileRun run in snapshot.Manifest.ProfileRuns)
        {
            SimulationProfileRunProgress? progress = snapshot.Profiles.SingleOrDefault(x => x.ProfileRunId == run.ProfileRunId);
            Guid? simulationId = progress?.EvaluationJobId ?? progress?.LearningJobId;
            if (!simulationId.HasValue) continue;
            SimulationExperimentRunEntity? row = await db.SimulationExperimentRuns.SingleOrDefaultAsync(
                x => x.ExperimentRunId == run.ProfileRunId, cancellationToken).ConfigureAwait(false);
            AnalysisWarmupPlan? trainingWarmup = snapshot.Manifest.TrainingWarmups.GetValueOrDefault(run.ProfileRunId);
            AnalysisWarmupPlan? evaluationWarmup = snapshot.Manifest.EvaluationWarmups.GetValueOrDefault(run.ProfileRunId);
            if (row is null)
            {
                row = new SimulationExperimentRunEntity
                {
                    ExperimentRunId = run.ProfileRunId,
                    ExperimentId = snapshot.Id,
                    SimulationId = simulationId.Value,
                    ProfileRevisionId = PostgresSimulationStrategyProfileStore.RevisionId(run.Profile.ProfileId, run.Profile.Revision)
                };
                db.SimulationExperimentRuns.Add(row);
            }
            row.SimulationId = simulationId.Value;
            row.VariantId = run.BaselineProfileRunId;
            row.SharedAnalysisGroupId = null;
            row.AnalysisWarmupFrom = trainingWarmup?.StreamFrom ?? timeline.ResolveTrainingStreamFrom();
            row.LearningFrom = timeline.LearningFrom;
            row.LearningTo = timeline.LearningTo;
            row.EmbargoFrom = timeline.LearningTo;
            row.EmbargoTo = timeline.EvaluationFrom;
            row.EvaluationWarmupFrom = evaluationWarmup?.StreamFrom ?? timeline.ResolveEvaluationStreamFrom();
            row.EvaluationFrom = timeline.EvaluationFrom;
            row.EvaluationTo = timeline.EvaluationTo;
        }
    }

    private static async Task SyncComparisonsAsync(
        TradingHubDbContext db,
        SimulationExperimentSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot.Comparison?.BaselineProfileRunId is not { } baselineRunId)
            return;
        Guid? baselineSimulationId = snapshot.Profiles.SingleOrDefault(x => x.ProfileRunId == baselineRunId)?.EvaluationJobId;
        if (baselineSimulationId is null)
            return;

        foreach (SimulationExperimentComparisonRow comparison in snapshot.Comparison.Profiles
                     .Where(x => x.ProfileRunId != baselineRunId))
        {
            Guid? candidateSimulationId = snapshot.Profiles
                .SingleOrDefault(x => x.ProfileRunId == comparison.ProfileRunId)?.EvaluationJobId;
            if (candidateSimulationId is null)
                continue;
            Guid comparisonId = StableGuid($"{snapshot.Id:N}|{baselineRunId:N}|{comparison.ProfileRunId:N}");
            string metrics = JsonSerializer.Serialize(comparison, Json);
            SimulationExperimentComparisonEntity? row = await db.SimulationExperimentComparisons
                .SingleOrDefaultAsync(x => x.ComparisonId == comparisonId, cancellationToken).ConfigureAwait(false);
            if (row is not null)
            {
                if (!string.Equals(row.MetricsJson, metrics, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Experiment comparison {comparisonId:N} is immutable.");
                continue;
            }
            db.SimulationExperimentComparisons.Add(new SimulationExperimentComparisonEntity
            {
                ComparisonId = comparisonId,
                ExperimentId = snapshot.Id,
                BaselineSimulationId = baselineSimulationId.Value,
                CandidateSimulationId = candidateSimulationId.Value,
                MetricsJson = metrics,
                CreatedAt = snapshot.CompletedAt ?? snapshot.UpdatedAt ?? snapshot.CreatedAt
            });
        }
    }

    private static SimulationExperimentSnapshot Deserialize(SimulationExperimentEntity row)
    {
        if (!string.Equals(CanonicalJsonHash.Compute(row.ResolvedConfigurationJson), row.ConfigurationHash, StringComparison.Ordinal))
            throw new InvalidOperationException($"Experiment {row.ExperimentId:N} failed hash verification.");
        return DeserializeDocument(row);
    }

    private static SimulationExperimentSnapshot DeserializeDocument(SimulationExperimentEntity row) =>
        JsonSerializer.Deserialize<SimulationExperimentSnapshot>(row.ResolvedConfigurationJson, Json)
        ?? throw new JsonException($"Experiment {row.ExperimentId:N} is empty.");

    private static void ValidateLegacyEnvelope(
        SimulationExperimentEntity row,
        SimulationExperimentSnapshot snapshot)
    {
        bool validStoredHash = row.ConfigurationHash.Length == 64 &&
            row.ConfigurationHash.All(Uri.IsHexDigit);
        bool createdByAffectedWriter = string.Equals(row.CreatedBy, "runtime", StringComparison.Ordinal);
        bool createdAtMatches = Math.Abs((snapshot.CreatedAt - row.CreatedAt).TotalMilliseconds) < 1;
        if (!validStoredHash || !createdByAffectedWriter || snapshot.Id != row.ExperimentId ||
            snapshot.Revision != row.Revision || !string.Equals(snapshot.Name, row.Name, StringComparison.Ordinal) ||
            !string.Equals(snapshot.State.ToString(), row.Status, StringComparison.Ordinal) || !createdAtMatches)
        {
            throw new InvalidOperationException($"Experiment {row.ExperimentId:N} failed hash verification.");
        }

        snapshot.Manifest.Timeline.Validate();
        snapshot.Manifest.Parallelism.Validate();
        snapshot.Manifest.Ranking.Validate();
        Guid[] manifestRunIds = snapshot.Manifest.ProfileRuns.Select(item => item.ProfileRunId).ToArray();
        Guid[] progressRunIds = snapshot.Profiles.Select(item => item.ProfileRunId).ToArray();
        if (manifestRunIds.Length == 0 || manifestRunIds.Distinct().Count() != manifestRunIds.Length ||
            progressRunIds.Distinct().Count() != progressRunIds.Length ||
            !manifestRunIds.Order().SequenceEqual(progressRunIds.Order()))
        {
            throw new InvalidOperationException($"Experiment {row.ExperimentId:N} failed structural verification.");
        }

        foreach (SimulationExperimentProfileRun run in snapshot.Manifest.ProfileRuns)
        {
            SimulationStrategyProfile profile = run.Profile.ResolvedProfile;
            profile.Validate();
            if (run.Profile.ProfileId != profile.ProfileId || run.Profile.Revision != profile.Revision ||
                !string.Equals(run.Profile.ContentHash, profile.ContentHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Experiment {row.ExperimentId:N} failed profile verification.");
            }
        }
    }

    private static Guid StableGuid(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value))[..16]);
}
