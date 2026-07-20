using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RiskManager.Calibration;
using TradeManager;

namespace Simulator.Calibration;

/// <summary>
/// Versioned, file-backed calibration-artifact store under <c>.cache/calibration-artifacts</c>.
/// Mirrors <c>Simulator.Jobs.FileSimulationJobRepository</c>'s pattern exactly: atomic
/// tmp-then-move writes gated by a semaphore, a <c>quarantine/</c> folder for corrupt/
/// unparseable files (never throws on a bad read), content-hash verified on read.
/// </summary>
public sealed class FileCalibrationArtifactRepository : ICalibrationArtifactRepository
{
    public const int CurrentEnvelopeSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Used only for content-hash computation, on both the write and read side. Must stay
    /// non-indented: an indented payload re-serialized from inside a nested envelope property
    /// picks up extra nesting whitespace that a standalone top-level serialization never had,
    /// which would make the two hashes diverge for reasons unrelated to actual content.
    /// </summary>
    private static readonly JsonSerializerOptions HashOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _root;
    private readonly string _quarantine;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileCalibrationArtifactRepository(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new ArgumentException("Calibration artifacts directory is required.", nameof(rootDirectory));
        _root = Path.GetFullPath(rootDirectory);
        _quarantine = Path.Combine(_root, "quarantine");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_quarantine);
    }

    public async Task<CalibrationArtifactMetadata> StoreSetupAsync(
        SetupCalibrationArtifact artifact,
        string? description = null,
        CalibrationArtifactProvenance? provenance = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        artifact.Validate();
        JsonElement payload = JsonSerializer.SerializeToElement(artifact, JsonOptions);
        CalibrationArtifactMetadata metadata = BuildMetadata(
            CalibrationArtifactType.Setup, artifact.SchemaVersion, artifact.CalibrationId, payload, description, provenance);
        await WriteAsync(metadata, payload, cancellationToken).ConfigureAwait(false);
        return metadata;
    }

    public async Task<CalibrationArtifactMetadata> StoreManagementAsync(
        TradeManagementCalibration artifact,
        string? description = null,
        CalibrationArtifactProvenance? provenance = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        artifact.Validate();
        JsonElement payload = JsonSerializer.SerializeToElement(artifact, JsonOptions);
        CalibrationArtifactMetadata metadata = BuildMetadata(
            CalibrationArtifactType.Management, artifact.SchemaVersion, artifact.CalibrationId, payload, description, provenance);
        await WriteAsync(metadata, payload, cancellationToken).ConfigureAwait(false);
        return metadata;
    }

    public async Task<CalibrationArtifactMetadata> StoreMetaModelAsync(
        MetaModelArtifact artifact,
        string? description = null,
        CalibrationArtifactProvenance? provenance = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        artifact.Validate();
        JsonElement payload = JsonSerializer.SerializeToElement(artifact, JsonOptions);
        CalibrationArtifactMetadata metadata = BuildMetadata(
            CalibrationArtifactType.MetaModel, artifact.SchemaVersion, artifact.CalibrationId, payload, description, provenance);
        await WriteAsync(metadata, payload, cancellationToken).ConfigureAwait(false);
        return metadata;
    }

    public async Task<CalibrationArtifactMetadata> StoreIndicatorParametersAsync(
        IndicatorCalibrationArtifact artifact,
        string? description = null,
        CalibrationArtifactProvenance? provenance = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        artifact.Validate();
        JsonElement payload = JsonSerializer.SerializeToElement(artifact, JsonOptions);
        CalibrationArtifactMetadata metadata = BuildMetadata(
            CalibrationArtifactType.IndicatorParameters, artifact.SchemaVersion, artifact.CalibrationId, payload,
            description, provenance);
        await WriteAsync(metadata, payload, cancellationToken).ConfigureAwait(false);
        return metadata;
    }

    private static CalibrationArtifactMetadata BuildMetadata(
        CalibrationArtifactType type,
        int schemaVersion,
        string calibrationId,
        JsonElement payload,
        string? description,
        CalibrationArtifactProvenance? provenance) => new()
    {
        Id = Guid.NewGuid(),
        Type = type,
        SchemaVersion = schemaVersion,
        CalibrationId = calibrationId,
        CreatedAt = DateTimeOffset.UtcNow,
        ContentHash = ComputeHashOfElement(payload),
        Description = description,
        Folds = provenance?.Folds ?? [],
        Embargo = provenance?.Embargo ?? TimeSpan.Zero,
        TestWindowFrom = provenance?.TestWindowFrom,
        TestWindowTo = provenance?.TestWindowTo,
        ValidationMetrics = provenance?.ValidationMetrics,
        TestMetrics = provenance?.TestMetrics,
        SupersedesArtifactId = provenance?.SupersedesArtifactId
    };

    public async Task<CalibrationArtifactMetadata?> UpdatePromotionStatusAsync(
        Guid id, CalibrationPromotionStatus status, CancellationToken cancellationToken = default)
    {
        ArtifactEnvelope<JsonElement>? envelope = await ReadEnvelopeAsync(id, cancellationToken).ConfigureAwait(false);
        if (envelope is null)
            return null;
        CalibrationArtifactMetadata updated = envelope.Metadata with { PromotionStatus = status };
        // Payload/content-hash are unchanged - only the review-status metadata field moves.
        await WriteAsync(updated, envelope.Payload, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task<MetaModelArtifact?> GetMetaModelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        ArtifactEnvelope<JsonElement>? envelope = await ReadEnvelopeAsync(id, cancellationToken).ConfigureAwait(false);
        if (envelope is null || envelope.Metadata.Type != CalibrationArtifactType.MetaModel)
            return null;
        return envelope.Payload.Deserialize<MetaModelArtifact>(JsonOptions);
    }

    public async Task<SetupCalibrationArtifact?> GetSetupAsync(Guid id, CancellationToken cancellationToken = default)
    {
        ArtifactEnvelope<JsonElement>? envelope = await ReadEnvelopeAsync(id, cancellationToken).ConfigureAwait(false);
        if (envelope is null || envelope.Metadata.Type != CalibrationArtifactType.Setup)
            return null;
        return envelope.Payload.Deserialize<SetupCalibrationArtifact>(JsonOptions);
    }

    public async Task<TradeManagementCalibration?> GetManagementAsync(Guid id, CancellationToken cancellationToken = default)
    {
        ArtifactEnvelope<JsonElement>? envelope = await ReadEnvelopeAsync(id, cancellationToken).ConfigureAwait(false);
        if (envelope is null || envelope.Metadata.Type != CalibrationArtifactType.Management)
            return null;
        return envelope.Payload.Deserialize<TradeManagementCalibration>(JsonOptions);
    }

    public async Task<IndicatorCalibrationArtifact?> GetIndicatorParametersAsync(
        Guid id, CancellationToken cancellationToken = default)
    {
        ArtifactEnvelope<JsonElement>? envelope = await ReadEnvelopeAsync(id, cancellationToken).ConfigureAwait(false);
        if (envelope is null || envelope.Metadata.Type != CalibrationArtifactType.IndicatorParameters)
            return null;
        IndicatorCalibrationArtifact? payload = envelope.Payload.Deserialize<IndicatorCalibrationArtifact>(JsonOptions);
        // The stored payload is immutable (its bytes are content-hash-verified), but
        // UpdatePromotionStatusAsync only ever updates the envelope metadata's PromotionStatus -
        // reconcile here so a caller never observes the artifact's own stale, as-calibrated status
        // instead of the actual current one.
        return payload is null ? null : payload with { PromotionStatus = envelope.Metadata.PromotionStatus };
    }

    public async Task<CalibrationArtifactMetadata?> GetMetadataAsync(Guid id, CancellationToken cancellationToken = default)
    {
        ArtifactEnvelope<JsonElement>? envelope = await ReadEnvelopeAsync(id, cancellationToken).ConfigureAwait(false);
        return envelope?.Metadata;
    }

    public async Task<IReadOnlyList<CalibrationArtifactMetadata>> ListAsync(
        CalibrationArtifactType? filter = null, int take = 50, CancellationToken cancellationToken = default)
    {
        if (take < 1)
            throw new ArgumentOutOfRangeException(nameof(take));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var items = new List<CalibrationArtifactMetadata>();
            foreach (string path in Directory.EnumerateFiles(_root, "*.json")
                         .OrderByDescending(File.GetLastWriteTimeUtc))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArtifactEnvelope<JsonElement>? envelope = await ReadEnvelopeUnsafeAsync(path, cancellationToken)
                    .ConfigureAwait(false);
                if (envelope is null || (filter is not null && envelope.Metadata.Type != filter))
                    continue;
                items.Add(envelope.Metadata);
                if (items.Count >= take)
                    break;
            }

            return items;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = GetPath(id);
            if (!File.Exists(path))
                return false;
            File.Delete(path);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ArtifactEnvelope<JsonElement>?> ReadEnvelopeAsync(Guid id, CancellationToken cancellationToken)
    {
        string path = GetPath(id);
        if (!File.Exists(path))
            return null;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadEnvelopeUnsafeAsync(path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ArtifactEnvelope<JsonElement>?> ReadEnvelopeUnsafeAsync(string path, CancellationToken cancellationToken)
    {
        ArtifactEnvelope<JsonElement>? envelope;
        try
        {
            await using FileStream stream = new(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024, useAsync: true);
            envelope = await JsonSerializer
                .DeserializeAsync<ArtifactEnvelope<JsonElement>>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            QuarantineUnsafe(path);
            return null;
        }

        if (envelope is null || envelope.SchemaVersion > CurrentEnvelopeSchemaVersion || envelope.SchemaVersion < 1)
        {
            QuarantineUnsafe(path);
            return null;
        }

        string expectedHash = ComputeHashOfElement(envelope.Payload);
        if (!string.Equals(expectedHash, envelope.Metadata.ContentHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Calibration artifact '{envelope.Metadata.Id}' failed content-hash verification - " +
                "the file may have been edited outside the repository. This is a data-integrity error, not quarantined.");
        }

        return envelope;
    }

    private void QuarantineUnsafe(string path)
    {
        string destination = Path.Combine(_quarantine, Path.GetFileName(path));
        try
        {
            File.Move(path, destination, overwrite: true);
        }
        catch (IOException)
        {
            // Best-effort - if quarantine itself fails, leave the file in place rather than throw.
        }
    }

    private async Task WriteAsync(CalibrationArtifactMetadata metadata, JsonElement payload, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = GetPath(metadata.Id);
            string temporary = path + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await using (FileStream stream = new(
                    temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    bufferSize: 64 * 1024, useAsync: true))
                {
                    var envelope = new ArtifactEnvelope<JsonElement>
                    {
                        SchemaVersion = CurrentEnvelopeSchemaVersion,
                        Metadata = metadata,
                        Payload = payload
                    };
                    await JsonSerializer.SerializeAsync(stream, envelope, JsonOptions, cancellationToken)
                        .ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private string GetPath(Guid id) => Path.Combine(_root, $"{id:N}.json");

    private static string ComputeHashOfElement(JsonElement payload)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, HashOptions)));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed record ArtifactEnvelope<T>
    {
        public required int SchemaVersion { get; init; }
        public required CalibrationArtifactMetadata Metadata { get; init; }
        public required T Payload { get; init; }
    }
}
