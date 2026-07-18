using System.Diagnostics;
using System.Security.Cryptography;
using DBManager.Abstractions;
using DBManager.Abstractions.Operations;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DBManager.Postgres.Operations.Backup;

public sealed record BackupResult
{
    public required Guid BackupId { get; init; }
    public required bool Succeeded { get; init; }
    public string? FilePath { get; init; }
    public string? FailureReason { get; init; }
}

public sealed record RestoreVerificationResult
{
    public required bool Succeeded { get; init; }
    public string? FailureDetail { get; init; }
}

/// <summary>
/// Section 27's minimum: nightly <c>pg_dump</c> custom-format backup, weekly verified restore
/// test. Shells out to the real <c>pg_dump</c>/<c>pg_restore</c> binaries rather than
/// reimplementing dump/restore in C# — matches the doc's own recommendation and is the only
/// trustworthy way to produce a restorable backup. The restore verification creates a scratch
/// database, restores into it, runs a basic sanity check, and always drops it afterward.
/// </summary>
public sealed class BackupRunner(IBackupStore backupStore, ISchemaVersionReader schemaVersionReader, ILogger<BackupRunner> logger)
{
    public async Task<BackupResult> RunBackupAsync(
        string connectionString, string outputDirectory, TimeSpan retention, CancellationToken cancellationToken)
    {
        NpgsqlConnectionStringBuilder builder = new(connectionString);
        Guid backupId = Guid.NewGuid();
        Directory.CreateDirectory(outputDirectory);
        string filePath = Path.Combine(outputDirectory, $"{backupId:N}.dump");

        await backupStore.RecordBackupStartedAsync(
            new RecordBackupStarted { BackupId = backupId, FilePath = filePath }, cancellationToken)
            .ConfigureAwait(false);

        ProcessStartInfo startInfo = new("pg_dump")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add($"--host={builder.Host}");
        startInfo.ArgumentList.Add($"--port={builder.Port}");
        startInfo.ArgumentList.Add($"--username={builder.Username}");
        startInfo.ArgumentList.Add("--format=custom");
        startInfo.ArgumentList.Add($"--file={filePath}");
        startInfo.ArgumentList.Add(builder.Database!);
        startInfo.Environment["PGPASSWORD"] = builder.Password ?? string.Empty;

        (bool ok, string stderr) = await RunProcessAsync(startInfo, cancellationToken).ConfigureAwait(false);
        if (!ok)
        {
            await backupStore.RecordBackupFailedAsync(
                new RecordBackupFailed { BackupId = backupId, FailureReason = stderr }, cancellationToken)
                .ConfigureAwait(false);
            logger.LogError("pg_dump failed for backup {BackupId}: {Error}", backupId, stderr);
            return new BackupResult { BackupId = backupId, Succeeded = false, FailureReason = stderr };
        }

        string checksum = await ComputeChecksumAsync(filePath, cancellationToken).ConfigureAwait(false);
        long sizeBytes = new FileInfo(filePath).Length;
        string postgresVersion = await GetPostgresVersionAsync(connectionString, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<SchemaVersionInfo> migrations = await schemaVersionReader
            .GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false);
        string schemaVersion = migrations.Count == 0 ? "none" : migrations[^1].MigrationId;

        await backupStore.RecordBackupCompletedAsync(new RecordBackupCompleted
        {
            BackupId = backupId,
            PostgreSqlVersion = postgresVersion,
            SchemaVersion = schemaVersion,
            Checksum = checksum,
            SizeBytes = sizeBytes,
            RetentionExpiresAt = DateTimeOffset.UtcNow + retention
        }, cancellationToken).ConfigureAwait(false);

        return new BackupResult { BackupId = backupId, Succeeded = true, FilePath = filePath };
    }

    /// <summary>
    /// "A backup is not valid until it has been restored and verified" (section 27). Requires a
    /// connection string with database-creation privilege (<c>trading_migrator</c>).
    /// </summary>
    public async Task<RestoreVerificationResult> VerifyRestoreAsync(
        Guid backupId, string filePath, string adminConnectionString, CancellationToken cancellationToken)
    {
        NpgsqlConnectionStringBuilder builder = new(adminConnectionString);
        string scratchDatabase = $"restore_verify_{backupId:N}";

        try
        {
            await using (NpgsqlConnection maintenance = new(
                new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = "postgres" }.ConnectionString))
            {
                await maintenance.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using NpgsqlCommand createDb = new($"CREATE DATABASE \"{scratchDatabase}\"", maintenance);
                await createDb.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            ProcessStartInfo restoreInfo = new("pg_restore")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            restoreInfo.ArgumentList.Add($"--host={builder.Host}");
            restoreInfo.ArgumentList.Add($"--port={builder.Port}");
            restoreInfo.ArgumentList.Add($"--username={builder.Username}");
            restoreInfo.ArgumentList.Add($"--dbname={scratchDatabase}");
            restoreInfo.ArgumentList.Add("--no-owner");
            restoreInfo.ArgumentList.Add(filePath);
            restoreInfo.Environment["PGPASSWORD"] = builder.Password ?? string.Empty;

            (bool restoreOk, string restoreStderr) = await RunProcessAsync(restoreInfo, cancellationToken)
                .ConfigureAwait(false);

            bool sane = false;
            if (restoreOk)
                sane = await SanityCheckRestoredDatabaseAsync(adminConnectionString, scratchDatabase, cancellationToken)
                    .ConfigureAwait(false);

            RecordRestoreTest resultCommand = restoreOk && sane
                ? new RecordRestoreTest { BackupId = backupId, Status = RestoreTestStatus.Passed }
                : new RecordRestoreTest
                {
                    BackupId = backupId,
                    Status = RestoreTestStatus.Failed,
                    FailureDetail = !restoreOk ? restoreStderr : "sanity check found no schema_history rows"
                };
            await backupStore.RecordRestoreTestAsync(resultCommand, cancellationToken).ConfigureAwait(false);

            return new RestoreVerificationResult
            {
                Succeeded = restoreOk && sane,
                FailureDetail = resultCommand.FailureDetail
            };
        }
        finally
        {
            await using NpgsqlConnection maintenance = new(
                new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = "postgres" }.ConnectionString);
            await maintenance.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using NpgsqlCommand dropDb = new(
                $"DROP DATABASE IF EXISTS \"{scratchDatabase}\" WITH (FORCE)", maintenance);
            await dropDb.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> SanityCheckRestoredDatabaseAsync(
        string adminConnectionString, string scratchDatabase, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(
            new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = scratchDatabase }.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new(
            "SELECT count(*) FROM operations.schema_history", connection);
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is long count && count > 0;
    }

    private static async Task<string> GetPostgresVersionAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new("SHOW server_version", connection);
        return (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static async Task<string> ComputeChecksumAsync(string filePath, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(filePath);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<(bool Ok, string StdErr)> RunProcessAsync(
        ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        using Process process = new() { StartInfo = startInfo };
        process.Start();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        await stdoutTask.ConfigureAwait(false);
        return (process.ExitCode == 0, stderr);
    }
}
