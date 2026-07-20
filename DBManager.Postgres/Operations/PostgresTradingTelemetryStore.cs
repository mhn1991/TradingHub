using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TradingObservability.Abstractions;

namespace DBManager.Postgres.Operations;

/// <summary>
/// Environment-neutral telemetry sink. Producers emit only state/lifecycle changes; ordinary
/// observations are coalesced through <see cref="IAgentActivityStore"/>.
/// </summary>
public sealed class PostgresTradingTelemetryStore(
    IDbContextFactory<TradingHubDbContext> contextFactory) :
    ITradingTelemetryWriter,
    IRuntimeSessionStore,
    IAgentActivityStore
{
    public async ValueTask WriteAsync(
        TradingTelemetryEvent telemetryEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(telemetryEvent);
        using JsonDocument _ = JsonDocument.Parse(telemetryEvent.PayloadJson);
        TelemetryCorrelation correlation = telemetryEvent.Correlation;
        await using TradingHubDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        context.TradingTelemetryEvents.Add(new TradingTelemetryEventEntity
        {
            EventId = telemetryEvent.EventId,
            RuntimeSessionId = correlation.RuntimeSessionId,
            Type = telemetryEvent.Type,
            Severity = telemetryEvent.Severity,
            OccurredAt = telemetryEvent.OccurredAt,
            ObservedAt = telemetryEvent.ObservedAt,
            ReasonCode = telemetryEvent.ReasonCode,
            ExperimentId = correlation.ExperimentId,
            SimulationId = correlation.SimulationId,
            ResearchRunId = correlation.ResearchRunId,
            DeploymentId = correlation.DeploymentId,
            AgentInstanceId = correlation.AgentInstanceId,
            PolicyRevisionId = correlation.PolicyRevisionId,
            InstrumentId = correlation.InstrumentId,
            DecisionId = correlation.DecisionId,
            CandidateId = correlation.CandidateId,
            ReservationId = correlation.ReservationId,
            OrderCommandId = correlation.OrderCommandId,
            OrderId = correlation.OrderId,
            FillId = correlation.FillId,
            PositionId = correlation.PositionId,
            PayloadJson = telemetryEvent.PayloadJson
        });
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // Event IDs are idempotency keys; retrying an acknowledged event is harmless.
        }
    }

    public ValueTask FlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public async Task StartAsync(RuntimeSessionRegistration registration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        await using TradingHubDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        context.RuntimeSessions.Add(new RuntimeSessionEntity
        {
            RuntimeSessionId = registration.RuntimeSessionId,
            SessionKind = registration.Kind,
            SimulationId = registration.SimulationId,
            DeploymentId = registration.DeploymentId,
            ResearchRunId = registration.ResearchRunId,
            HostInstanceId = registration.HostInstanceId,
            MachineName = registration.MachineName,
            ProcessId = registration.ProcessId,
            ConfigurationHash = registration.ConfigurationHash,
            StartedAt = registration.StartedAt,
            Status = RuntimeSessionStatus.Running
        });
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // Starting the same session after an uncertain acknowledgement is idempotent.
        }
    }

    public async Task EndAsync(
        Guid runtimeSessionId,
        RuntimeSessionStatus status,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (status is RuntimeSessionStatus.Starting or RuntimeSessionStatus.Running)
            throw new ArgumentOutOfRangeException(nameof(status));
        await using TradingHubDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        RuntimeSessionEntity row = await context.RuntimeSessions.SingleOrDefaultAsync(
                value => value.RuntimeSessionId == runtimeSessionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Runtime session was not found.");
        if (row.EndedAt is not null)
            return;
        row.Status = status;
        row.EndedAt = DateTimeOffset.UtcNow;
        row.TerminationReason = reason;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertAsync(AgentActivityWindow window, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.WindowEnd <= window.WindowStart)
            throw new ArgumentOutOfRangeException(nameof(window));
        using JsonDocument _ = JsonDocument.Parse(window.ReasonCountsJson);
        string playbookKey = window.PlaybookId ?? string.Empty;
        await using TradingHubDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        AgentActivityWindowEntity? row = await context.AgentActivityWindows.SingleOrDefaultAsync(value =>
                value.RuntimeSessionId == window.RuntimeSessionId &&
                value.AgentInstanceId == window.AgentInstanceId &&
                value.InstrumentId == window.InstrumentId &&
                value.StrategyId == window.StrategyId &&
                value.PlaybookKey == playbookKey &&
                value.WindowStart == window.WindowStart,
            cancellationToken).ConfigureAwait(false);
        row ??= new AgentActivityWindowEntity
        {
            RuntimeSessionId = window.RuntimeSessionId,
            AgentInstanceId = window.AgentInstanceId,
            InstrumentId = window.InstrumentId,
            StrategyId = window.StrategyId,
            PlaybookKey = playbookKey,
            WindowStart = window.WindowStart,
            ReasonCountsJson = "{}"
        };
        if (context.Entry(row).State == EntityState.Detached)
            context.AgentActivityWindows.Add(row);
        row.WindowEnd = window.WindowEnd;
        row.EvaluationsObserved = window.EvaluationsObserved;
        row.NoSetupCount = window.NoSetupCount;
        row.WarmupCount = window.WarmupCount;
        row.BuyCount = window.BuyCount;
        row.SellCount = window.SellCount;
        row.HoldCount = window.HoldCount;
        row.CandidatesCreated = window.CandidatesCreated;
        row.CandidatesRejected = window.CandidatesRejected;
        row.StateTransitions = window.StateTransitions;
        row.TimeoutCount = window.TimeoutCount;
        row.ErrorCount = window.ErrorCount;
        row.ReasonCountsJson = window.ReasonCountsJson;
        row.MeanEvaluationMilliseconds = window.MeanEvaluationMilliseconds;
        row.MinEvaluationMilliseconds = window.MinEvaluationMilliseconds;
        row.MaxEvaluationMilliseconds = window.MaxEvaluationMilliseconds;
        row.MeanCandidateConfidence = window.MeanCandidateConfidence;
        row.LatestSnapshotVersion = window.LatestSnapshotVersion;
        row.LatestMarketTime = window.LatestMarketTime;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
