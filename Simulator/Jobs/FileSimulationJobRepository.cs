using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Simulator.Models;

namespace Simulator.Jobs;

public sealed record SimulationJobRecoveryReport
{
    public int ValidJobs { get; init; }
    public int InterruptedJobsMarkedFailed { get; init; }
    public int QuarantinedFiles { get; init; }
    public int TemporaryFilesRemoved { get; init; }
    public IReadOnlyList<string> WarningCodes { get; init; } = [];
}

/// <summary>
/// Versioned, file-backed job store. Individual legacy/corrupt files are isolated so
/// one historical job can never prevent the service from starting a new simulation.
/// </summary>
public sealed class FileSimulationJobRepository : ISimulationJobRepository
{
    public const int CurrentSchemaVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions PermissiveLegacyOptions = CreateLegacyOptions();

    private readonly string _root;
    private readonly string _quarantine;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileSimulationJobRepository(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new ArgumentException("Jobs directory is required.", nameof(rootDirectory));
        _root = Path.GetFullPath(rootDirectory);
        _quarantine = Path.Combine(_root, "quarantine");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_quarantine);
    }

    public SimulationJobRecoveryReport LastRecoveryReport { get; private set; } = new();

    public async Task SaveAsync(SimulationJobSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = GetPath(snapshot.Id);
            SimulationJobSnapshot? previous = File.Exists(path)
                ? await ReadSnapshotUnsafeAsync(path, quarantineOnFailure: true, cancellationToken)
                    .ConfigureAwait(false)
                : null;
            if (previous is not null && snapshot.Revision < previous.Revision)
                return;

            string temporary = path + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await using (FileStream stream = new(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 64 * 1024,
                    useAsync: true))
                {
                    var persisted = new PersistedSimulationJob
                    {
                        SchemaVersion = CurrentSchemaVersion,
                        Snapshot = snapshot
                    };
                    await JsonSerializer.SerializeAsync(stream, persisted, JsonOptions, cancellationToken)
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

    /// <summary>Mark non-terminal jobs as interrupted after process restart.</summary>
    public async Task MarkInterruptedJobsAsync(CancellationToken cancellationToken = default)
    {
        int removedTemporary = 0;
        int quarantinedBefore = CountQuarantinedFiles();
        var warnings = new HashSet<string>(StringComparer.Ordinal);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (string temporary in Directory.EnumerateFiles(_root, "*.tmp"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    File.Delete(temporary);
                    removedTemporary++;
                }
                catch (IOException)
                {
                    warnings.Add("TemporaryJobCleanupFailed");
                }
                catch (UnauthorizedAccessException)
                {
                    warnings.Add("TemporaryJobCleanupFailed");
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        IReadOnlyList<SimulationJobSnapshot> jobs = await ListAsync(int.MaxValue, cancellationToken)
            .ConfigureAwait(false);
        int interrupted = 0;
        foreach (SimulationJobSnapshot job in jobs)
        {
            if (job.IsComplete ||
                job.Status is SimulationJobStatus.Completed or
                    SimulationJobStatus.Failed or
                    SimulationJobStatus.Cancelled)
            {
                continue;
            }

            await SaveAsync(job with
            {
                Revision = job.Revision + 1,
                Status = SimulationJobStatus.Failed,
                IsComplete = true,
                CompletedAt = DateTimeOffset.UtcNow,
                Error = "HostRestartedWhileRunning: The host restarted before this simulation " +
                        "could complete. Partial replay output remains available."
            }, cancellationToken).ConfigureAwait(false);
            interrupted++;
        }

        int quarantinedAfter = CountQuarantinedFiles();
        int quarantined = Math.Max(0, quarantinedAfter - quarantinedBefore);
        if (quarantined > 0)
            warnings.Add("PersistedJobsQuarantined");
        LastRecoveryReport = new SimulationJobRecoveryReport
        {
            ValidJobs = jobs.Count,
            InterruptedJobsMarkedFailed = interrupted,
            QuarantinedFiles = quarantined,
            TemporaryFilesRemoved = removedTemporary,
            WarningCodes = warnings.OrderBy(item => item, StringComparer.Ordinal).ToArray()
        };
    }

    public async Task<SimulationJobSnapshot?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        string path = GetPath(id);
        if (!File.Exists(path))
            return null;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadSnapshotUnsafeAsync(path, quarantineOnFailure: true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<SimulationJobSnapshot>> ListAsync(
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        if (take < 1)
            throw new ArgumentOutOfRangeException(nameof(take));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var items = new List<SimulationJobSnapshot>();
            foreach (string path in Directory.EnumerateFiles(_root, "*.json")
                         .OrderByDescending(File.GetLastWriteTimeUtc))
            {
                cancellationToken.ThrowIfCancellationRequested();
                SimulationJobSnapshot? snapshot = await ReadSnapshotUnsafeAsync(
                        path,
                        quarantineOnFailure: true,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (snapshot is not null)
                    items.Add(snapshot);
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

    private async Task<SimulationJobSnapshot?> ReadSnapshotUnsafeAsync(
        string path,
        bool quarantineOnFailure,
        CancellationToken cancellationToken)
    {
        try
        {
            await using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                useAsync: true);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            JsonElement root = document.RootElement;

            SimulationJobSnapshot? snapshot;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("schemaVersion", out JsonElement schemaElement) &&
                root.TryGetProperty("snapshot", out JsonElement snapshotElement))
            {
                int schema = schemaElement.GetInt32();
                if (schema > CurrentSchemaVersion || schema < 1)
                    throw new UnsupportedPersistedJobSchemaException(schema);
                snapshot = snapshotElement.Deserialize<SimulationJobSnapshot>(JsonOptions);
            }
            else
            {
                // Phase 1-4 files were stored as a raw snapshot. Deserialize them
                // permissively, then rewrite on the next mutation using the envelope.
                snapshot = root.Deserialize<SimulationJobSnapshot>(PermissiveLegacyOptions);
            }

            if (snapshot is null || snapshot.Id == Guid.Empty)
                throw new JsonException("Persisted job does not contain a valid simulation ID.");
            return snapshot;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or
                ArgumentException or
                IOException or
                UnauthorizedAccessException or
                NotSupportedException or
                FormatException or
                InvalidOperationException or
                UnsupportedPersistedJobSchemaException)
        {
            // ArgumentException covers ArgumentOutOfRangeException from domain types such as
            // BarInterval(value<=0) when a legacy snapshot stored default(BarInterval). That
            // must quarantine rather than poison MarkInterruptedJobsAsync / StartAsync.
            if (quarantineOnFailure)
                QuarantineUnsafe(path, ClassifyFailure(exception));
            return null;
        }
    }

    private void QuarantineUnsafe(string path, string reason)
    {
        if (!File.Exists(path))
            return;
        Directory.CreateDirectory(_quarantine);
        string original = Path.GetFileName(path);
        string destination = Path.Combine(
            _quarantine,
            $"{Path.GetFileNameWithoutExtension(original)}.{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.{reason}.json");
        try
        {
            File.Move(path, destination, overwrite: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A concurrent recovery may already have moved the file, or the process
            // may lack permission to quarantine it. Either way, recovery continues
            // because a single historical file must not poison the repository.
        }
    }

    private int CountQuarantinedFiles() => Directory.Exists(_quarantine)
        ? Directory.EnumerateFiles(_quarantine, "*.json").Count()
        : 0;

    private static string ClassifyFailure(Exception exception) => exception switch
    {
        UnsupportedPersistedJobSchemaException => "unsupported-schema",
        JsonException or FormatException or InvalidOperationException => "invalid-json",
        ArgumentException => "invalid-domain",
        UnauthorizedAccessException => "access-denied",
        IOException => "io-error",
        _ => "unsupported-content"
    };

    private static JsonSerializerOptions CreateLegacyOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(typeInfo =>
        {
            foreach (JsonPropertyInfo property in typeInfo.Properties)
                property.IsRequired = false;
        });
        return new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = resolver,
            Converters = { new JsonStringEnumConverter() }
        };
    }

    private string GetPath(Guid id) => Path.Combine(_root, $"{id:N}.json");

    private sealed record PersistedSimulationJob
    {
        public required int SchemaVersion { get; init; }
        public required SimulationJobSnapshot Snapshot { get; init; }
    }

    private sealed class UnsupportedPersistedJobSchemaException(int schemaVersion)
        : Exception($"Persisted simulation job schema {schemaVersion} is unsupported.");
}
