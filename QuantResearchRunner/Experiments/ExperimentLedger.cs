using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuantResearchRunner.Experiments;

public enum ExperimentUnitStatus
{
    Pending,
    Running,
    Completed,
    Failed
}

public sealed record ExperimentUnitRecord
{
    public required string Key { get; init; }
    public required ExperimentUnitStatus Status { get; init; }
    public string? Error { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    /// <summary>
    /// Optional JSON payload for completed units (e.g. performance + parameters) so resume
    /// can restore selection state without re-running the backtest.
    /// </summary>
    public string? ResultJson { get; init; }
}

public sealed record ExperimentLedgerState
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyDictionary<string, ExperimentUnitRecord> Units { get; init; } =
        new Dictionary<string, ExperimentUnitRecord>();
}

/// <summary>
/// Tracks completion of individual units of work (a walk-forward fold candidate, an ablation
/// variant, a sensitivity grid point) within one research experiment, so an interrupted
/// multi-run experiment can resume by skipping already-completed units and reloading their
/// stored results. Mirrors <c>Simulator.Jobs.FileSimulationJobRepository</c>'s atomic
/// tmp-then-move write pattern.
/// </summary>
public sealed class ExperimentLedger
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ExperimentLedger(string experimentDirectory)
    {
        if (string.IsNullOrWhiteSpace(experimentDirectory))
            throw new ArgumentException("Experiment directory is required.", nameof(experimentDirectory));
        Directory.CreateDirectory(experimentDirectory);
        _path = Path.Combine(experimentDirectory, "ledger.json");
    }

    public async Task<bool> IsCompletedAsync(string key, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ExperimentLedgerState state = await ReadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return state.Units.TryGetValue(key, out ExperimentUnitRecord? record) &&
                record.Status == ExperimentUnitStatus.Completed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkAsync(
        string key,
        ExperimentUnitStatus status,
        string? error = null,
        CancellationToken cancellationToken = default) =>
        await MarkAsync(key, status, error, resultJson: null, cancellationToken).ConfigureAwait(false);

    public async Task MarkCompletedAsync<T>(
        string key,
        T result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        string json = JsonSerializer.Serialize(result, JsonOptions);
        await MarkAsync(key, ExperimentUnitStatus.Completed, error: null, resultJson: json, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<T?> TryGetResultAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("A unit key is required.", nameof(key));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ExperimentLedgerState state = await ReadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            if (!state.Units.TryGetValue(key, out ExperimentUnitRecord? record) ||
                record.Status != ExperimentUnitStatus.Completed ||
                string.IsNullOrWhiteSpace(record.ResultJson))
            {
                return default;
            }

            return JsonSerializer.Deserialize<T>(record.ResultJson, JsonOptions);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task MarkAsync(
        string key,
        ExperimentUnitStatus status,
        string? error,
        string? resultJson,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("A unit key is required.", nameof(key));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ExperimentLedgerState state = await ReadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var units = new Dictionary<string, ExperimentUnitRecord>(state.Units)
            {
                [key] = new ExperimentUnitRecord
                {
                    Key = key,
                    Status = status,
                    Error = error,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    ResultJson = resultJson
                }
            };
            await WriteUnsafeAsync(state with { Units = units }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ExperimentLedgerState> ReadUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
            return new ExperimentLedgerState();

        await using FileStream stream = new(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        ExperimentLedgerState? state = await JsonSerializer
            .DeserializeAsync<ExperimentLedgerState>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return state ?? new ExperimentLedgerState();
    }

    private async Task WriteUnsafeAsync(ExperimentLedgerState state, CancellationToken cancellationToken)
    {
        string temporary = _path + $".{Guid.NewGuid():N}.tmp";
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
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    /// <summary>Deterministic SHA-256 key from an ordered set of inputs.</summary>
    public static string ComputeKey(params object?[] parts)
    {
        string joined = string.Join('|', parts.Select(part => part?.ToString() ?? "null"));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
