using System.Text.Json;
using System.Text.Json.Serialization;
using Simulator.Experiments.Models;

namespace Simulator.Experiments.Persistence;

public sealed class FileSimulationExperimentRepository : ISimulationExperimentRepository
{
    public const int CurrentSchemaVersion = 1;

    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public FileSimulationExperimentRepository(string root = ".cache/simulation-experiments")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public async Task SaveAsync(
        SimulationExperimentSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = PathFor(snapshot.Id);
            SimulationExperimentSnapshot? previous = File.Exists(path)
                ? await ReadUnsafeAsync(path, cancellationToken).ConfigureAwait(false)
                : null;
            if (previous is not null && snapshot.Revision < previous.Revision)
                return;

            string temporary = Path.Combine(_root, $".{snapshot.Id:N}.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (FileStream stream = new(
                                 temporary,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 64 * 1024,
                                 FileOptions.Asynchronous))
                {
                    await JsonSerializer.SerializeAsync(stream, new PersistedExperiment
                    {
                        SchemaVersion = CurrentSchemaVersion,
                        Snapshot = snapshot
                    }, _json, cancellationToken).ConfigureAwait(false);
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

    public async Task<SimulationExperimentSnapshot?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
            return null;
        string path = PathFor(id);
        if (!File.Exists(path))
            return null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadUnsafeAsync(path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<SimulationExperimentSnapshot>> ListAsync(
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        if (take < 1)
            throw new ArgumentOutOfRangeException(nameof(take));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshots = new List<SimulationExperimentSnapshot>();
            foreach (string path in Directory.EnumerateFiles(_root, "*.json")
                         .OrderByDescending(File.GetLastWriteTimeUtc))
            {
                cancellationToken.ThrowIfCancellationRequested();
                snapshots.Add(await ReadUnsafeAsync(path, cancellationToken).ConfigureAwait(false));
                if (snapshots.Count == take)
                    break;
            }
            return snapshots;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> MarkInterruptedAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SimulationExperimentSnapshot> snapshots = await ListAsync(10_000, cancellationToken)
            .ConfigureAwait(false);
        int count = 0;
        foreach (SimulationExperimentSnapshot snapshot in snapshots.Where(item => !item.IsTerminal))
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

    private async Task<SimulationExperimentSnapshot> ReadUnsafeAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous);
        PersistedExperiment envelope = await JsonSerializer.DeserializeAsync<PersistedExperiment>(
            stream, _json, cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException($"Experiment file '{path}' is empty.");
        if (envelope.SchemaVersion is < 1 or > CurrentSchemaVersion || envelope.Snapshot is null)
            throw new JsonException($"Experiment file '{path}' has an unsupported schema.");
        return envelope.Snapshot;
    }

    private string PathFor(Guid id) => Path.Combine(_root, $"{id:N}.json");

    private sealed record PersistedExperiment
    {
        public int SchemaVersion { get; init; }
        public SimulationExperimentSnapshot? Snapshot { get; init; }
    }
}
