using System.Text.Json;
using DBManager.Postgres.Operations;
using Microsoft.EntityFrameworkCore;
using TradingObservability.Abstractions;
using TradingObservability.Abstractions.Reporting;

namespace DBManager.Postgres.Reporting;

/// <summary>Builds bounded, operator-safe reports from normalized PostgreSQL records.</summary>
public sealed class PostgresTradingReportQueryService(
    IDbContextFactory<TradingHubDbContext> contextFactory) : ITradingReportQueryService
{
    private const int EventLimit = 200;

    public async Task<SimulationOperationalReport?> GetSimulationAsync(Guid simulationId, CancellationToken cancellationToken = default)
    {
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var job = await db.SimulationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.SimulationId == simulationId, cancellationToken).ConfigureAwait(false);
        if (job is null) return null;
        var progress = await db.SimulationJobProgress.AsNoTracking().SingleOrDefaultAsync(x => x.SimulationId == simulationId, cancellationToken).ConfigureAwait(false);
        Guid[] sessions = await db.RuntimeSessions.AsNoTracking().Where(x => x.SimulationId == simulationId)
            .Select(x => x.RuntimeSessionId).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        List<AgentActivityWindowEntity> windows = await db.AgentActivityWindows.AsNoTracking()
            .Where(x => sessions.Contains(x.RuntimeSessionId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var telemetry = await db.TradingTelemetryEvents.AsNoTracking()
            .Where(x => x.SimulationId == simulationId || sessions.Contains(x.RuntimeSessionId))
            .OrderBy(x => x.OccurredAt).Take(EventLimit).ToListAsync(cancellationToken).ConfigureAwait(false);
        var events = await db.SimulationJobEvents.AsNoTracking().Where(x => x.SimulationId == simulationId)
            .OrderBy(x => x.OccurredAt).Take(EventLimit).ToListAsync(cancellationToken).ConfigureAwait(false);
        var blobs = await db.SimulationOutputBlobs.AsNoTracking().Where(x => x.SimulationId == simulationId)
            .OrderBy(x => x.CreatedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        List<string> warnings = [];
        if (sessions.Length == 0) warnings.Add("No runtime session is linked to this simulation; agent and incident sections may be incomplete.");
        if (blobs.Count == 0 && job.OutputManifestId is not null) warnings.Add("The output manifest has no registered blobs.");
        return new SimulationOperationalReport(
            job.SimulationId, job.ParentExperimentId, SimulationStatus(job.Status), SimulationStatus(job.Phase), job.Revision,
            job.ConfigurationHash, job.InputHash, job.RequestedAt, job.StartedAt, job.CompletedAt,
            progress?.ProcessedCandles ?? 0, progress?.ProgressPercent ?? 0, progress?.CandlesPerSecond ?? 0,
            Aggregate(windows), TopReasons(windows, telemetry),
            events.Select(x => new TimelineReportItem(x.OccurredAt, x.EventType,
                    $"simulation.status.{SimulationStatus(x.Status).ToLowerInvariant()}", x.DetailsJson))
                .Concat(telemetry.Select(ToTimeline)).OrderBy(x => x.OccurredAt).Take(EventLimit).ToList(),
            blobs.Select(x => new SimulationBlobReport(x.BlobId, x.BlobKind.ToString(), x.ContentHash, x.SizeBytes,
                x.MediaType, x.CreatedAt, x.RetentionUntil)).ToList(), warnings);
    }

    public async Task<ExperimentOperationalReport?> GetExperimentAsync(Guid experimentId, CancellationToken cancellationToken = default)
    {
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var experiment = await db.SimulationExperiments.AsNoTracking().SingleOrDefaultAsync(x => x.ExperimentId == experimentId, cancellationToken).ConfigureAwait(false);
        if (experiment is null) return null;
        var runs = await db.SimulationExperimentRuns.AsNoTracking().Where(x => x.ExperimentId == experimentId)
            .OrderBy(x => x.ExperimentRunId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var comparisons = await db.SimulationExperimentComparisons.AsNoTracking().Where(x => x.ExperimentId == experimentId)
            .OrderBy(x => x.CreatedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new ExperimentOperationalReport(experiment.ExperimentId, experiment.Name, experiment.Status,
            experiment.ConfigurationHash, experiment.CreatedAt,
            runs.Select(x => new ExperimentRunReport(x.ExperimentRunId, x.SimulationId, x.ProfileRevisionId,
                x.VariantId, x.SharedAnalysisGroupId, x.LearningFrom, x.LearningTo, x.EmbargoFrom, x.EmbargoTo,
                x.EvaluationFrom, x.EvaluationTo)).ToList(),
            comparisons.Select(x => new ExperimentComparisonReport(x.BaselineSimulationId, x.CandidateSimulationId,
                x.MetricsJson, x.CreatedAt)).ToList());
    }

    public async Task<LiveSessionOperationalReport?> GetLiveSessionAsync(Guid runtimeSessionId, CancellationToken cancellationToken = default)
    {
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var session = await db.RuntimeSessions.AsNoTracking().SingleOrDefaultAsync(x => x.RuntimeSessionId == runtimeSessionId, cancellationToken).ConfigureAwait(false);
        if (session is null) return null;
        List<AgentActivityWindowEntity> windows = await db.AgentActivityWindows.AsNoTracking()
            .Where(x => x.RuntimeSessionId == runtimeSessionId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var telemetry = await db.TradingTelemetryEvents.AsNoTracking().Where(x => x.RuntimeSessionId == runtimeSessionId)
            .OrderByDescending(x => x.OccurredAt).Take(EventLimit).ToListAsync(cancellationToken).ConfigureAwait(false);
        long candidates = 0, orders = 0, openPositions = 0;
        if (session.DeploymentId is { } deploymentId)
        {
            candidates = await db.TradeCandidates.LongCountAsync(x => x.DeploymentId == deploymentId, cancellationToken).ConfigureAwait(false);
            orders = await (from order in db.Orders join candidate in db.TradeCandidates on order.CandidateId equals candidate.CandidateId
                where candidate.DeploymentId == deploymentId select order.OrderId).LongCountAsync(cancellationToken).ConfigureAwait(false);
            Guid accountId = await db.Deployments.Where(x => x.DeploymentId == deploymentId).Select(x => x.BrokerAccountId)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            openPositions = await db.Positions.LongCountAsync(x => x.BrokerAccountId == accountId && x.ClosedAt == null, cancellationToken).ConfigureAwait(false);
        }
        return new LiveSessionOperationalReport(session.RuntimeSessionId, session.SessionKind.ToString(), session.DeploymentId,
            session.HostInstanceId, session.ConfigurationHash, session.StartedAt, session.EndedAt, session.Status.ToString(),
            session.TerminationReason, Aggregate(windows), candidates, orders, openPositions,
            telemetry.Where(IsIncident).Select(ToTimeline).OrderBy(x => x.OccurredAt).ToList(), TopReasons(windows, telemetry));
    }

    public async Task<DeploymentOperationalReport?> GetDeploymentAsync(Guid deploymentId, DateTimeOffset? from = null,
        DateTimeOffset? to = null, CancellationToken cancellationToken = default)
    {
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var deployment = await db.Deployments.AsNoTracking().SingleOrDefaultAsync(x => x.DeploymentId == deploymentId, cancellationToken).ConfigureAwait(false);
        if (deployment is null) return null;
        DateTimeOffset rangeFrom = from ?? deployment.StartedAt ?? deployment.RequestedAt;
        DateTimeOffset rangeTo = to ?? deployment.StoppedAt ?? DateTimeOffset.UtcNow;
        if (rangeTo < rangeFrom) throw new ArgumentException("The report end must not precede its start.");
        Guid[] candidateIds = await db.TradeCandidates.AsNoTracking()
            .Where(x => x.DeploymentId == deploymentId && x.DecisionTime >= rangeFrom && x.DecisionTime <= rangeTo)
            .Select(x => x.CandidateId).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var portfolio = await db.PortfolioDecisions.AsNoTracking().Where(x => candidateIds.Contains(x.CandidateId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var orders = await db.Orders.AsNoTracking().Where(x => candidateIds.Contains(x.CandidateId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        Guid[] orderIds = orders.Select(x => x.OrderId).ToArray();
        long fills = await db.Fills.LongCountAsync(x => orderIds.Contains(x.OrderId), cancellationToken).ConfigureAwait(false);
        string[] tradeIds = orders.Where(x => x.BrokerTradeId != null).Select(x => x.BrokerTradeId!).ToArray();
        var positions = await db.Positions.AsNoTracking().Where(x => x.BrokerAccountId == deployment.BrokerAccountId &&
            (tradeIds.Contains(x.BrokerTradeId) || (x.OpenedAt >= rangeFrom && x.OpenedAt <= rangeTo)))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var telemetry = await db.TradingTelemetryEvents.AsNoTracking().Where(x => x.DeploymentId == deploymentId &&
            x.OccurredAt >= rangeFrom && x.OccurredAt <= rangeTo).OrderByDescending(x => x.OccurredAt)
            .Take(EventLimit).ToListAsync(cancellationToken).ConfigureAwait(false);
        var safety = await db.SafetyEvents.AsNoTracking().Where(x => x.DeploymentId == deploymentId &&
            x.OccurredAt >= rangeFrom && x.OccurredAt <= rangeTo).OrderByDescending(x => x.OccurredAt)
            .Take(EventLimit).ToListAsync(cancellationToken).ConfigureAwait(false);
        var lifecycle = await db.DeploymentEvents.AsNoTracking().Where(x => x.DeploymentId == deploymentId &&
            x.OccurredAt >= rangeFrom && x.OccurredAt <= rangeTo).OrderByDescending(x => x.OccurredAt)
            .Take(EventLimit).ToListAsync(cancellationToken).ConfigureAwait(false);
        var settings = await db.BrokerAccountSettingsRevisions.AsNoTracking()
            .Where(x => x.BrokerAccountId == deployment.BrokerAccountId && x.Active)
            .OrderByDescending(x => x.Revision).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        string environment = settings is null ? "unknown" : await db.BrokerEnvironments.AsNoTracking()
            .Where(x => x.BrokerEnvironmentId == settings.BrokerEnvironmentId).Select(x => x.EnvironmentCode)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? "unknown";
        return new DeploymentOperationalReport(deployment.DeploymentId, settings?.AccountAlias ?? "masked-account", environment,
            deployment.PolicyRevisionId ?? Guid.Empty, deployment.ExecutionMode.ToString(), deployment.Status.ToString(), deployment.DeploymentHash,
            deployment.StartedAt ?? deployment.RequestedAt, deployment.StoppedAt, rangeFrom, rangeTo, candidateIds.LongLength,
            portfolio.LongCount(x => x.Approved), portfolio.LongCount(x => !x.Approved), orders.LongCount(), fills,
            positions.LongCount(x => x.ClosedAt is null), positions.Sum(x => x.RealisedPnl), positions.Sum(x => x.UnrealisedPnl),
            telemetry.Where(IsIncident).Select(ToTimeline).Concat(safety.Select(x => new TimelineReportItem(
                x.OccurredAt, "Safety", x.ReasonCode, x.AutomaticActionJson))).Concat(lifecycle.Select(x =>
                new TimelineReportItem(x.OccurredAt, x.EventType, x.ReasonCode ?? "deployment.lifecycle",
                    x.DetailJson))).OrderBy(x => x.OccurredAt).Take(EventLimit).ToList(),
            telemetry.Select(x => x.ReasonCode).Concat(lifecycle.Select(x => x.ReasonCode ?? x.EventType))
                .GroupBy(x => x).OrderByDescending(x => x.Count()).Take(20)
                .Select(x => new ReasonFrequencyReport(x.Key, x.LongCount())).ToList());
    }

    public async Task<CandidateExplanationReport?> GetCandidateAsync(Guid candidateId, CancellationToken cancellationToken = default)
    {
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var candidate = await db.TradeCandidates.AsNoTracking().SingleOrDefaultAsync(x => x.CandidateId == candidateId, cancellationToken).ConfigureAwait(false);
        if (candidate is null) return null;
        var evaluation = await db.AgentEvaluations.AsNoTracking().SingleOrDefaultAsync(x => x.DecisionId == candidate.DecisionId, cancellationToken).ConfigureAwait(false);
        var stages = await db.CandidateStageEvents.AsNoTracking().Where(x => x.CandidateId == candidateId)
            .OrderBy(x => x.OccurredAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        var sizing = await db.PositionSizingEvaluations.AsNoTracking().SingleOrDefaultAsync(x => x.CandidateId == candidateId, cancellationToken).ConfigureAwait(false);
        var portfolio = await db.PortfolioDecisions.AsNoTracking().SingleOrDefaultAsync(x => x.CandidateId == candidateId, cancellationToken).ConfigureAwait(false);
        var orders = await db.Orders.AsNoTracking().Where(x => x.CandidateId == candidateId).OrderBy(x => x.CreatedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        Guid[] orderIds = orders.Select(x => x.OrderId).ToArray();
        var fills = await db.Fills.AsNoTracking().Where(x => orderIds.Contains(x.OrderId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        string[] tradeIds = orders.Where(x => x.BrokerTradeId != null).Select(x => x.BrokerTradeId!).ToArray();
        var position = await db.Positions.AsNoTracking().FirstOrDefaultAsync(x => tradeIds.Contains(x.BrokerTradeId), cancellationToken).ConfigureAwait(false);
        var management = position is null ? null : await db.PositionManagementStates.AsNoTracking()
            .SingleOrDefaultAsync(x => x.PositionId == position.PositionId, cancellationToken).ConfigureAwait(false);
        var outcome = await db.CandidateOutcomes.AsNoTracking().Where(x => x.CandidateId == candidateId)
            .OrderByDescending(x => x.OutcomeVersion).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        List<string> warnings = [];
        if (evaluation is null) warnings.Add("The originating agent evaluation was not found.");
        if (stages.Count == 0) warnings.Add("No append-only candidate funnel stages were recorded.");
        return new CandidateExplanationReport(candidate.CandidateId, candidate.DecisionId, candidate.DeploymentId,
            candidate.PolicyRevisionId, candidate.InstrumentId, candidate.StrategyId, candidate.SetupId,
            candidate.Direction.ToString(), candidate.Status.ToString(), candidate.DecisionTime, candidate.ReferenceMid,
            candidate.StopPrice, candidate.TargetPrice, candidate.RawConfidence,
            evaluation?.DiagnosticsJson ?? candidate.TradingConditionAuditJson, candidate.SetupCalibrationAuditJson,
            candidate.MetaLabelAuditJson,
            stages.Select(x => new CandidateStageReport(x.OccurredAt, x.Stage.ToString(), x.Outcome.ToString(), x.ReasonCode,
                x.ConfidenceBefore, x.ConfidenceAfter, x.RiskMultiplier, x.DetailsJson)).ToList(),
            sizing is null ? null : new SizingExplanationReport(sizing.Approved, sizing.ReasonCode, sizing.BaseRiskAmount,
                sizing.RawQuantity, sizing.NormalizedQuantity, sizing.EstimatedMargin, sizing.AuditJson),
            portfolio is null ? null : new PortfolioExplanationReport(portfolio.Approved, portfolio.ReasonCode,
                portfolio.RequestedRisk, portfolio.ApprovedRisk, portfolio.RequestedQuantity, portfolio.ApprovedQuantity,
                portfolio.PortfolioSnapshotJson),
            orders.Select(order =>
            {
                var orderFills = fills.Where(fill => fill.OrderId == order.OrderId).ToList();
                return new OrderExplanationReport(order.OrderId, order.ClientOrderId, Mask(order.BrokerOrderId),
                    Mask(order.BrokerTradeId), order.State.ToString(), order.SubmissionCertainty.ToString(),
                    order.RequestedQuantity, order.FilledQuantity, order.AverageFillPrice, orderFills.Count,
                    orderFills.Sum(x => x.SpreadCost ?? 0), orderFills.Sum(x => x.SlippageCost ?? 0), order.CreatedAt);
            }).ToList(), position is null ? null : ToPosition(position, management),
            outcome is null ? null : new OutcomeExplanationReport(outcome.TargetReached, outcome.StopReached,
                outcome.FirstTerminalEvent.ToString(), outcome.MfeR, outcome.MaeR, outcome.SpreadAdjustedR, outcome.CalculatedAt), warnings);
    }

    public async Task<PositionExplanationReport?> GetPositionAsync(Guid positionId, CancellationToken cancellationToken = default)
    {
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var position = await db.Positions.AsNoTracking().SingleOrDefaultAsync(x => x.PositionId == positionId, cancellationToken).ConfigureAwait(false);
        if (position is null) return null;
        var state = await db.PositionManagementStates.AsNoTracking().SingleOrDefaultAsync(x => x.PositionId == positionId, cancellationToken).ConfigureAwait(false);
        var events = await db.PositionEvents.AsNoTracking().Where(x => x.PositionId == positionId).OrderBy(x => x.OccurredAt)
            .Take(EventLimit).ToListAsync(cancellationToken).ConfigureAwait(false);
        var management = await db.ManagementEvents.AsNoTracking().Where(x => x.PositionId == positionId).OrderBy(x => x.OccurredAt)
            .Take(EventLimit).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PositionExplanationReport(ToPosition(position, state),
            events.Select(x => new TimelineReportItem(x.OccurredAt, x.EventType.ToString(), x.ReasonCode ?? "position.state_changed", x.DetailsJson)).ToList(),
            management.Select(x => new ManagementActionReport(x.OccurredAt, x.ActionType.ToString(), x.ReasonCode,
                x.CurrentR, x.MfeR, x.MaeR, x.PreviousStop, x.RequestedStop, x.ConfirmedStop,
                x.RequestedReduction, x.ConfirmedReduction, x.DetailsJson)).ToList());
    }

    public async Task<AgentOperationalReport> GetAgentAsync(Guid agentInstanceId, DateTimeOffset? from = null,
        DateTimeOffset? to = null, CancellationToken cancellationToken = default)
    {
        DateTimeOffset rangeTo = to ?? DateTimeOffset.UtcNow;
        DateTimeOffset rangeFrom = from ?? rangeTo.AddDays(-1);
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        List<AgentActivityWindowEntity> windows = await db.AgentActivityWindows.AsNoTracking()
            .Where(x => x.AgentInstanceId == agentInstanceId && x.WindowEnd >= rangeFrom && x.WindowStart <= rangeTo)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var telemetry = await db.TradingTelemetryEvents.AsNoTracking().Where(x => x.AgentInstanceId == agentInstanceId &&
            x.OccurredAt >= rangeFrom && x.OccurredAt <= rangeTo).OrderByDescending(x => x.OccurredAt)
            .Take(EventLimit).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new AgentOperationalReport(agentInstanceId, rangeFrom, rangeTo, Aggregate(windows), TopReasons(windows, telemetry),
            telemetry.Select(ToTimeline).OrderBy(x => x.OccurredAt).ToList());
    }

    public async Task<IReadOnlyList<ReasonFrequencyReport>> GetRejectionReasonsAsync(ReportQueryRange range, CancellationToken cancellationToken = default)
    {
        int limit = Math.Clamp(range.Limit, 1, 200);
        await using TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = from stage in db.CandidateStageEvents.AsNoTracking()
            join candidate in db.TradeCandidates.AsNoTracking() on stage.CandidateId equals candidate.CandidateId
            where (!range.From.HasValue || stage.OccurredAt >= range.From.Value) &&
                  (!range.To.HasValue || stage.OccurredAt <= range.To.Value) &&
                  (!range.DeploymentId.HasValue || candidate.DeploymentId == range.DeploymentId.Value) &&
                  stage.Outcome == DBManager.Abstractions.Decision.StageOutcome.Rejected
            group stage by stage.ReasonCode into reasons orderby reasons.Count() descending
            select new ReasonFrequencyReport(reasons.Key, reasons.LongCount());
        return await query.Take(limit).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private static TimelineReportItem ToTimeline(TradingTelemetryEventEntity x) => new(x.OccurredAt, x.Type.ToString(), x.ReasonCode, x.PayloadJson);
    private static bool IsIncident(TradingTelemetryEventEntity x) => x.Severity >= TradingTelemetrySeverity.Warning ||
        x.Type is TradingTelemetryType.Incident or TradingTelemetryType.SafetyStateChange or
            TradingTelemetryType.PersistenceStateChange or TradingTelemetryType.DataQualityStateChange;

    private static ActivityAggregateReport Aggregate(IReadOnlyList<AgentActivityWindowEntity> windows)
    {
        long evaluations = windows.Sum(x => x.EvaluationsObserved);
        double mean = evaluations == 0 ? 0 : windows.Sum(x => x.MeanEvaluationMilliseconds * x.EvaluationsObserved) / evaluations;
        double p95 = windows.Count == 0 ? 0 : windows.Select(x => x.MaxEvaluationMilliseconds).Order()
            .ElementAt((int)Math.Floor((windows.Count - 1) * .95));
        return new ActivityAggregateReport(evaluations, windows.Sum(x => x.NoSetupCount), windows.Sum(x => x.WarmupCount),
            windows.Sum(x => x.BuyCount), windows.Sum(x => x.SellCount), windows.Sum(x => x.HoldCount),
            windows.Sum(x => x.CandidatesCreated), windows.Sum(x => x.CandidatesRejected), windows.Sum(x => x.StateTransitions),
            windows.Sum(x => x.TimeoutCount), windows.Sum(x => x.ErrorCount), mean, p95);
    }

    private static IReadOnlyList<ReasonFrequencyReport> TopReasons(IReadOnlyList<AgentActivityWindowEntity> windows,
        IReadOnlyList<TradingTelemetryEventEntity> telemetry)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (AgentActivityWindowEntity window in windows)
        {
            try
            {
                Dictionary<string, long>? values = JsonSerializer.Deserialize<Dictionary<string, long>>(window.ReasonCountsJson);
                if (values is null) continue;
                foreach ((string reason, long count) in values) counts[reason] = counts.GetValueOrDefault(reason) + count;
            }
            catch (JsonException) { counts["report.invalid_reason_counts"] = counts.GetValueOrDefault("report.invalid_reason_counts") + 1; }
        }
        foreach (IGrouping<string, TradingTelemetryEventEntity> group in telemetry.GroupBy(x => x.ReasonCode))
            counts[group.Key] = counts.GetValueOrDefault(group.Key) + group.LongCount();
        return counts.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).Take(20)
            .Select(x => new ReasonFrequencyReport(x.Key, x.Value)).ToList();
    }

    private static PositionSummaryReport ToPosition(Execution.PositionEntity position,
        Management.PositionManagementStateEntity? management) => new(position.PositionId, position.State.ToString(),
        position.OriginalQuantity, position.RemainingQuantity, position.AverageEntryPrice, position.CurrentStopPrice,
        position.TargetPrice, position.RealisedPnl, position.UnrealisedPnl, management?.MfeR, management?.MaeR,
        position.OpenedAt, position.ClosedAt);

    private static string SimulationStatus(short value) => value switch
    {
        0 => "Queued",
        1 => "PreparingData",
        2 => "DownloadingData",
        3 => "LoadingCache",
        4 => "WarmingUp",
        5 => "Running",
        6 => "Paused",
        7 => "Cancelling",
        8 => "Cancelled",
        9 => "Exporting",
        10 => "Completed",
        11 => "Failed",
        _ => $"Unknown({value})"
    };

    private static string? Mask(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Length <= 4 ? "****" : $"***{value[^4..]}";
}
