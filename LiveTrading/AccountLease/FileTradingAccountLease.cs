using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiveTrading.AccountLease;

/// <summary>
/// File-based implementation. Uses two files, not one: an exclusively-locked <c>.lock</c> file is
/// the actual OS-level mutual-exclusion primitive (held open for the process's lifetime); a
/// separately-written <c>.lease.json</c> carries observability metadata only. A single combined
/// file cannot work here - <see cref="FileShare.None"/> blocks a second process from even
/// *reading* the file to diagnose who holds it, which is exactly the case a second process needs
/// to handle (stale-lease detection). Never writes the access token.
/// </summary>
public sealed class FileTradingAccountLease(AccountLeaseOptions options, TimeProvider timeProvider)
    : ITradingAccountLease, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        TypeInfoResolver = LeaseJsonContext.Default,
        WriteIndented = true
    };

    private FileStream? _lockStream;
    private string? _lockPath;
    private string? _metadataPath;
    private LeaseMetadata? _metadata;

    public async Task<AccountLeaseResult> TryAcquireAsync(
        string broker, string accountId, string instanceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(broker);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        Directory.CreateDirectory(options.Directory);
        string key = SanitizeKey($"{broker}-{accountId}");
        _lockPath = Path.Combine(options.Directory, $"{key}.lock");
        _metadataPath = Path.Combine(options.Directory, $"{key}.lease.json");

        for (int attempt = 0; attempt < 2; attempt++)
        {
            AccountLeaseResult result = await TryAcquireOnceAsync(broker, accountId, instanceId, cancellationToken)
                .ConfigureAwait(false);

            if (result.Outcome != AccountLeaseOutcome.HeldByOtherStale || !options.Force || attempt > 0)
            {
                return result;
            }

            // Stale + force: delete both files and retry exactly once. A live, healthy holder
            // (HeldByOther) is never displaced regardless of Force.
            TryDeleteLeaseFiles();
        }

        return await TryAcquireOnceAsync(broker, accountId, instanceId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AccountLeaseResult> TryAcquireOnceAsync(
        string broker, string accountId, string instanceId, CancellationToken cancellationToken)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(
                _lockPath!, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            LeaseMetadata? current = await TryReadMetadataAsync(cancellationToken).ConfigureAwait(false);
            bool stale = current is not null &&
                timeProvider.GetUtcNow() - current.RenewedAt > options.StaleThreshold;
            return new AccountLeaseResult
            {
                Outcome = stale ? AccountLeaseOutcome.HeldByOtherStale : AccountLeaseOutcome.HeldByOther,
                CurrentHolder = current
            };
        }

        _lockStream = stream;
        DateTimeOffset now = timeProvider.GetUtcNow();
        var metadata = new LeaseMetadata
        {
            Broker = broker,
            AccountId = accountId,
            InstanceId = instanceId,
            MachineName = Environment.MachineName,
            ProcessId = Environment.ProcessId,
            AcquiredAt = now,
            RenewedAt = now
        };
        await WriteMetadataAsync(metadata, cancellationToken).ConfigureAwait(false);
        _metadata = metadata;
        return new AccountLeaseResult { Outcome = AccountLeaseOutcome.Acquired };
    }

    public async Task RenewAsync(CancellationToken cancellationToken)
    {
        if (_metadata is null || _lockStream is null)
        {
            throw new InvalidOperationException("The lease has not been acquired.");
        }

        _metadata = _metadata with { RenewedAt = timeProvider.GetUtcNow() };
        await WriteMetadataAsync(_metadata, cancellationToken).ConfigureAwait(false);
    }

    public async Task ReleaseAsync(CancellationToken cancellationToken)
    {
        if (_lockStream is null)
        {
            return;
        }

        await _lockStream.DisposeAsync().ConfigureAwait(false);
        _lockStream = null;
        _metadata = null;
        TryDeleteLeaseFiles();
    }

    public async ValueTask DisposeAsync()
    {
        if (_lockStream is not null)
        {
            await _lockStream.DisposeAsync().ConfigureAwait(false);
            _lockStream = null;
        }
        // Deliberately does not delete the files on a non-graceful dispose - a crash should leave
        // the OS lock releasable (which DisposeAsync above achieves) without racing a concurrent
        // reader trying to inspect the metadata file at the same moment.
    }

    private async Task<LeaseMetadata?> TryReadMetadataAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!File.Exists(_metadataPath))
                {
                    return null;
                }

                await using FileStream read = new(_metadataPath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                LeaseFileV1? content = await JsonSerializer.DeserializeAsync(
                    read, LeaseJsonContext.Default.LeaseFileV1, cancellationToken).ConfigureAwait(false);
                return content is null ? null : ToMetadata(content);
            }
            catch (IOException) when (attempt < 2)
            {
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return null;
    }

    private async Task WriteMetadataAsync(LeaseMetadata metadata, CancellationToken cancellationToken)
    {
        string tempPath = $"{_metadataPath}.tmp";
        await using (FileStream write = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(
                write, ToFileContent(metadata), LeaseJsonContext.Default.LeaseFileV1, cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(tempPath, _metadataPath!, overwrite: true);
    }

    private void TryDeleteLeaseFiles()
    {
        try
        {
            if (_lockPath is not null && File.Exists(_lockPath))
            {
                File.Delete(_lockPath);
            }

            if (_metadataPath is not null && File.Exists(_metadataPath))
            {
                File.Delete(_metadataPath);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; a leftover file does not affect correctness (the OS lock, not
            // file presence, is the source of truth for exclusivity).
        }
    }

    private static string SanitizeKey(string key)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(key.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).ToLowerInvariant();
    }

    private static LeaseFileV1 ToFileContent(LeaseMetadata metadata) => new()
    {
        LeaseVersion = 1,
        Broker = metadata.Broker,
        AccountId = metadata.AccountId,
        InstanceId = metadata.InstanceId,
        MachineName = metadata.MachineName,
        ProcessId = metadata.ProcessId,
        AcquiredAt = metadata.AcquiredAt,
        RenewedAt = metadata.RenewedAt
    };

    private static LeaseMetadata ToMetadata(LeaseFileV1 content) => new()
    {
        Broker = content.Broker,
        AccountId = content.AccountId,
        InstanceId = content.InstanceId,
        MachineName = content.MachineName,
        ProcessId = content.ProcessId,
        AcquiredAt = content.AcquiredAt,
        RenewedAt = content.RenewedAt
    };
}

internal sealed record LeaseFileV1
{
    public required int LeaseVersion { get; init; }
    public required string Broker { get; init; }
    public required string AccountId { get; init; }
    public required string InstanceId { get; init; }
    public required string MachineName { get; init; }
    public required int ProcessId { get; init; }
    public required DateTimeOffset AcquiredAt { get; init; }
    public required DateTimeOffset RenewedAt { get; init; }
}

[JsonSerializable(typeof(LeaseFileV1))]
internal sealed partial class LeaseJsonContext : JsonSerializerContext;
