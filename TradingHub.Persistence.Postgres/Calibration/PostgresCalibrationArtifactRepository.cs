using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DBManager.Abstractions.Config;
using DBManager.Postgres;
using DBManager.Postgres.Config;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RiskManager.Calibration;
using Simulator.Calibration;
using TradeManager;

namespace TradingHub.Persistence.Postgres.Calibration;

public sealed class PostgresCalibrationArtifactRepository(
    IDbContextFactory<TradingHubDbContext> contextFactory,
    TimeProvider? timeProvider = null,
    ILogger<PostgresCalibrationArtifactRepository>? logger = null) : ICalibrationArtifactRepository
{
    private const int SerializerVersion = 1;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Task<CalibrationArtifactMetadata> StoreSetupAsync(
        SetupCalibrationArtifact artifact, string? description = null,
        CalibrationArtifactProvenance? provenance = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        artifact.Validate();
        return StoreAsync(
            CalibrationArtifactType.Setup, artifact.SchemaVersion, artifact.CalibrationId,
            artifact.Buckets.FirstOrDefault()?.StrategyId ?? "unknown", artifact.StrategyVersion,
            artifact.FeatureSchemaHash, artifact.TrainingFrom, artifact.TrainingTo, artifact.TotalSamples,
            artifact, description, provenance, cancellationToken);
    }

    public Task<CalibrationArtifactMetadata> StoreManagementAsync(
        TradeManagementCalibration artifact, string? description = null,
        CalibrationArtifactProvenance? provenance = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        artifact.Validate();
        DateTimeOffset trainingTo = artifact.CreatedAt;
        DateTimeOffset trainingFrom = trainingTo.AddMilliseconds(-1);
        return StoreAsync(
            CalibrationArtifactType.Management, artifact.SchemaVersion, artifact.CalibrationId,
            artifact.Cohorts.FirstOrDefault()?.StrategyId ?? "unknown", "1", "not-applicable",
            trainingFrom, trainingTo, artifact.Cohorts.Sum(x => (long)x.Samples), artifact,
            description, provenance, cancellationToken);
    }

    public Task<CalibrationArtifactMetadata> StoreMetaModelAsync(
        MetaModelArtifact artifact, string? description = null,
        CalibrationArtifactProvenance? provenance = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        artifact.Validate();
        return StoreAsync(
            CalibrationArtifactType.MetaModel, artifact.SchemaVersion, artifact.CalibrationId,
            artifact.Buckets.FirstOrDefault()?.StrategyId ?? "unknown", artifact.ModelVersion,
            artifact.FeatureSchemaHash, artifact.TrainingFrom, artifact.TrainingTo,
            artifact.Buckets.Sum(x => (long)x.Samples), artifact, description, provenance, cancellationToken);
    }

    public Task<CalibrationArtifactMetadata> StoreIndicatorParametersAsync(
        IndicatorCalibrationArtifact artifact, string? description = null,
        CalibrationArtifactProvenance? provenance = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        artifact.Validate();
        // featureSchemaHash slot: TimeframeTopologyHash is the closest analog for this artifact
        // type - the identity that determines whether the artifact remains structurally
        // meaningful. trainingFrom/trainingTo: the compact artifact deliberately does not carry
        // the full experiment window (that lives in the research ledger, referenced by
        // ExperimentLedgerId) - CreatedAt is used for both as a documented simplification.
        return StoreAsync(
            CalibrationArtifactType.IndicatorParameters, artifact.SchemaVersion, artifact.CalibrationId,
            artifact.StrategyId, artifact.StrategyImplementationVersion, artifact.TimeframeTopologyHash,
            artifact.CreatedAt, artifact.CreatedAt, artifact.Evidence.TotalCandidatesEvaluated,
            artifact, description, provenance, cancellationToken);
    }

    public async Task<IndicatorCalibrationArtifact?> GetIndicatorParametersAsync(
        Guid id, CancellationToken cancellationToken = default)
    {
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        CalibrationArtifactEntity? row = await db.CalibrationArtifacts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ArtifactId == id, cancellationToken).ConfigureAwait(false);
        if (row is null || DomainType(row.ArtifactType) != CalibrationArtifactType.IndicatorParameters || row.PayloadJson is null)
            return null;
        if (!string.Equals(ComputeHash(row.PayloadJson), row.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Calibration artifact {id:N} failed content-hash verification.");
        IndicatorCalibrationArtifact? payload = JsonSerializer.Deserialize<IndicatorCalibrationArtifact>(row.PayloadJson, Json);
        // The stored payload is immutable (its bytes are content-hash-verified), but
        // UpdatePromotionStatusAsync only ever updates row.Status/MetricsJson - reconcile here so
        // a caller never observes the artifact's own stale, as-calibrated status instead of the
        // actual current one (mirrors the equivalent fix in FileCalibrationArtifactRepository).
        return payload is null ? null : payload with { PromotionStatus = DeserializeMetadata(row).PromotionStatus };
    }

    public Task<SetupCalibrationArtifact?> GetSetupAsync(Guid id, CancellationToken cancellationToken = default) =>
        GetPayloadAsync<SetupCalibrationArtifact>(id, CalibrationArtifactType.Setup, cancellationToken);

    public Task<TradeManagementCalibration?> GetManagementAsync(Guid id, CancellationToken cancellationToken = default) =>
        GetPayloadAsync<TradeManagementCalibration>(id, CalibrationArtifactType.Management, cancellationToken);

    public Task<MetaModelArtifact?> GetMetaModelAsync(Guid id, CancellationToken cancellationToken = default) =>
        GetPayloadAsync<MetaModelArtifact>(id, CalibrationArtifactType.MetaModel, cancellationToken);

    public async Task<CalibrationArtifactMetadata?> UpdatePromotionStatusAsync(
        Guid id, CalibrationPromotionStatus status, CancellationToken cancellationToken = default)
    {
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        CalibrationArtifactEntity? row = await db.CalibrationArtifacts
            .SingleOrDefaultAsync(x => x.ArtifactId == id, cancellationToken).ConfigureAwait(false);
        if (row is null) return null;

        CalibrationArtifactMetadata metadata = DeserializeMetadata(row);
        CalibrationArtifactMetadata updated = metadata with { PromotionStatus = status };
        CalibrationArtifactStatus from = row.Status;
        CalibrationArtifactStatus to = DatabaseStatus(status);
        row.Status = to;
        row.MetricsJson = JsonSerializer.Serialize(updated, Json);
        if (status == CalibrationPromotionStatus.Approved)
        {
            row.ApprovedAt ??= _timeProvider.GetUtcNow();
            row.ApprovedBy ??= "runtime";
        }
        db.CalibrationArtifactStatusEvents.Add(new CalibrationArtifactStatusEventEntity
        {
            ArtifactId = id,
            FromStatus = from,
            ToStatus = to,
            OccurredAt = _timeProvider.GetUtcNow(),
            ActorIdentity = "runtime"
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task<CalibrationArtifactMetadata?> GetMetadataAsync(
        Guid id, CancellationToken cancellationToken = default)
    {
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        CalibrationArtifactEntity? row = await db.CalibrationArtifacts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ArtifactId == id, cancellationToken).ConfigureAwait(false);
        return row is null ? null : DeserializeMetadata(row);
    }

    public async Task<IReadOnlyList<CalibrationArtifactMetadata>> ListAsync(
        CalibrationArtifactType? filter = null, int take = 50, CancellationToken cancellationToken = default)
    {
        if (take < 1) throw new ArgumentOutOfRangeException(nameof(take));
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        IQueryable<CalibrationArtifactEntity> query = db.CalibrationArtifacts.AsNoTracking();
        if (filter.HasValue)
        {
            ArtifactRole role = DatabaseType(filter.Value);
            query = query.Where(x => x.ArtifactType == role);
        }
        CalibrationArtifactEntity[] rows = await query.OrderByDescending(x => x.CreatedAt)
            .Take(take).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<CalibrationArtifactMetadata>(rows.Length);
        foreach (CalibrationArtifactEntity row in rows)
        {
            try
            {
                results.Add(DeserializeMetadata(row));
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                // A stale/incompatible row (e.g. written under an older metadata schema) must not
                // take down the whole listing - skip it and keep serving everything that does parse.
                logger?.LogWarning(ex, "Skipping calibration artifact {ArtifactId} with unparseable metadata.", row.ArtifactId);
            }
        }
        return results;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        CalibrationArtifactEntity? row = await db.CalibrationArtifacts
            .SingleOrDefaultAsync(x => x.ArtifactId == id, cancellationToken).ConfigureAwait(false);
        if (row is null) return false;
        bool referenced = await db.PolicyArtifacts.AnyAsync(x => x.ArtifactId == id, cancellationToken).ConfigureAwait(false) ||
                          await db.CalibrationBundleCandidates.AnyAsync(x =>
                              x.SetupArtifactId == id || x.MetaModelArtifactId == id || x.ManagementArtifactId == id,
                              cancellationToken).ConfigureAwait(false);
        if (referenced || row.Status == CalibrationArtifactStatus.ApprovedForDemo || row.Status == CalibrationArtifactStatus.Retired)
            return false;
        db.CalibrationArtifacts.Remove(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<CalibrationArtifactMetadata> StoreAsync<T>(
        CalibrationArtifactType type, int schemaVersion, string calibrationId, string strategyId,
        string strategyVersion, string featureSchemaHash, DateTimeOffset trainingFrom,
        DateTimeOffset trainingTo, long sampleCount, T artifact, string? description,
        CalibrationArtifactProvenance? provenance, CancellationToken cancellationToken)
    {
        string payload = JsonSerializer.Serialize(artifact, Json);
        string contentHash = ComputeHash(payload);
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        CalibrationArtifactEntity? existing = await db.CalibrationArtifacts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ContentHash == contentHash, cancellationToken).ConfigureAwait(false);
        if (existing is not null) return DeserializeMetadata(existing);

        var metadata = new CalibrationArtifactMetadata
        {
            Id = Guid.NewGuid(),
            Type = type,
            SchemaVersion = schemaVersion,
            CalibrationId = calibrationId,
            CreatedAt = _timeProvider.GetUtcNow(),
            ContentHash = contentHash,
            Description = description,
            Folds = provenance?.Folds ?? [],
            Embargo = provenance?.Embargo ?? TimeSpan.Zero,
            TestWindowFrom = provenance?.TestWindowFrom,
            TestWindowTo = provenance?.TestWindowTo,
            ValidationMetrics = provenance?.ValidationMetrics,
            TestMetrics = provenance?.TestMetrics,
            SupersedesArtifactId = provenance?.SupersedesArtifactId,
            PromotionStatus = CalibrationPromotionStatus.PendingReview
        };
        DateTimeOffset validationFrom = provenance?.Folds.Count > 0
            ? provenance.Folds.Min(x => x.TestFrom)
            : trainingFrom;
        DateTimeOffset validationTo = provenance?.Folds.Count > 0
            ? provenance.Folds.Max(x => x.TestTo)
            : trainingTo;
        db.CalibrationArtifacts.Add(new CalibrationArtifactEntity
        {
            ArtifactId = metadata.Id,
            ArtifactType = DatabaseType(type),
            StrategyId = strategyId,
            StrategyVersion = strategyVersion,
            FeatureSchemaHash = featureSchemaHash,
            ContentHash = contentHash,
            StorageUri = $"postgresql://config/calibration-artifacts/{metadata.Id:N}",
            Status = CalibrationArtifactStatus.Registered,
            TrainingFrom = trainingFrom,
            TrainingTo = trainingTo,
            ValidationFrom = validationFrom,
            ValidationTo = validationTo,
            TestFrom = provenance?.TestWindowFrom,
            TestTo = provenance?.TestWindowTo,
            SampleCount = sampleCount,
            MetricsJson = JsonSerializer.Serialize(metadata, Json),
            PayloadJson = payload,
            ContentSizeBytes = Encoding.UTF8.GetByteCount(payload),
            MediaType = "application/json",
            SerializerVersion = SerializerVersion,
            CreatedAt = metadata.CreatedAt
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return metadata;
    }

    private async Task<T?> GetPayloadAsync<T>(
        Guid id, CalibrationArtifactType expectedType, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        CalibrationArtifactEntity? row = await db.CalibrationArtifacts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ArtifactId == id, cancellationToken).ConfigureAwait(false);
        if (row is null || DomainType(row.ArtifactType) != expectedType || row.PayloadJson is null) return default;
        if (!string.Equals(ComputeHash(row.PayloadJson), row.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Calibration artifact {id:N} failed content-hash verification.");
        return JsonSerializer.Deserialize<T>(row.PayloadJson, Json);
    }

    private static CalibrationArtifactMetadata DeserializeMetadata(CalibrationArtifactEntity row)
    {
        CalibrationArtifactMetadata metadata = JsonSerializer.Deserialize<CalibrationArtifactMetadata>(row.MetricsJson, Json)
            ?? throw new InvalidOperationException($"Calibration artifact {row.ArtifactId:N} has invalid metadata.");
        return metadata;
    }

    private static string ComputeHash(string payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        string normalized = JsonSerializer.Serialize(document.RootElement, Json);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    private static ArtifactRole DatabaseType(CalibrationArtifactType type) => type switch
    {
        CalibrationArtifactType.Setup => ArtifactRole.SetupCalibration,
        CalibrationArtifactType.MetaModel => ArtifactRole.MetaModel,
        CalibrationArtifactType.Management => ArtifactRole.ManagementCalibration,
        CalibrationArtifactType.IndicatorParameters => ArtifactRole.IndicatorParameters,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static CalibrationArtifactType DomainType(ArtifactRole role) => role switch
    {
        ArtifactRole.SetupCalibration => CalibrationArtifactType.Setup,
        ArtifactRole.MetaModel => CalibrationArtifactType.MetaModel,
        ArtifactRole.ManagementCalibration => CalibrationArtifactType.Management,
        ArtifactRole.IndicatorParameters => CalibrationArtifactType.IndicatorParameters,
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };

    private static CalibrationArtifactStatus DatabaseStatus(CalibrationPromotionStatus status) => status switch
    {
        CalibrationPromotionStatus.PendingReview => CalibrationArtifactStatus.Registered,
        CalibrationPromotionStatus.Approved => CalibrationArtifactStatus.ApprovedForDemo,
        CalibrationPromotionStatus.Rejected or CalibrationPromotionStatus.Superseded => CalibrationArtifactStatus.Retired,
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };
}
