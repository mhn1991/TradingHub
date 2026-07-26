using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agent.Models;
using DBManager.Postgres;
using LiveTrading.Agents;
using LiveTrading.Configuration;
using LiveTrading.Reconciliation;
using Microsoft.EntityFrameworkCore;
using TradingCore.Pipeline;
using TradingObservability.Abstractions;

namespace LiveTradingHost.Observability;

public sealed record LiveRuntimeIdentity(Guid DeploymentId, Guid RuntimeSessionId);

/// <summary>
/// Coalesces ordinary live-agent observations into one-minute activity windows. Only candidates,
/// failures/timeouts, and actual state changes use the detailed telemetry path.
/// </summary>
public sealed class LiveRuntimeObservability(
    LiveRuntimeIdentity identity,
    IRuntimeSessionStore sessions,
    IAgentActivityStore activity,
    ITradingTelemetryWriter telemetry,
    IDbContextFactory<TradingHubDbContext> contextFactory,
    TimeProvider timeProvider)
{
    private readonly object _sync = new();
    private readonly Dictionary<(Guid AgentId, string Playbook, DateTimeOffset Start), WindowBuilder> _windows = [];
    private IReadOnlyDictionary<string, long> _instrumentIds = new Dictionary<string, long>();
    private bool _started;

    public bool Started => _started;
    public Guid RuntimeSessionId => identity.RuntimeSessionId;

    public Guid AgentInstanceId(AgentInstanceKey key)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{identity.DeploymentId:N}|{key.DeploymentId}|{key.Instrument.Value}|{key.StrategyId}|" +
            $"{key.PolicyBundleId:N}|{key.Revision}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    public async Task StartAsync(
        IReadOnlyCollection<AgentInstanceState> instances,
        string hostInstanceId,
        CancellationToken cancellationToken)
    {
        if (_started)
            return;

        string[] canonicalKeys = instances.Select(item => item.Key.Instrument.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        await using (TradingHubDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken)
                         .ConfigureAwait(false))
        {
            _instrumentIds = await db.Instruments.AsNoTracking()
                .Where(item => canonicalKeys.Contains(item.CanonicalKey))
                .ToDictionaryAsync(item => item.CanonicalKey, item => item.InstrumentId,
                    StringComparer.Ordinal, cancellationToken)
                .ConfigureAwait(false);
        }

        string[] missing = canonicalKeys.Where(key => !_instrumentIds.ContainsKey(key)).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"Live instruments are missing from PostgreSQL reference data: {string.Join(", ", missing)}.");
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        string configurationHash = ConfigurationHash(instances);
        RuntimeSessionKind kind = instances.Any(item => item.Assignment.Mode is
            StrategyActivationMode.ManualApproval or StrategyActivationMode.Automatic)
            ? RuntimeSessionKind.LiveExecutable
            : RuntimeSessionKind.LiveShadow;
        await sessions.StartAsync(new RuntimeSessionRegistration
        {
            RuntimeSessionId = identity.RuntimeSessionId,
            Kind = kind,
            DeploymentId = identity.DeploymentId,
            HostInstanceId = hostInstanceId,
            MachineName = Environment.MachineName,
            ProcessId = Environment.ProcessId,
            ConfigurationHash = configurationHash,
            StartedAt = now
        }, cancellationToken).ConfigureAwait(false);
        _started = true;

        await telemetry.WriteAsync(new TradingTelemetryEvent
        {
            EventId = Guid.NewGuid(),
            Type = TradingTelemetryType.RuntimeLifecycle,
            Severity = TradingTelemetrySeverity.Information,
            OccurredAt = now,
            ObservedAt = now,
            ReasonCode = "runtime.started",
            Correlation = Correlation(),
            PayloadJson = JsonSerializer.Serialize(new
            {
                kind = kind.ToString(),
                agentCount = instances.Count,
                configurationHash
            })
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task ObserveAsync(
        IReadOnlyList<AgentEvaluationAudit> audits,
        CancellationToken cancellationToken)
    {
        if (!_started || audits.Count == 0)
            return;

        var closed = new List<WindowBuilder>();
        lock (_sync)
        {
            foreach (AgentEvaluationAudit audit in audits)
            {
                Guid agentId = AgentInstanceId(audit.Agent);
                DateTimeOffset start = MinuteStart(audit.AvailableAt);
                string playbook = audit.PlaybookId ?? string.Empty;
                var key = (agentId, playbook, start);
                if (!_windows.TryGetValue(key, out WindowBuilder? window))
                {
                    window = new WindowBuilder(
                        identity.RuntimeSessionId,
                        agentId,
                        RequireInstrumentId(audit.Agent.Instrument.Value),
                        audit.Agent.StrategyId,
                        audit.PlaybookId,
                        start);
                    _windows.Add(key, window);
                }
                window.Add(audit);

                foreach (var expired in _windows
                             .Where(item => item.Key.AgentId == agentId && item.Key.Start < start)
                             .Select(item => item.Key)
                             .ToArray())
                {
                    closed.Add(_windows[expired]);
                    _windows.Remove(expired);
                }
            }
        }

        foreach (WindowBuilder window in closed)
            await activity.UpsertAsync(window.Build(), cancellationToken).ConfigureAwait(false);

        foreach (AgentEvaluationAudit audit in audits.Where(IsDetailedEvent))
            await WriteDetailedAsync(audit, cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteAsync(
        RuntimeSessionStatus status,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (!_started)
            return;
        try
        {
            await FlushActivityAsync(cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = timeProvider.GetUtcNow();
            await telemetry.WriteAsync(new TradingTelemetryEvent
            {
                EventId = Guid.NewGuid(),
                Type = TradingTelemetryType.RuntimeLifecycle,
                Severity = status == RuntimeSessionStatus.Failed
                    ? TradingTelemetrySeverity.Error
                    : TradingTelemetrySeverity.Information,
                OccurredAt = now,
                ObservedAt = now,
                ReasonCode = $"runtime.{status.ToString().ToLowerInvariant()}",
                Correlation = Correlation(),
                PayloadJson = JsonSerializer.Serialize(new { status = status.ToString(), reason = Bound(reason, 2_048) })
            }, cancellationToken).ConfigureAwait(false);
            await telemetry.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await sessions.EndAsync(identity.RuntimeSessionId, status, Bound(reason, 2_048), cancellationToken)
                .ConfigureAwait(false);
            _started = false;
        }
    }

    private async Task FlushActivityAsync(CancellationToken cancellationToken)
    {
        WindowBuilder[] pending;
        lock (_sync)
        {
            pending = _windows.Values.ToArray();
            _windows.Clear();
        }
        foreach (WindowBuilder window in pending)
            await activity.UpsertAsync(window.Build(), cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteDetailedAsync(AgentEvaluationAudit audit, CancellationToken cancellationToken)
    {
        DateTimeOffset observedAt = timeProvider.GetUtcNow();
        Guid.TryParse(audit.DecisionId, out Guid decisionId);
        var boundedDiagnostics = audit.Diagnostics.Take(16).ToDictionary(
            item => Bound(item.Key, 128)!,
            item => Bound(item.Value, 256)!,
            StringComparer.Ordinal);
        await telemetry.WriteAsync(new TradingTelemetryEvent
        {
            EventId = Guid.NewGuid(),
            Type = TradingTelemetryType.AgentStateTransition,
            Severity = audit.Outcome switch
            {
                AgentEvaluationOutcome.Failed => TradingTelemetrySeverity.Error,
                AgentEvaluationOutcome.TimedOut => TradingTelemetrySeverity.Warning,
                _ => TradingTelemetrySeverity.Information
            },
            OccurredAt = audit.AvailableAt,
            ObservedAt = observedAt,
            ReasonCode = Bound(audit.ReasonCode, 128) ?? audit.Outcome.ToString(),
            Correlation = Correlation() with
            {
                AgentInstanceId = AgentInstanceId(audit.Agent),
                InstrumentId = RequireInstrumentId(audit.Agent.Instrument.Value),
                DecisionId = decisionId == Guid.Empty ? null : decisionId,
                CandidateId = Bound(audit.CandidateId, 256)
            },
            PayloadJson = JsonSerializer.Serialize(new
            {
                audit.DecisionEpoch,
                audit.SnapshotVersion,
                audit.AnalysisProfileHash,
                activationMode = audit.ActivationMode.ToString(),
                outcome = audit.Outcome.ToString(),
                audit.PipelineStatus,
                action = audit.Action?.ToString(),
                audit.StateBefore,
                audit.StateAfter,
                audit.Confidence,
                audit.PlaybookId,
                audit.EvaluationMilliseconds,
                diagnostics = boundedDiagnostics,
                error = Bound(audit.Error, 1_024)
            })
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records a reconciliation outcome. Of TradingTelemetryType's nine values, only
    /// RuntimeLifecycle and AgentStateTransition were ever actually emitted before this -
    /// querying ITradingReportQueryService for why a reconciliation blocked new entries, or
    /// what an operator would have needed to review, returned nothing despite the schema having
    /// an exact category for it.</summary>
    public async Task RecordReconciliationAsync(
        BrokerReconciliationReport report,
        CancellationToken cancellationToken)
    {
        if (!_started)
            return;
        DateTimeOffset observedAt = timeProvider.GetUtcNow();
        await telemetry.WriteAsync(new TradingTelemetryEvent
        {
            EventId = Guid.NewGuid(),
            Type = TradingTelemetryType.ReconciliationStateChange,
            Severity = report.RequiresOperatorReview
                ? TradingTelemetrySeverity.Warning
                : TradingTelemetrySeverity.Information,
            OccurredAt = report.CompletedAt,
            ObservedAt = observedAt,
            ReasonCode = report.Trigger.ToString(),
            Correlation = Correlation(),
            PayloadJson = JsonSerializer.Serialize(new
            {
                report.ReconciliationId,
                trigger = report.Trigger.ToString(),
                report.CanOpenNewEntries,
                report.RequiresOperatorReview,
                differenceCount = report.Differences.Count,
                differences = report.Differences.Take(32)
            })
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records an operational incident (a failure that isn't attributable to one
    /// agent's evaluation - e.g. a reconciliation cycle itself throwing). See the doc comment on
    /// RecordReconciliationAsync - this closes the same "defined but never emitted" gap for the
    /// Incident category.</summary>
    public async Task RecordIncidentAsync(
        string reasonCode,
        string message,
        Exception? exception,
        CancellationToken cancellationToken)
    {
        if (!_started)
            return;
        DateTimeOffset observedAt = timeProvider.GetUtcNow();
        await telemetry.WriteAsync(new TradingTelemetryEvent
        {
            EventId = Guid.NewGuid(),
            Type = TradingTelemetryType.Incident,
            Severity = TradingTelemetrySeverity.Critical,
            OccurredAt = observedAt,
            ObservedAt = observedAt,
            ReasonCode = Bound(reasonCode, 128) ?? "incident",
            Correlation = Correlation(),
            PayloadJson = JsonSerializer.Serialize(new
            {
                message = Bound(message, 2_048),
                exception = Bound(exception?.ToString(), 4_096)
            })
        }, cancellationToken).ConfigureAwait(false);
    }

    private TelemetryCorrelation Correlation() => new()
    {
        RuntimeSessionId = identity.RuntimeSessionId,
        DeploymentId = identity.DeploymentId
    };

    private long RequireInstrumentId(string canonicalKey) =>
        _instrumentIds.TryGetValue(canonicalKey, out long instrumentId)
            ? instrumentId
            : throw new InvalidOperationException($"Instrument '{canonicalKey}' has no PostgreSQL reference id.");

    private static bool IsDetailedEvent(AgentEvaluationAudit audit) =>
        audit.CandidateId is not null ||
        audit.Outcome is AgentEvaluationOutcome.Failed or AgentEvaluationOutcome.TimedOut ||
        !string.Equals(audit.StateBefore, audit.StateAfter, StringComparison.Ordinal);

    private static DateTimeOffset MinuteStart(DateTimeOffset value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0, value.Offset);

    private static string ConfigurationHash(IEnumerable<AgentInstanceState> instances)
    {
        string canonical = string.Join('\n', instances
            .OrderBy(item => item.Key.Instrument.Value, StringComparer.Ordinal)
            .ThenBy(item => item.Key.StrategyId, StringComparer.Ordinal)
            .ThenBy(item => item.Key.PolicyBundleId)
            .ThenBy(item => item.Key.Revision)
            .Select(item =>
                $"{item.Key.Instrument.Value}|{item.Key.StrategyId}|{item.Key.PolicyBundleId:N}|" +
                $"{item.Key.Revision}|{item.Assignment.Mode}|{item.AnalysisProfile?.ProfileHash}|" +
                item.PolicyBundle.ConfigurationHash));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string? Bound(string? value, int maximumLength) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Length <= maximumLength ? value : value[..maximumLength];

    private sealed class WindowBuilder(
        Guid runtimeSessionId,
        Guid agentInstanceId,
        long instrumentId,
        string strategyId,
        string? playbookId,
        DateTimeOffset windowStart)
    {
        private readonly Dictionary<string, long> _reasons = new(StringComparer.Ordinal);
        private long _evaluations;
        private long _noSetup;
        private long _warmup;
        private long _buy;
        private long _sell;
        private long _hold;
        private long _candidates;
        private long _rejected;
        private long _transitions;
        private long _timeouts;
        private long _errors;
        private double _durationTotal;
        private double _durationMinimum = double.MaxValue;
        private double _durationMaximum;
        private decimal _confidenceTotal;
        private long _confidenceCount;
        private long? _latestSnapshot;
        private DateTimeOffset? _latestMarketTime;

        public void Add(AgentEvaluationAudit audit)
        {
            _evaluations++;
            _durationTotal += audit.EvaluationMilliseconds;
            _durationMinimum = Math.Min(_durationMinimum, audit.EvaluationMilliseconds);
            _durationMaximum = Math.Max(_durationMaximum, audit.EvaluationMilliseconds);
            _latestSnapshot = audit.SnapshotVersion;
            _latestMarketTime = audit.AvailableAt;
            if (audit.Action == AgentAction.Buy) _buy++;
            else if (audit.Action == AgentAction.Sell) _sell++;
            else if (audit.Action == AgentAction.Observe) _hold++;
            if (audit.Action is null or AgentAction.Observe) _noSetup++;
            if (audit.CandidateId is not null)
            {
                _candidates++;
                if (audit.Confidence is { } confidence)
                {
                    _confidenceTotal += confidence;
                    _confidenceCount++;
                }
            }
            if (audit.PipelineStatus?.StartsWith("Rejected", StringComparison.Ordinal) == true ||
                audit.PipelineStatus?.StartsWith("Delayed", StringComparison.Ordinal) == true)
                _rejected++;
            if (!string.Equals(audit.StateBefore, audit.StateAfter, StringComparison.Ordinal)) _transitions++;
            if (audit.Outcome == AgentEvaluationOutcome.TimedOut) _timeouts++;
            if (audit.Outcome == AgentEvaluationOutcome.Failed) _errors++;
            string reason = Bound(audit.ReasonCode, 128) ?? audit.PipelineStatus ?? "Unspecified";
            _reasons[reason] = _reasons.GetValueOrDefault(reason) + 1;
            if (reason.Contains("warmup", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("notready", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("not ready", StringComparison.OrdinalIgnoreCase))
                _warmup++;
        }

        public AgentActivityWindow Build() => new()
        {
            RuntimeSessionId = runtimeSessionId,
            AgentInstanceId = agentInstanceId,
            InstrumentId = instrumentId,
            StrategyId = strategyId,
            PlaybookId = playbookId,
            WindowStart = windowStart,
            WindowEnd = windowStart.AddMinutes(1),
            EvaluationsObserved = _evaluations,
            NoSetupCount = _noSetup,
            WarmupCount = _warmup,
            BuyCount = _buy,
            SellCount = _sell,
            HoldCount = _hold,
            CandidatesCreated = _candidates,
            CandidatesRejected = _rejected,
            StateTransitions = _transitions,
            TimeoutCount = _timeouts,
            ErrorCount = _errors,
            ReasonCountsJson = JsonSerializer.Serialize(_reasons.OrderBy(item => item.Key)
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)),
            MeanEvaluationMilliseconds = _evaluations == 0 ? 0 : _durationTotal / _evaluations,
            MinEvaluationMilliseconds = _durationMinimum == double.MaxValue ? 0 : _durationMinimum,
            MaxEvaluationMilliseconds = _durationMaximum,
            MeanCandidateConfidence = _confidenceCount == 0 ? null : (double)(_confidenceTotal / _confidenceCount),
            LatestSnapshotVersion = _latestSnapshot,
            LatestMarketTime = _latestMarketTime
        };
    }
}
