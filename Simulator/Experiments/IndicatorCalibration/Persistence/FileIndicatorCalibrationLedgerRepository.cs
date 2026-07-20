using System.Text.Json;
using System.Text.Json.Serialization;

namespace Simulator.Experiments.IndicatorCalibration.Persistence;

/// <summary>
/// File-backed ledger repository. Mirrors <c>Simulator.Experiments.Persistence.FileSimulationExperimentRepository</c>'s
/// exact pattern: one JSON file per ledger, atomic write-to-temp-then-move, a semaphore
/// serializing all reads/writes, and a "never persist an older revision over a newer one" guard -
/// the same pattern already used by every other file-backed job/experiment store in this project.
/// </summary>
public sealed class FileIndicatorCalibrationLedgerRepository : IIndicatorCalibrationLedgerRepository
{
    public const int CurrentSchemaVersion = 1;

    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public FileIndicatorCalibrationLedgerRepository(string root = ".cache/indicator-calibration-ledgers")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public async Task<IndicatorCalibrationExperimentLedger> SaveAsync(
        IndicatorCalibrationExperimentLedger ledger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ledger.Validate();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = PathFor(ledger.LedgerId);
            IndicatorCalibrationExperimentLedger? previous = File.Exists(path)
                ? await ReadUnsafeAsync(path, cancellationToken).ConfigureAwait(false)
                : null;
            if (previous is not null && ledger.Revision <= previous.Revision)
                return previous;

            string temporary = Path.Combine(_root, $".{Sanitize(ledger.LedgerId)}.{Guid.NewGuid():N}.tmp");
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
                    await JsonSerializer.SerializeAsync(
                        stream,
                        new PersistedLedger { SchemaVersion = CurrentSchemaVersion, Ledger = ledger },
                        _json,
                        cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }

            return ledger;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IndicatorCalibrationExperimentLedger?> GetAsync(
        string ledgerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ledgerId);
        string path = PathFor(ledgerId);
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

    public async Task<IReadOnlyList<IndicatorCalibrationExperimentLedger>> ListAsync(
        int take = 50, CancellationToken cancellationToken = default)
    {
        if (take < 1)
            throw new ArgumentOutOfRangeException(nameof(take));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var ledgers = new List<IndicatorCalibrationExperimentLedger>();
            foreach (string path in Directory.EnumerateFiles(_root, "*.json")
                         .OrderByDescending(File.GetLastWriteTimeUtc))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ledgers.Add(await ReadUnsafeAsync(path, cancellationToken).ConfigureAwait(false));
                if (ledgers.Count == take)
                    break;
            }

            return ledgers;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IndicatorCalibrationExperimentLedger> ReadUnsafeAsync(
        string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        PersistedLedger envelope = await JsonSerializer
            .DeserializeAsync<PersistedLedger>(stream, _json, cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException($"Ledger file '{path}' is empty.");
        if (envelope.SchemaVersion is < 1 || envelope.SchemaVersion > CurrentSchemaVersion || envelope.Ledger is null)
            throw new JsonException($"Ledger file '{path}' has an unsupported schema.");
        return envelope.Ledger;
    }

    private string PathFor(string ledgerId) => Path.Combine(_root, $"{Sanitize(ledgerId)}.json");

    private static string Sanitize(string value) => string.Concat(value.Select(character =>
        char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_'));

    private sealed record PersistedLedger
    {
        public int SchemaVersion { get; init; }
        public IndicatorCalibrationExperimentLedger? Ledger { get; init; }
    }
}
