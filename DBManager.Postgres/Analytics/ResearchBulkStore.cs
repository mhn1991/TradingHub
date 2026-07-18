using DBManager.Abstractions;
using DBManager.Abstractions.Analytics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace DBManager.Postgres.Analytics;

/// <summary>
/// Binary COPY bulk writer through the Research lane (section 19.2, section 15's
/// <c>IResearchBulkStore</c>). Chunks commits so one failure doesn't lose an enormous import
/// (section 19.2), and never touches the Critical lane's connection pool — proving "research
/// import cannot exhaust the connection pool" is a matter of which <see cref="NpgsqlDataSource"/>
/// this class holds, not application-level throttling.
/// </summary>
public sealed class ResearchBulkStore(
    [FromKeyedServices(PersistenceLane.Research)] NpgsqlDataSource researchDataSource)
    : IResearchBulkStore
{
    private const int ChunkSize = 10_000;

    private const string CopyCommand =
        """
        COPY analytics.candidate_outcomes
            (outcome_id, candidate_id, decision_time, horizon_end, target_reached, stop_reached,
             first_terminal_event, mfe_price, mae_price, mfe_r, mae_r, maximum_achievable_r,
             time_to_mfe, time_to_mae, spread_adjusted_r, outcome_version, calculated_at)
        FROM STDIN (FORMAT BINARY)
        """;

    public async Task<long> WriteCandidateOutcomesAsync(
        IAsyncEnumerable<CandidateOutcomeRecord> outcomes, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await researchDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        long totalWritten = 0;
        NpgsqlBinaryImporter? importer = null;
        int rowsInChunk = 0;

        try
        {
            await foreach (CandidateOutcomeRecord record in outcomes.WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                importer ??= await connection.BeginBinaryImportAsync(CopyCommand, cancellationToken)
                    .ConfigureAwait(false);

                await importer.StartRowAsync(cancellationToken).ConfigureAwait(false);
                await importer.WriteAsync(record.OutcomeId, NpgsqlDbType.Uuid, cancellationToken).ConfigureAwait(false);
                await importer.WriteAsync(record.CandidateId, NpgsqlDbType.Uuid, cancellationToken).ConfigureAwait(false);
                await importer.WriteAsync(record.DecisionTime, NpgsqlDbType.TimestampTz, cancellationToken).ConfigureAwait(false);
                await importer.WriteAsync(record.HorizonEnd, NpgsqlDbType.TimestampTz, cancellationToken).ConfigureAwait(false);
                await importer.WriteAsync(record.TargetReached, NpgsqlDbType.Boolean, cancellationToken).ConfigureAwait(false);
                await importer.WriteAsync(record.StopReached, NpgsqlDbType.Boolean, cancellationToken).ConfigureAwait(false);
                await importer.WriteAsync((short)record.FirstTerminalEvent, NpgsqlDbType.Smallint, cancellationToken).ConfigureAwait(false);
                await importer.WriteAsync(record.MfePrice, NpgsqlDbType.Numeric, cancellationToken).ConfigureAwait(false);
                await importer.WriteAsync(record.MaePrice, NpgsqlDbType.Numeric, cancellationToken).ConfigureAwait(false);
                await importer.WriteAsync(record.MfeR, NpgsqlDbType.Numeric, cancellationToken).ConfigureAwait(false);
                await importer.WriteAsync(record.MaeR, NpgsqlDbType.Numeric, cancellationToken).ConfigureAwait(false);
                await importer.WriteAsync(record.MaximumAchievableR, NpgsqlDbType.Numeric, cancellationToken).ConfigureAwait(false);
                await importer.WriteAsync(record.TimeToMfe, NpgsqlDbType.Interval, cancellationToken).ConfigureAwait(false);
                await importer.WriteAsync(record.TimeToMae, NpgsqlDbType.Interval, cancellationToken).ConfigureAwait(false);
                await importer.WriteAsync(record.SpreadAdjustedR, NpgsqlDbType.Numeric, cancellationToken).ConfigureAwait(false);
                await importer.WriteAsync(record.OutcomeVersion, NpgsqlDbType.Integer, cancellationToken).ConfigureAwait(false);
                await importer.WriteAsync(DateTimeOffset.UtcNow, NpgsqlDbType.TimestampTz, cancellationToken).ConfigureAwait(false);

                rowsInChunk++;
                totalWritten++;

                if (rowsInChunk >= ChunkSize)
                {
                    await importer.CompleteAsync(cancellationToken).ConfigureAwait(false);
                    await importer.DisposeAsync().ConfigureAwait(false);
                    importer = null;
                    rowsInChunk = 0;
                }
            }

            if (importer is not null)
                await importer.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (importer is not null)
                await importer.DisposeAsync().ConfigureAwait(false);
        }

        return totalWritten;
    }
}
