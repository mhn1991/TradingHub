using System.Diagnostics;
using DBManager.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DBManager.Postgres;

/// <summary>
/// Pings the critical lane with <c>SELECT 1</c> to determine whether new entries may proceed
/// (section 1.3: "No new entry while critical persistence is unhealthy").
/// </summary>
public sealed class PostgresPersistenceHealthCheck(
    [FromKeyedServices(PersistenceLane.Critical)] NpgsqlDataSource criticalDataSource,
    TimeProvider timeProvider)
    : IPersistenceHealthCheck
{
    public async Task<PersistenceHealthReport> CheckAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset checkedAt = timeProvider.GetUtcNow();
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            await using NpgsqlConnection connection = await criticalDataSource
                .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using NpgsqlCommand command = new("SELECT 1", connection);
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            return new PersistenceHealthReport
            {
                Status = PersistenceHealthStatus.Healthy,
                CheckedAt = checkedAt,
                Latency = stopwatch.Elapsed
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new PersistenceHealthReport
            {
                Status = PersistenceHealthStatus.Unavailable,
                CheckedAt = checkedAt,
                Latency = stopwatch.Elapsed,
                Detail = ex.Message
            };
        }
    }
}
