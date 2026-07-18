using DBManager.Abstractions;
using DBManager.Abstractions.Research;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DBManager.Postgres.Research;

/// <summary>EF Core implementation of <see cref="IResearchStore"/> (section 7.9, Phase 6).</summary>
public sealed class ResearchStore(IDbContextFactory<TradingHubDbContext> contextFactory) : IResearchStore
{
    public async Task<DurableResult> StartResearchRunAsync(
        StartResearchRun command, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        context.Add(new ResearchRunEntity
        {
            ResearchRunId = command.ResearchRunId,
            RunType = command.RunType,
            StrategyId = command.StrategyId,
            StrategyVersion = command.StrategyVersion,
            ConfigurationHash = command.ConfigurationHash,
            DatasetHash = command.DatasetHash,
            Status = ResearchRunStatus.Requested,
            RequestedAt = DateTimeOffset.UtcNow,
            RandomSeed = command.RandomSeed,
            ParametersJson = command.ParametersJson
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return DurableResult.Committed();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            return DurableResult.Duplicate("research_run_already_exists");
        }
    }

    public async Task<DurableResult> CompleteResearchRunAsync(
        CompleteResearchRun command, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        ResearchRunEntity? run = await context.Set<ResearchRunEntity>()
            .FirstOrDefaultAsync(r => r.ResearchRunId == command.ResearchRunId, cancellationToken)
            .ConfigureAwait(false);
        if (run is null)
            return DurableResult.PermanentFailure("research_run_not_found");

        run.Status = command.Status;
        run.CompletedAt = DateTimeOffset.UtcNow;
        run.SummaryMetricsJson = command.SummaryMetricsJson;
        run.FailureReason = command.FailureReason;

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return DurableResult.Committed();
    }

    public async Task<DurableResult> RecordFoldAsync(RecordTimeSeriesFold command, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        context.Add(new TimeSeriesFoldEntity
        {
            FoldId = command.FoldId,
            ResearchRunId = command.ResearchRunId,
            FoldNumber = command.FoldNumber,
            TrainingFrom = command.TrainingFrom,
            TrainingTo = command.TrainingTo,
            ValidationFrom = command.ValidationFrom,
            ValidationTo = command.ValidationTo,
            TestFrom = command.TestFrom,
            TestTo = command.TestTo,
            PurgeDuration = command.PurgeDuration,
            EmbargoDuration = command.EmbargoDuration,
            SampleCountsJson = command.SampleCountsJson,
            MetricsJson = command.MetricsJson
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return DurableResult.Committed();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            return DurableResult.Duplicate("fold_already_exists");
        }
    }

    public async Task<DurableResult> RecordCalibrationRunAsync(
        RecordCalibrationRun command, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        context.Add(new CalibrationRunEntity
        {
            CalibrationRunId = command.CalibrationRunId,
            ResearchRunId = command.ResearchRunId,
            Stage = command.Stage,
            InputArtifactIdsJson = command.InputArtifactIdsJson,
            OutputArtifactId = command.OutputArtifactId,
            Status = command.Status,
            StartedAt = DateTimeOffset.UtcNow,
            MetricsJson = command.MetricsJson
        });

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return DurableResult.Committed();
    }

    public async Task<IReadOnlyList<FoldLineageEntry>> GetFoldLineageAsync(
        Guid researchRunId, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        List<TimeSeriesFoldEntity> folds = await context.Set<TimeSeriesFoldEntity>()
            .AsNoTracking()
            .Where(f => f.ResearchRunId == researchRunId)
            .OrderBy(f => f.FoldNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return folds.Select(f => new FoldLineageEntry
        {
            FoldId = f.FoldId,
            FoldNumber = f.FoldNumber,
            PurgeDuration = f.PurgeDuration,
            EmbargoDuration = f.EmbargoDuration,
            TrainingFrom = f.TrainingFrom,
            ValidationTo = f.ValidationTo
        }).ToArray();
    }
}
