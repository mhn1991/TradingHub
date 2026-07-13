using System.Text.Json;
using System.Text.Json.Serialization;
using Simulator.Models;

namespace Simulator.Jobs;

/// <summary>File-backed job store. Can later be replaced by a database without changing callers.</summary>
public sealed class FileSimulationJobRepository : ISimulationJobRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileSimulationJobRepository(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new ArgumentException("Jobs directory is required.", nameof(rootDirectory));
        _root = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(_root);
    }

    public async Task SaveAsync(SimulationJobSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = GetPath(snapshot.Id);
            if (File.Exists(path))
            {
                await using FileStream existing = File.OpenRead(path);
                SimulationJobSnapshot? previous =
                    await JsonSerializer.DeserializeAsync<SimulationJobSnapshot>(
                        existing,
                        JsonOptions,
                        cancellationToken).ConfigureAwait(false);
                if (previous is not null && snapshot.Revision < previous.Revision)
                {
                    // Never overwrite a newer revision with an older one.
                    return;
                }
            }

            string temporary = path + ".tmp";
            await using (FileStream stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Mark non-terminal jobs as interrupted after process restart.</summary>
    public async Task MarkInterruptedJobsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SimulationJobSnapshot> jobs = await ListAsync(500, cancellationToken).ConfigureAwait(false);
        foreach (SimulationJobSnapshot job in jobs)
        {
            if (job.IsComplete)
                continue;
            if (job.Status is SimulationJobStatus.Completed or SimulationJobStatus.Failed or SimulationJobStatus.Cancelled)
                continue;

            await SaveAsync(job with
            {
                Revision = job.Revision + 1,
                Status = SimulationJobStatus.Failed,
                IsComplete = true,
                CompletedAt = DateTimeOffset.UtcNow,
                Error = "Interrupted by service restart. Partial replay output may remain on disk."
            }, cancellationToken).ConfigureAwait(false);
        }
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
            await using FileStream stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<SimulationJobSnapshot>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
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
                         .OrderByDescending(File.GetLastWriteTimeUtc)
                         .Take(take))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using FileStream stream = File.OpenRead(path);
                SimulationJobSnapshot? snapshot =
                    await JsonSerializer.DeserializeAsync<SimulationJobSnapshot>(
                        stream,
                        JsonOptions,
                        cancellationToken).ConfigureAwait(false);
                if (snapshot is not null)
                    items.Add(snapshot);
            }

            return items;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string GetPath(Guid id) => Path.Combine(_root, $"{id:N}.json");
}
