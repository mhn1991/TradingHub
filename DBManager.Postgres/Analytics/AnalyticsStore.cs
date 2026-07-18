using DBManager.Abstractions;
using DBManager.Abstractions.Analytics;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DBManager.Postgres.Analytics;

/// <summary>EF Core implementation of <see cref="IAnalyticsStore"/> (section 7.10).</summary>
public sealed class AnalyticsStore(IDbContextFactory<TradingHubDbContext> contextFactory) : IAnalyticsStore
{
    public async Task<DurableResult> RecordExecutionQualityAsync(
        RecordExecutionQuality command, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        context.Add(new ExecutionQualityEntity
        {
            ExecutionQualityId = command.ExecutionQualityId,
            OrderId = command.OrderId,
            DecisionBid = command.DecisionBid,
            DecisionAsk = command.DecisionAsk,
            SubmissionBid = command.SubmissionBid,
            SubmissionAsk = command.SubmissionAsk,
            ExpectedFillPrice = command.ExpectedFillPrice,
            ActualFillPrice = command.ActualFillPrice,
            ExpectedSpread = command.ExpectedSpread,
            ActualSpread = command.ActualSpread,
            ExpectedSlippage = command.ExpectedSlippage,
            ActualSlippage = command.ActualSlippage,
            SubmissionLatencyMs = command.SubmissionLatencyMs,
            AckLatencyMs = command.AckLatencyMs,
            FillLatencyMs = command.FillLatencyMs,
            Session = command.Session,
            Regime = command.Regime,
            CalculatedAt = DateTimeOffset.UtcNow
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return DurableResult.Committed();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            return DurableResult.Duplicate("execution_quality_already_exists");
        }
    }

    public async Task<DurableResult> RecordModelMonitoringWindowAsync(
        RecordModelMonitoringWindow command, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        context.Add(new ModelMonitoringWindowEntity
        {
            WindowId = command.WindowId,
            ArtifactId = command.ArtifactId,
            WindowStart = command.WindowStart,
            WindowEnd = command.WindowEnd,
            SampleCount = command.SampleCount,
            DriftMetricsJson = command.DriftMetricsJson,
            CalibrationMetricsJson = command.CalibrationMetricsJson
        });

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return DurableResult.Committed();
    }
}
