using System.Text.Json;
using System.Text.Json.Serialization;
using Simulator.Calibration;

namespace Simulator.Experiments.IndicatorCalibration.Persistence;

/// <summary>
/// File-backed promotion-event audit log. One JSON file per event (named by <c>EventId</c>),
/// atomic write-to-temp-then-move, a semaphore serializing all reads/writes - the same pattern as
/// <see cref="FileIndicatorCalibrationLedgerRepository"/>. An event id that already exists on disk
/// is refused rather than overwritten: audit events are immutable once recorded.
/// </summary>
public sealed class FileCalibrationPromotionEventRepository : ICalibrationPromotionEventRepository
{
    public const int CurrentSchemaVersion = 1;

    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public FileCalibrationPromotionEventRepository(string root = ".cache/calibration-promotion-events")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public async Task SaveAsync(CalibrationPromotionEvent promotionEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(promotionEvent);
        promotionEvent.Validate();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = PathFor(promotionEvent.EventId);
            if (File.Exists(path))
                throw new InvalidOperationException($"Promotion event '{promotionEvent.EventId}' already exists and cannot be overwritten.");

            string temporary = Path.Combine(_root, $".{Sanitize(promotionEvent.EventId)}.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (FileStream stream = new(
                                 temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                 64 * 1024, FileOptions.Asynchronous))
                {
                    await JsonSerializer.SerializeAsync(
                        stream,
                        new PersistedEvent { SchemaVersion = CurrentSchemaVersion, Event = promotionEvent },
                        _json,
                        cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporary, path, overwrite: false);
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

    public async Task<IReadOnlyList<CalibrationPromotionEvent>> ListForArtifactAsync(
        string artifactId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactId);
        IReadOnlyList<CalibrationPromotionEvent> all = await ListAsync(int.MaxValue, cancellationToken).ConfigureAwait(false);
        return all.Where(item => string.Equals(item.ArtifactId, artifactId, StringComparison.Ordinal))
            .OrderBy(item => item.ApprovedAt)
            .ToArray();
    }

    public async Task<IReadOnlyList<CalibrationPromotionEvent>> ListAsync(
        int take = 50, CancellationToken cancellationToken = default)
    {
        if (take < 1)
            throw new ArgumentOutOfRangeException(nameof(take));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var events = new List<CalibrationPromotionEvent>();
            foreach (string path in Directory.EnumerateFiles(_root, "*.json")
                         .OrderByDescending(File.GetLastWriteTimeUtc))
            {
                cancellationToken.ThrowIfCancellationRequested();
                events.Add(await ReadUnsafeAsync(path, cancellationToken).ConfigureAwait(false));
                if (events.Count == take)
                    break;
            }
            return events;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<CalibrationPromotionEvent> ReadUnsafeAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        PersistedEvent envelope = await JsonSerializer
            .DeserializeAsync<PersistedEvent>(stream, _json, cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException($"Promotion event file '{path}' is empty.");
        if (envelope.SchemaVersion is < 1 || envelope.SchemaVersion > CurrentSchemaVersion || envelope.Event is null)
            throw new JsonException($"Promotion event file '{path}' has an unsupported schema.");
        return envelope.Event;
    }

    private string PathFor(string eventId) => Path.Combine(_root, $"{Sanitize(eventId)}.json");

    private static string Sanitize(string value) => string.Concat(value.Select(character =>
        char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_'));

    private sealed record PersistedEvent
    {
        public int SchemaVersion { get; init; }
        public CalibrationPromotionEvent? Event { get; init; }
    }
}
