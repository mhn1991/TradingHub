using System.Text.Json;

namespace Simulator.Experiments.IndicatorCalibration.Persistence;

/// <summary>
/// File-backed <see cref="ICalibrationCandidateCache"/>: one JSON file per cache key, under a
/// caller-chosen root directory. This is what makes resume actually save real work rather than
/// merely reproducing the same decision - <see cref="InMemoryCalibrationCandidateCache"/> dies
/// with the process, so a resumed run would recompute every evaluation from scratch even though
/// the decision came out identical either way. Pointing a fresh cache instance at the same root
/// directory a prior (interrupted) run used is what "resume" means for this engine: the
/// orchestrator itself is unchanged and unaware caching happened at all (blueprint §14: resume
/// must produce the same decision output - this is guaranteed by construction here, since a cache
/// hit returns the exact value that would otherwise have been recomputed, never a different one).
/// </summary>
public sealed class FileCalibrationCandidateCache : ICalibrationCandidateCache
{
    private readonly string _root;
    private readonly object _sync = new();
    private long _hits;
    private long _misses;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public FileCalibrationCandidateCache(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);

    public bool TryGet(BacktestEvaluationIdentity identity, out BacktestEvaluationResult? result)
    {
        ArgumentNullException.ThrowIfNull(identity);
        string path = PathFor(identity.ComputeCacheKey());
        lock (_sync)
        {
            if (File.Exists(path))
            {
                result = JsonSerializer.Deserialize<BacktestEvaluationResult>(File.ReadAllText(path), JsonOptions);
                Interlocked.Increment(ref _hits);
                return result is not null;
            }
        }
        Interlocked.Increment(ref _misses);
        result = null;
        return false;
    }

    public void Set(BacktestEvaluationIdentity identity, BacktestEvaluationResult result)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(result);
        result.Validate();
        string path = PathFor(identity.ComputeCacheKey());
        string temporary = Path.Combine(_root, $".{Guid.NewGuid():N}.tmp");
        lock (_sync)
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(result, JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
    }

    private string PathFor(string cacheKey) => Path.Combine(_root, $"{cacheKey}.json");
}
