using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using LiveTrading.ManualApproval;
using LiveTrading.Registry;
using PortfolioManager.Risk;
using RiskManager.Safety;
using LiveTrading.Shadow.Outcomes;

namespace LiveTrading.Persistence;

public sealed record LiveTradingPersistenceOptions
{
    public string RootDirectory { get; init; } = ".state/live-trading";
    public int QueueCapacity { get; init; } = 4_096;
    public bool FlushEachEvent { get; init; } = true;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RootDirectory) || QueueCapacity < 32)
            throw new ArgumentOutOfRangeException(nameof(LiveTradingPersistenceOptions));
    }
}

public sealed record LiveEngineCheckpoint
{
    public int SchemaVersion { get; init; } = 1;
    public required DateTimeOffset SavedAt { get; init; }
    public required LiveRegistrySnapshot Registry { get; init; }
    public required PortfolioReservationSnapshot Reservations { get; init; }
    public required IReadOnlyList<ManualApprovalCandidate> ManualApprovals { get; init; }
    public required TradingSafetySnapshot Safety { get; init; }
    public string? LastReconciliationId { get; init; }
    public string? LastBrokerTransactionId { get; init; }
    public bool CleanShutdown { get; init; }
    public LiveShadowOutcomeCheckpoint? ShadowOutcomes { get; init; }
}

public interface ILiveTradingPersistence
{
    Task RunAsync(CancellationToken cancellationToken);
    ValueTask AppendAsync<T>(string streamName, T payload, CancellationToken cancellationToken);
    ValueTask SaveCheckpointAsync(LiveEngineCheckpoint checkpoint, CancellationToken cancellationToken);
    Task<LiveEngineCheckpoint?> LoadCheckpointAsync(CancellationToken cancellationToken);
}

public sealed record LivePersistenceMetrics(
    int Capacity,
    int QueueDepth,
    long Enqueued,
    long Completed,
    long Failed);

/// <summary>Optional bounded-writer telemetry without widening the critical persistence contract.</summary>
public interface ILiveTradingPersistenceDiagnostics
{
    LivePersistenceMetrics Metrics { get; }
}

/// <summary>
/// Single bounded persistence writer. Journals are append-only SHA-256 envelopes and checkpoints
/// use a hashed envelope, temporary file, and atomic move. Any write failure is returned to the
/// caller so the live safety
/// layer can pause new entries rather than silently losing broker/account history.
/// </summary>
public sealed class FileLiveTradingPersistence : ILiveTradingPersistence, ILiveTradingPersistenceDiagnostics
{
    private readonly LiveTradingPersistenceOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly JsonSerializerOptions _json;
    private readonly Channel<PersistenceCommand> _commands;
    private int _queueDepth;
    private long _enqueued;
    private long _completed;
    private long _failed;

    public FileLiveTradingPersistence(
        LiveTradingPersistenceOptions options,
        TimeProvider timeProvider)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
            PropertyNameCaseInsensitive = true
        };
        _json.Converters.Add(new JsonStringEnumConverter());
        _commands = Channel.CreateBounded<PersistenceCommand>(new BoundedChannelOptions(_options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    public LivePersistenceMetrics Metrics => new(
        _options.QueueCapacity,
        Volatile.Read(ref _queueDepth),
        Interlocked.Read(ref _enqueued),
        Interlocked.Read(ref _completed),
        Interlocked.Read(ref _failed));

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.RootDirectory);
        try
        {
            await foreach (PersistenceCommand command in _commands.Reader.ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                Interlocked.Decrement(ref _queueDepth);
                try
                {
                    switch (command)
                    {
                        case AppendCommand append:
                            await AppendCoreAsync(append, cancellationToken).ConfigureAwait(false);
                            break;
                        case CheckpointCommand checkpoint:
                            await SaveCheckpointCoreAsync(checkpoint.Checkpoint, cancellationToken)
                                .ConfigureAwait(false);
                            break;
                    }
                    Interlocked.Increment(ref _completed);
                    command.Completion.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _failed);
                    command.Completion.TrySetException(ex);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            while (_commands.Reader.TryRead(out PersistenceCommand? pending))
            {
                Interlocked.Decrement(ref _queueDepth);
                Interlocked.Increment(ref _failed);
                pending.Completion.TrySetCanceled(cancellationToken);
            }
        }
    }

    public async ValueTask AppendAsync<T>(
        string streamName,
        T payload,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamName);
        ArgumentNullException.ThrowIfNull(payload);
        var command = new AppendCommand
        {
            StreamName = Sanitize(streamName),
            Payload = payload,
            PayloadType = payload.GetType(),
            Timestamp = _timeProvider.GetUtcNow()
        };
        await EnqueueAsync(command, cancellationToken).ConfigureAwait(false);
        await command.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SaveCheckpointAsync(
        LiveEngineCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var command = new CheckpointCommand { Checkpoint = checkpoint };
        await EnqueueAsync(command, cancellationToken).ConfigureAwait(false);
        await command.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask EnqueueAsync(
        PersistenceCommand command,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _queueDepth);
        Interlocked.Increment(ref _enqueued);
        try
        {
            await _commands.Writer.WriteAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Decrement(ref _queueDepth);
            Interlocked.Increment(ref _failed);
            throw;
        }
    }

    public async Task<LiveEngineCheckpoint?> LoadCheckpointAsync(CancellationToken cancellationToken)
    {
        string path = CheckpointPath;
        if (!File.Exists(path))
            return null;
        try
        {
            await using FileStream stream = File.OpenRead(path);
            using JsonDocument document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            LiveEngineCheckpoint? checkpoint;
            if (document.RootElement.TryGetProperty("payload", out _))
            {
                CheckpointEnvelope envelope = document.RootElement.Deserialize<CheckpointEnvelope>(_json) ??
                    throw new InvalidDataException("The live checkpoint envelope is empty.");
                if (envelope.SchemaVersion != 1)
                    throw new InvalidDataException("The live checkpoint envelope has an unsupported schema.");
                byte[] payloadBytes = JsonSerializer.SerializeToUtf8Bytes(envelope.Payload, _json);
                string actualHash = Sha256(payloadBytes);
                if (!CryptographicOperations.FixedTimeEquals(
                        Encoding.ASCII.GetBytes(actualHash),
                        Encoding.ASCII.GetBytes(envelope.PayloadSha256)))
                {
                    throw new InvalidDataException("The live checkpoint SHA-256 integrity check failed.");
                }
                checkpoint = envelope.Payload;
            }
            else
            {
                // Backward-compatible migration of the Phase-3 plain checkpoint format.
                checkpoint = document.RootElement.Deserialize<LiveEngineCheckpoint>(_json);
            }
            if (checkpoint is null || checkpoint.SchemaVersion != 1)
                throw new InvalidDataException("The live engine checkpoint is empty or has an unsupported schema.");
            return checkpoint;
        }
        catch
        {
            string quarantine = Path.Combine(
                _options.RootDirectory,
                $"engine-checkpoint.corrupt-{_timeProvider.GetUtcNow():yyyyMMddHHmmssfff}.json");
            File.Move(path, quarantine, overwrite: true);
            throw;
        }
    }

    private async Task AppendCoreAsync(AppendCommand command, CancellationToken cancellationToken)
    {
        string path = Path.Combine(_options.RootDirectory, $"{command.StreamName}.ndjson");
        await using FileStream stream = new(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 16 * 1024,
            useAsync: true);
        await using var writer = new StreamWriter(stream);
        string payload = JsonSerializer.Serialize(command.Payload, command.PayloadType, _json);
        using JsonDocument document = JsonDocument.Parse(payload);
        string envelope = JsonSerializer.Serialize(new JournalEnvelope
        {
            SchemaVersion = 1,
            Timestamp = command.Timestamp,
            PayloadType = command.PayloadType.FullName ?? command.PayloadType.Name,
            PayloadSha256 = Sha256(Encoding.UTF8.GetBytes(payload)),
            Payload = document.RootElement.Clone()
        }, _json);
        await writer.WriteLineAsync(envelope.AsMemory(), cancellationToken).ConfigureAwait(false);
        if (_options.FlushEachEvent)
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveCheckpointCoreAsync(
        LiveEngineCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.RootDirectory);
        string temporary = $"{CheckpointPath}.{Guid.NewGuid():N}.tmp";
        byte[] payloadBytes = JsonSerializer.SerializeToUtf8Bytes(checkpoint, _json);
        var envelope = new CheckpointEnvelope
        {
            PayloadSha256 = Sha256(payloadBytes),
            Payload = checkpoint
        };
        await using (FileStream stream = new(
                         temporary,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 32 * 1024,
                         useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, envelope, _json, cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        File.Move(temporary, CheckpointPath, overwrite: true);
    }

    private string CheckpointPath => Path.Combine(_options.RootDirectory, "engine-checkpoint.json");

    private static string Sha256(ReadOnlySpan<byte> payload) =>
        Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    private static string Sanitize(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '-' : character).ToArray());
    }

    private abstract record PersistenceCommand
    {
        public TaskCompletionSource<bool> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record AppendCommand : PersistenceCommand
    {
        public required string StreamName { get; init; }
        public required object Payload { get; init; }
        public required Type PayloadType { get; init; }
        public required DateTimeOffset Timestamp { get; init; }
    }

    private sealed record CheckpointCommand : PersistenceCommand
    {
        public required LiveEngineCheckpoint Checkpoint { get; init; }
    }

    private sealed record CheckpointEnvelope
    {
        public int SchemaVersion { get; init; } = 1;
        public required string PayloadSha256 { get; init; }
        public required LiveEngineCheckpoint Payload { get; init; }
    }

    private sealed record JournalEnvelope
    {
        public required int SchemaVersion { get; init; }
        public required DateTimeOffset Timestamp { get; init; }
        public required string PayloadType { get; init; }
        public required string PayloadSha256 { get; init; }
        public required JsonElement Payload { get; init; }
    }
}
