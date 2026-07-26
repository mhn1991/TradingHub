using Brokers.Models;
using LiveTrading.Account;
using LiveTrading.Agents;
using LiveTrading.Execution;
using LiveTrading.Configuration;
using LiveTrading.ManualApproval;
using LiveTrading.Persistence;
using LiveTrading.Portfolio;
using LiveTrading.Reconciliation;
using LiveTrading.Registry;
using LiveTrading.Shadow.Outcomes;
using PortfolioManager.Risk;
using RiskManager.Safety;
using TradingCore.Pipeline;

namespace LiveTrading.Runtime;

public sealed record LiveExecutionRuntimeOptions
{
    /// <summary>Master guard. Defaults false so merely configuring credentials cannot place orders.</summary>
    public bool BrokerWritesEnabled { get; init; }
    public bool AutomaticExecutionEnabled { get; init; }
    /// <summary>Enable only after explicit OANDA Practice partial-close certification.</summary>
    public bool PartialCloseEnabled { get; init; }
    /// <summary>Enable only after explicit OANDA Practice dependent-stop replacement certification.</summary>
    public bool DynamicStopReplacementEnabled { get; init; }
    public TimeSpan ManualApprovalLifetime { get; init; } = TimeSpan.FromMinutes(3);
    public decimal MaximumManualApprovalPriceDriftBasisPoints { get; init; } = 5m;
    public bool RejectEpochWhenAnyMarketUnavailable { get; init; } = true;
    public decimal MaximumMarginUsagePercent { get; init; } = 15m;
    public int MaximumOpenPositions { get; init; } = 2;

    public void Validate()
    {
        if (ManualApprovalLifetime <= TimeSpan.Zero ||
            MaximumManualApprovalPriceDriftBasisPoints is < 0m or > 100m ||
            MaximumMarginUsagePercent is <= 0m or > 100m ||
            MaximumOpenPositions < 1 ||
            AutomaticExecutionEnabled && !BrokerWritesEnabled ||
            (PartialCloseEnabled || DynamicStopReplacementEnabled) && !BrokerWritesEnabled)
        {
            throw new ArgumentOutOfRangeException(nameof(LiveExecutionRuntimeOptions));
        }
    }
}

public sealed record LiveRuntimeSnapshot
{
    public required bool BrokerWritesEnabled { get; init; }
    public required bool AutomaticExecutionEnabled { get; init; }
    public required bool PartialCloseEnabled { get; init; }
    public required bool DynamicStopReplacementEnabled { get; init; }
    public required bool CanOpenNewEntries { get; init; }
    public required TradingSafetySnapshot Safety { get; init; }
    public BrokerReconciliationReport? LastReconciliation { get; init; }
    public required LiveAccountStateSnapshot? Account { get; init; }
    public required LiveRegistrySnapshot Registry { get; init; }
    public required PortfolioReservationSnapshot Reservations { get; init; }
    public required IReadOnlyList<ManualApprovalCandidate> ManualCandidates { get; init; }
    public required IReadOnlyList<PortfolioDecision> RecentPortfolioDecisions { get; init; }
    public required LiveShadowOutcomeSnapshot Shadow { get; init; }
    public required DateTimeOffset AsOf { get; init; }
}

public interface ILiveTradingRuntimeCoordinator
{
    LiveRuntimeSnapshot Snapshot { get; }
    Task RecoverAsync(CancellationToken cancellationToken);
    Task ProcessEpochAsync(
        LiveDecisionEpochBatch batch,
        Func<LiveTradeCandidate, LiveTradingPolicyBundle> policyResolver,
        Func<LiveTradeCandidate, StrategyActivationMode> activationModeResolver,
        CancellationToken cancellationToken);
    Task<LiveExecutionResult> ApproveAndExecuteAsync(
        ManualApprovalRequest request,
        CancellationToken cancellationToken);
    Task<ManualApprovalCandidate> RejectAsync(
        string candidateId,
        string reviewedBy,
        string reason,
        CancellationToken cancellationToken);
    Task<BrokerReconciliationReport> ReconcileAsync(
        ReconciliationTrigger trigger,
        CancellationToken cancellationToken);
    Task PauseAsync(string reason, CancellationToken cancellationToken);
    Task ResumeAsync(CancellationToken cancellationToken);
    Task CancelPendingEntriesAsync(CancellationToken cancellationToken);
    Task FlattenAsync(string reason, CancellationToken cancellationToken);
    Task<PositionReductionResult> ReducePositionAsync(
        string positionId,
        decimal quantity,
        DateTimeOffset expectedPositionUpdatedAt,
        string reason,
        CancellationToken cancellationToken);
    Task<ProtectiveStopAmendmentResult> AmendProtectiveStopAsync(
        string positionId,
        decimal proposedStopPrice,
        DateTimeOffset expectedPositionUpdatedAt,
        string reason,
        CancellationToken cancellationToken);
    Task SaveCheckpointAsync(bool cleanShutdown, CancellationToken cancellationToken);

    /// <summary>
    /// The "controlled decision boundary" hot-swap activation point: holds the same serial lock
    /// <see cref="ProcessEpochAsync"/> holds, so a policy revision can only ever become "current"
    /// strictly between two decision epochs, never mid-batch. Delegates the actual swap to
    /// <see cref="ILivePolicyRegistry.Activate"/>; already-open positions are unaffected (see
    /// <c>LivePositionManagementService</c>'s by-revision continuity lookup).
    /// </summary>
    Task ActivatePolicyRevisionAsync(
        AgentInstanceKey key, LivePolicyRegistration registration, CancellationToken cancellationToken);
}

/// <summary>
/// The account-wide live decision boundary. It is the only component that turns a closed Agent
/// epoch into reservations/manual approvals/autonomous demo submissions, and the only API-facing
/// component allowed to consume an approval. Broker writes remain guarded by an explicit config
/// flag and OANDA Practice construction.
/// </summary>
public sealed class LiveTradingRuntimeCoordinator : ILiveTradingRuntimeCoordinator
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly LiveExecutionRuntimeOptions _options;
    private readonly ILiveAccountStateService _account;
    private readonly ILiveOpportunityCoordinator _opportunities;
    private readonly IManualApprovalStore _manualApprovals;
    private readonly ILiveExecutionGateway _execution;
    private readonly ILiveOrderPositionRegistry _registry;
    private readonly IPortfolioReservationBook _reservations;
    private readonly ILiveBrokerReconciler _reconciler;
    private readonly ILiveBrokerEventProcessor _brokerEvents;
    private readonly ITradingSafetyController _safety;
    private readonly ILiveTradingPersistence _persistence;
    private readonly ILivePolicyRegistry _policies;
    private readonly ILiveShadowOutcomeService _shadowOutcomes;
    private readonly TimeProvider _timeProvider;
    private readonly Queue<PortfolioDecision> _recentDecisions = new();
    private BrokerReconciliationReport? _lastReconciliation;

    public LiveTradingRuntimeCoordinator(
        LiveExecutionRuntimeOptions options,
        ILiveAccountStateService account,
        ILiveOpportunityCoordinator opportunities,
        IManualApprovalStore manualApprovals,
        ILiveExecutionGateway execution,
        ILiveOrderPositionRegistry registry,
        IPortfolioReservationBook reservations,
        ILiveBrokerReconciler reconciler,
        ILiveBrokerEventProcessor brokerEvents,
        ITradingSafetyController safety,
        ILiveTradingPersistence persistence,
        ILivePolicyRegistry policies,
        ILiveShadowOutcomeService shadowOutcomes,
        TimeProvider timeProvider)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _account = account;
        _opportunities = opportunities;
        _manualApprovals = manualApprovals;
        _execution = execution;
        _registry = registry;
        _reservations = reservations;
        _reconciler = reconciler;
        _brokerEvents = brokerEvents;
        _safety = safety;
        _persistence = persistence;
        _policies = policies;
        _shadowOutcomes = shadowOutcomes;
        _timeProvider = timeProvider;
    }

    public LiveRuntimeSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                bool reconciliationHealthy = _lastReconciliation?.CanOpenNewEntries ?? false;
                return new LiveRuntimeSnapshot
                {
                    BrokerWritesEnabled = _options.BrokerWritesEnabled,
                    AutomaticExecutionEnabled = _options.AutomaticExecutionEnabled,
                    PartialCloseEnabled = _options.PartialCloseEnabled,
                    DynamicStopReplacementEnabled = _options.DynamicStopReplacementEnabled,
                    CanOpenNewEntries = _options.BrokerWritesEnabled && _safety.CanOpenNewTrades &&
                        reconciliationHealthy && _account.Snapshot is { IsStale: false },
                    Safety = _safety.Snapshot,
                    LastReconciliation = _lastReconciliation,
                    Account = _account.Snapshot,
                    Registry = _registry.Snapshot,
                    Reservations = _reservations.Snapshot,
                    ManualCandidates = _manualApprovals.Snapshot,
                    RecentPortfolioDecisions = _recentDecisions.ToArray(),
                    Shadow = _shadowOutcomes.Snapshot,
                    AsOf = _timeProvider.GetUtcNow()
                };
            }
        }
    }

    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LiveEngineCheckpoint? checkpoint = await _persistence.LoadCheckpointAsync(cancellationToken)
                .ConfigureAwait(false);
            if (checkpoint is not null)
            {
                _registry.Restore(checkpoint.Registry);
                _reservations.Restore(checkpoint.Reservations);
                _manualApprovals.Restore(checkpoint.ManualApprovals);
                _safety.Restore(checkpoint.Safety, _timeProvider.GetUtcNow());
                await _shadowOutcomes.RestoreAsync(checkpoint.ShadowOutcomes, cancellationToken)
                    .ConfigureAwait(false);
            }
            LiveAccountStateSnapshot account = await _account.RefreshAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(checkpoint?.LastBrokerTransactionId))
            {
                await _brokerEvents.ReplaySinceAsync(
                    checkpoint.LastBrokerTransactionId,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // A first deployment has no local event cursor. Reconciliation adopts no unknown
                // positions automatically; starting at the account's current transaction boundary
                // avoids replaying the account's entire historical lifetime.
                _brokerEvents.InitializeCursor(account.Account.LastTransactionId);
            }
            _safety.ObserveEquity(account.Equity, _timeProvider.GetUtcNow());
            BrokerReconciliationReport report = await _reconciler.ReconcileAsync(
                ReconciliationTrigger.Startup,
                cancellationToken).ConfigureAwait(false);
            SetReconciliation(report);
            if (!report.CanOpenNewEntries)
            {
                _safety.Pause(
                    "Startup reconciliation requires operator review; new entries remain paused.",
                    _timeProvider.GetUtcNow());
            }
            await PersistAsync("reconciliation", report, cancellationToken).ConfigureAwait(false);
            await SaveCheckpointCoreAsync(cleanShutdown: false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _serial.Release();
        }
    }

    public async Task ProcessEpochAsync(
        LiveDecisionEpochBatch batch,
        Func<LiveTradeCandidate, LiveTradingPolicyBundle> policyResolver,
        Func<LiveTradeCandidate, StrategyActivationMode> activationModeResolver,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(policyResolver);
        ArgumentNullException.ThrowIfNull(activationModeResolver);
        await PersistAsync("decision-epochs", batch, cancellationToken).ConfigureAwait(false);
        if (batch.OrderedCandidates.Count == 0)
            return;

        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var activationModes = batch.OrderedCandidates.ToDictionary(
                candidate => candidate.CandidateId,
                activationModeResolver,
                StringComparer.Ordinal);
            LiveTradeCandidate[] executableCandidates = batch.OrderedCandidates
                .Where(candidate => activationModes[candidate.CandidateId] is
                    StrategyActivationMode.ManualApproval or StrategyActivationMode.Automatic)
                .ToArray();
            foreach (LiveTradeCandidate candidate in batch.OrderedCandidates.Where(candidate =>
                         activationModes[candidate.CandidateId] is
                             StrategyActivationMode.ObserveOnly or StrategyActivationMode.Shadow))
            {
                StrategyActivationMode mode = activationModes[candidate.CandidateId];
                _account.TryGetQuote(candidate.Instrument, out var quote);
                ShadowAdmissionResult shadow = await _shadowOutcomes.AdmitAsync(
                    candidate,
                    mode,
                    quote,
                    cancellationToken).ConfigureAwait(false);
                PortfolioDecision nonExecuting = new()
                {
                    Candidate = candidate,
                    Approved = false,
                    ReasonCode = "NonExecutingDeploymentMode",
                    Explanation = mode == StrategyActivationMode.Shadow
                        ? $"Shadow outcome: {shadow.Admission.Disposition}. No capital was reserved and no broker order was submitted."
                        : "ObserveOnly recorded decision diagnostics without opening a paper or real position."
                };
                RecordDecision(nonExecuting);
                // Durable, not just the 500-entry in-memory ring buffer: ObserveOnly/Shadow are
                // the modes an operator runs first specifically to review what the strategy
                // would have done, so their diagnostic trail must survive a restart the same way
                // an executed decision's does.
                await PersistAsync("portfolio-decisions", nonExecuting, cancellationToken).ConfigureAwait(false);
            }
            if (executableCandidates.Length == 0)
            {
                await SaveCheckpointCoreAsync(cleanShutdown: false, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!_options.BrokerWritesEnabled)
            {
                foreach (LiveTradeCandidate candidate in executableCandidates)
                {
                    PortfolioDecision decision = new()
                    {
                        Candidate = candidate,
                        Approved = false,
                        ReasonCode = "BrokerWritesDisabled",
                        Explanation = "The Agent is executable but the deployment-level broker write guard is disabled."
                    };
                    RecordDecision(decision);
                    await PersistAsync("portfolio-decisions", decision, cancellationToken).ConfigureAwait(false);
                }
                return;
            }

            if (_options.RejectEpochWhenAnyMarketUnavailable &&
                (batch.UnavailableInstruments.Count > 0 ||
                 batch.FailedAgents.Count > 0 ||
                 batch.TimedOutAgents.Count > 0 ||
                 batch.MissingAgents.Count > 0))
            {
                foreach (LiveTradeCandidate candidate in executableCandidates)
                {
                    PortfolioDecision decision = new()
                    {
                        Candidate = candidate,
                        Approved = false,
                        ReasonCode = "IncompleteDecisionEpoch",
                        Explanation = "At least one expected market or agent failed to complete the deterministic epoch barrier."
                    };
                    RecordDecision(decision);
                    await PersistAsync("portfolio-decisions", decision, cancellationToken).ConfigureAwait(false);
                }
                return;
            }

            if (!_safety.CanOpenNewTrades || _lastReconciliation?.CanOpenNewEntries != true)
            {
                foreach (LiveTradeCandidate candidate in executableCandidates)
                {
                    PortfolioDecision decision = new()
                    {
                        Candidate = candidate,
                        Approved = false,
                        ReasonCode = "LiveSafetyPaused",
                        Explanation = "Account safety or reconciliation currently forbids new entries."
                    };
                    RecordDecision(decision);
                    await PersistAsync("portfolio-decisions", decision, cancellationToken).ConfigureAwait(false);
                }
                return;
            }

            LiveAccountStateSnapshot account = await _account.RefreshAsync(cancellationToken).ConfigureAwait(false);
            _safety.ObserveEquity(account.Equity, _timeProvider.GetUtcNow());
            if (!_safety.CanOpenNewTrades)
                return;

            LiveOpportunityEvaluationContext context = _account.CreateOpportunityContext(
                executableCandidates,
                policyResolver,
                _registry,
                _reservations,
                _safety);
            IReadOnlyList<PortfolioDecision> decisions = await _opportunities.EvaluateEpochAsync(
                executableCandidates,
                context,
                cancellationToken).ConfigureAwait(false);
            foreach (PortfolioDecision decision in decisions)
            {
                RecordDecision(decision);
                await PersistAsync("portfolio-decisions", decision, cancellationToken).ConfigureAwait(false);
                if (!decision.Approved || decision.ApprovedDecision is null)
                    continue;

                switch (activationModes[decision.Candidate.CandidateId])
                {
                    case StrategyActivationMode.ManualApproval:
                        ManualApprovalCandidate item = _manualApprovals.Add(
                            decision.ApprovedDecision,
                            _options.ManualApprovalLifetime);
                        await PersistAsync("manual-candidates", item, cancellationToken).ConfigureAwait(false);
                        break;
                    case StrategyActivationMode.Automatic when _options.AutomaticExecutionEnabled:
                        EnsureBrokerWritesEnabled();
                        LiveExecutionResult result = await _execution.SubmitEntryAsync(
                            decision.ApprovedDecision,
                            cancellationToken).ConfigureAwait(false);
                        await PersistAsync("orders", result, cancellationToken).ConfigureAwait(false);
                        await HandleExecutionUncertaintyAsync(result, cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        _reservations.Release(
                            decision.ApprovedDecision.ReservationId,
                            PortfolioReleaseReason.Manual);
                        break;
                }
            }
            await SaveCheckpointCoreAsync(cleanShutdown: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _safety.Trip(
                SafetyTripReason.ExecutionFailure,
                $"Decision epoch processing failed: {ex.Message}",
                _timeProvider.GetUtcNow());
            await PersistAsync("safety-events", _safety.Snapshot, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _serial.Release();
        }
    }

    public async Task<LiveExecutionResult> ApproveAndExecuteAsync(
        ManualApprovalRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureBrokerWritesEnabled();
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ManualApprovalCandidate pending = _manualApprovals.Snapshot.SingleOrDefault(item =>
                item.Decision.Candidate.CandidateId == request.CandidateId) ??
                throw new KeyNotFoundException($"Candidate '{request.CandidateId}' was not found.");
            await ValidateManualCandidateAsync(pending, cancellationToken).ConfigureAwait(false);
            if (pending.State == ManualApprovalState.Pending)
            {
                ManualApprovalCandidate approved = _manualApprovals.Approve(request);
                await PersistAsync("manual-candidates", approved, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (pending.State != ManualApprovalState.Approved ||
                    !string.Equals(pending.CandidateFingerprint, request.CandidateFingerprint, StringComparison.Ordinal) ||
                    !string.Equals(pending.ReviewedBy, request.ApprovedBy, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Only the same reviewer may resume an already-approved candidate after an interrupted request.");
                }
            }
            if (!_manualApprovals.TryConsume(
                    request.CandidateId,
                    request.CandidateFingerprint,
                    out PortfolioApprovedDecision decision))
            {
                throw new InvalidOperationException("The approved candidate could not be consumed exactly once.");
            }

            // Consumption is the commit point. A browser disconnect must not leave a consumed
            // candidate without a registered/submitted order, so the bounded broker operation and
            // its durable projection continue independently of the request-abort token.
            CancellationToken committedOperation = CancellationToken.None;
            LiveExecutionResult result = await _execution.SubmitEntryAsync(decision, committedOperation)
                .ConfigureAwait(false);
            await PersistAsync("orders", result, committedOperation).ConfigureAwait(false);
            await HandleExecutionUncertaintyAsync(result, committedOperation).ConfigureAwait(false);
            await SaveCheckpointCoreAsync(cleanShutdown: false, committedOperation).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _serial.Release();
        }
    }

    public async Task<ManualApprovalCandidate> RejectAsync(
        string candidateId,
        string reviewedBy,
        string reason,
        CancellationToken cancellationToken)
    {
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ManualApprovalCandidate rejected = _manualApprovals.Reject(candidateId, reviewedBy, reason);
            await PersistAsync("manual-candidates", rejected, cancellationToken).ConfigureAwait(false);
            await SaveCheckpointCoreAsync(cleanShutdown: false, cancellationToken).ConfigureAwait(false);
            return rejected;
        }
        finally
        {
            _serial.Release();
        }
    }

    public async Task<BrokerReconciliationReport> ReconcileAsync(
        ReconciliationTrigger trigger,
        CancellationToken cancellationToken)
    {
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _account.RefreshAsync(cancellationToken).ConfigureAwait(false);
            BrokerReconciliationReport report = await _reconciler.ReconcileAsync(trigger, cancellationToken)
                .ConfigureAwait(false);
            SetReconciliation(report);
            if (!report.CanOpenNewEntries)
            {
                _safety.Pause("Broker reconciliation requires operator review.", _timeProvider.GetUtcNow());
            }
            await PersistAsync("reconciliation", report, cancellationToken).ConfigureAwait(false);
            await SaveCheckpointCoreAsync(cleanShutdown: false, cancellationToken).ConfigureAwait(false);
            return report;
        }
        finally
        {
            _serial.Release();
        }
    }

    public async Task ActivatePolicyRevisionAsync(
        AgentInstanceKey key, LivePolicyRegistration registration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Holding the same lock ProcessEpochAsync holds guarantees this can only land
            // strictly between two decision epochs - never observed mid-batch by a candidate
            // already resolving policy for this key.
            _policies.Activate(key, registration);
            await PersistAsync("policy-activation-events", new
            {
                key.StrategyId,
                key.Instrument,
                registration.Policy.PolicyBundleId,
                registration.Policy.Revision,
                registration.Policy.ConfigurationHash,
                registration.Mode,
                ActivatedAt = _timeProvider.GetUtcNow()
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _serial.Release();
        }
    }

    public async Task PauseAsync(string reason, CancellationToken cancellationToken)
    {
        _safety.Pause(reason, _timeProvider.GetUtcNow());
        await PersistAsync("safety-events", _safety.Snapshot, cancellationToken).ConfigureAwait(false);
    }

    public async Task ResumeAsync(CancellationToken cancellationToken)
    {
        BrokerReconciliationReport report = await ReconcileAsync(ReconciliationTrigger.Resume, cancellationToken)
            .ConfigureAwait(false);
        if (!report.CanOpenNewEntries)
            throw new InvalidOperationException("Cannot resume while broker reconciliation is unresolved.");
        _safety.Resume(_timeProvider.GetUtcNow());
        await PersistAsync("safety-events", _safety.Snapshot, cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelPendingEntriesAsync(CancellationToken cancellationToken)
    {
        EnsureBrokerWritesEnabled();
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (LiveOrderRecord order in _registry.Snapshot.Orders.Where(item => item.State is
                         LiveOrderState.Accepted or LiveOrderState.SubmissionPending or
                         LiveOrderState.SubmissionUnknown or LiveOrderState.PartiallyFilled))
            {
                if (!string.IsNullOrWhiteSpace(order.BrokerOrderId))
                {
                    // Once cancellation starts, finish the bounded broker operation even if the
                    // caller disconnects; reconciliation below determines the authoritative state.
                    await _execution.CancelOrderAsync(order.BrokerOrderId, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }

            await _account.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
            await ReconcileWithoutLockAsync(ReconciliationTrigger.Manual, CancellationToken.None)
                .ConfigureAwait(false);
            await SaveCheckpointCoreAsync(cleanShutdown: false, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _serial.Release();
        }
    }

    public async Task FlattenAsync(string reason, CancellationToken cancellationToken)
    {
        EnsureBrokerWritesEnabled();
        _safety.Pause($"Flatten requested: {reason}", _timeProvider.GetUtcNow());
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (LivePositionRecord position in _registry.Snapshot.Positions)
            {
                PositionReductionResult result = await _execution.ReducePositionAsync(
                    position,
                    position.Quantity,
                    reason,
                    cancellationToken).ConfigureAwait(false);
                await PersistAsync("management-events", result, cancellationToken).ConfigureAwait(false);
                if (result.Certainty == ExecutionCertainty.Unknown)
                    break;
            }
        }
        finally
        {
            _serial.Release();
        }
        await ReconcileAsync(ReconciliationTrigger.Manual, cancellationToken).ConfigureAwait(false);
    }


    public async Task<PositionReductionResult> ReducePositionAsync(
        string positionId,
        decimal quantity,
        DateTimeOffset expectedPositionUpdatedAt,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(positionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        EnsureBrokerWritesEnabled();
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LivePositionRecord position = _registry.Snapshot.Positions.SingleOrDefault(item =>
                item.PositionId == positionId || item.BrokerTradeId == positionId) ??
                throw new KeyNotFoundException($"Owned position '{positionId}' was not found.");
            if (position.UpdatedAt != expectedPositionUpdatedAt)
            {
                throw new InvalidOperationException(
                    "The position changed after the operator loaded it; refresh before reducing or closing it.");
            }
            if (quantity <= 0m || quantity > position.Quantity)
                throw new ArgumentOutOfRangeException(nameof(quantity));
            if (quantity < position.Quantity && !_options.PartialCloseEnabled)
            {
                throw new InvalidOperationException(
                    "Partial close is implemented but disabled until OANDA Practice certification is explicitly enabled.");
            }
            PositionReductionResult result = await _execution.ReducePositionAsync(
                position,
                quantity,
                reason,
                cancellationToken).ConfigureAwait(false);
            await PersistAsync("management-events", result, cancellationToken).ConfigureAwait(false);
            if (result.Certainty == ExecutionCertainty.Unknown)
            {
                _safety.Pause(
                    "Manual position reduction has unknown broker certainty; entries are paused pending reconciliation.",
                    _timeProvider.GetUtcNow());
                await ReconcileWithoutLockAsync(ReconciliationTrigger.PartialClose, cancellationToken)
                    .ConfigureAwait(false);
            }
            await SaveCheckpointCoreAsync(cleanShutdown: false, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _serial.Release();
        }
    }

    public async Task<ProtectiveStopAmendmentResult> AmendProtectiveStopAsync(
        string positionId,
        decimal proposedStopPrice,
        DateTimeOffset expectedPositionUpdatedAt,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(positionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        EnsureBrokerWritesEnabled();
        if (!_options.DynamicStopReplacementEnabled)
        {
            throw new InvalidOperationException(
                "Dynamic stop replacement is implemented but disabled until OANDA Practice certification is explicitly enabled.");
        }
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LivePositionRecord position = _registry.Snapshot.Positions.SingleOrDefault(item =>
                item.PositionId == positionId || item.BrokerTradeId == positionId) ??
                throw new KeyNotFoundException($"Owned position '{positionId}' was not found.");
            if (position.UpdatedAt != expectedPositionUpdatedAt)
            {
                throw new InvalidOperationException(
                    "The position changed after the operator loaded it; refresh before amending its stop.");
            }
            if (!_account.TryGetQuote(position.Instrument, out var quote) || quote.IsStale || !quote.IsTradeable)
                throw new InvalidOperationException("A current executable quote is required to amend a stop.");
            if (position.AveragePrice is not > 0m || position.InitialStopPrice is not > 0m)
                throw new InvalidOperationException("Entry and initial stop prices are required to amend a stop.");
            decimal executable = position.Side == OrderSide.Buy ? quote.Bid : quote.Ask;
            decimal initialRisk = Math.Abs(position.AveragePrice.Value - position.InitialStopPrice.Value);
            decimal signedMove = position.Side == OrderSide.Buy
                ? executable - position.AveragePrice.Value
                : position.AveragePrice.Value - executable;
            decimal openProfitR = initialRisk > 0m ? signedMove / initialRisk : 0m;
            LiveAccountStateSnapshot? currentAccount = _account.Snapshot;
            decimal priceIncrement = currentAccount is not null &&
                currentAccount.InstrumentMetadata.TryGetValue(
                    position.Instrument,
                    out InstrumentTradingMetadata? metadata)
                ? metadata.PriceIncrement
                : position.Instrument.Value.EndsWith("/JPY", StringComparison.OrdinalIgnoreCase)
                    ? 0.001m
                    : 0.00001m;
            ProtectiveStopAmendmentResult result = await _execution.AmendProtectiveStopAsync(
                position,
                proposedStopPrice,
                executable,
                priceIncrement,
                openProfitR,
                StopAmendmentReason.Manual,
                reason,
                _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                cancellationToken).ConfigureAwait(false);
            await PersistAsync("management-events", result, cancellationToken).ConfigureAwait(false);
            if (result.Certainty == ExecutionCertainty.Unknown)
            {
                _safety.Pause(
                    "Manual stop amendment has unknown broker certainty; entries are paused pending reconciliation.",
                    _timeProvider.GetUtcNow());
                await ReconcileWithoutLockAsync(ReconciliationTrigger.StopAmendment, cancellationToken)
                    .ConfigureAwait(false);
            }
            await SaveCheckpointCoreAsync(cleanShutdown: false, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _serial.Release();
        }
    }

    public Task SaveCheckpointAsync(bool cleanShutdown, CancellationToken cancellationToken) =>
        SaveCheckpointCoreAsync(cleanShutdown, cancellationToken);

    private async Task ValidateManualCandidateAsync(
        ManualApprovalCandidate candidate,
        CancellationToken cancellationToken)
    {
        if (candidate.State is not (ManualApprovalState.Pending or ManualApprovalState.Approved))
            throw new InvalidOperationException($"Candidate is {candidate.State}, not approvable.");
        if (!_safety.CanOpenNewTrades || _lastReconciliation?.CanOpenNewEntries != true)
            throw new InvalidOperationException("Safety or reconciliation forbids new entries.");
        LiveAccountStateSnapshot account = await _account.RefreshAsync(cancellationToken).ConfigureAwait(false);
        _safety.ObserveEquity(account.Equity, _timeProvider.GetUtcNow());
        if (_registry.Snapshot.Positions.Any(position =>
                position.Instrument == candidate.Decision.Candidate.Instrument))
        {
            throw new InvalidOperationException(
                "The first live release forbids pyramiding or multiple strategy ownership on one instrument.");
        }
        if (!_account.TryGetQuote(candidate.Decision.Candidate.Instrument, out var quote) ||
            quote.IsStale || !quote.IsTradeable)
        {
            throw new InvalidOperationException("The executable quote is missing, stale, or non-tradeable.");
        }
        decimal reference = candidate.Decision.Candidate.ReferencePrice ?? 0m;
        decimal executable = candidate.Decision.Candidate.Action == Agent.Models.AgentAction.Buy
            ? quote.Ask
            : quote.Bid;
        decimal driftBps = reference > 0m ? Math.Abs(executable - reference) / reference * 10_000m : decimal.MaxValue;
        if (driftBps > _options.MaximumManualApprovalPriceDriftBasisPoints)
        {
            throw new InvalidOperationException(
                $"The price moved {driftBps:F2} bps from the candidate reference; approval requires a fresh setup.");
        }
        PortfolioReservation? reservation = _reservations.Snapshot.Reservations.SingleOrDefault(item =>
            item.ReservationId == candidate.Decision.ReservationId);
        if (reservation is null)
            throw new InvalidOperationException("The candidate's portfolio reservation is no longer active.");
        decimal equity = account.Equity;
        decimal projectedMargin = (account.Account.MarginUsed ?? 0m) +
            _reservations.Snapshot.ReservedMargin;
        if (equity <= 0m || projectedMargin / equity * 100m > _options.MaximumMarginUsagePercent)
            throw new InvalidOperationException("Current account margin no longer permits this approval.");
        if (account.Positions.Count + _reservations.Snapshot.Reservations.Count > _options.MaximumOpenPositions)
            throw new InvalidOperationException("The live maximum position limit no longer permits this approval.");
    }

    private async Task HandleExecutionUncertaintyAsync(
        LiveExecutionResult result,
        CancellationToken cancellationToken)
    {
        if (result.Certainty != ExecutionCertainty.Unknown)
            return;
        _safety.Pause(
            "Broker submission certainty is unknown; entries are paused pending reconciliation.",
            _timeProvider.GetUtcNow());
        await ReconcileWithoutLockAsync(ReconciliationTrigger.UnknownSubmission, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ReconcileWithoutLockAsync(
        ReconciliationTrigger trigger,
        CancellationToken cancellationToken)
    {
        await _account.RefreshAsync(cancellationToken).ConfigureAwait(false);
        BrokerReconciliationReport report = await _reconciler.ReconcileAsync(trigger, cancellationToken)
            .ConfigureAwait(false);
        SetReconciliation(report);
        if (!report.CanOpenNewEntries)
        {
            _safety.Pause(
                "Broker reconciliation requires operator review.",
                _timeProvider.GetUtcNow());
        }
        await PersistAsync("reconciliation", report, cancellationToken).ConfigureAwait(false);
    }

    private void SetReconciliation(BrokerReconciliationReport report)
    {
        lock (_sync)
        {
            _lastReconciliation = report;
        }
    }

    private void RecordDecision(PortfolioDecision decision)
    {
        lock (_sync)
        {
            if (_recentDecisions.Count >= 500)
                _recentDecisions.Dequeue();
            _recentDecisions.Enqueue(decision);
        }
    }

    private void EnsureBrokerWritesEnabled()
    {
        if (!_options.BrokerWritesEnabled)
            throw new InvalidOperationException(
                "Broker writes are disabled. Set LiveExecution:BrokerWritesEnabled=true explicitly on OANDA Practice.");
    }

    private async Task PersistAsync<T>(
        string stream,
        T payload,
        CancellationToken cancellationToken)
    {
        try
        {
            await _persistence.AppendAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _safety.Trip(
                SafetyTripReason.External,
                $"Live persistence failed: {ex.Message}",
                _timeProvider.GetUtcNow());
            throw;
        }
    }

    private Task SaveCheckpointCoreAsync(bool cleanShutdown, CancellationToken cancellationToken) =>
        _persistence.SaveCheckpointAsync(new LiveEngineCheckpoint
        {
            SavedAt = _timeProvider.GetUtcNow(),
            // Read the cursor before the registry. Broker events mutate the registry first and
            // advance the cursor second, so this ordering can only cause harmless replay of an
            // already remembered event, never a cursor that skips an older registry snapshot.
            LastBrokerTransactionId = _brokerEvents.LastTransactionId,
            Registry = _registry.Snapshot,
            Reservations = _reservations.Snapshot,
            ManualApprovals = _manualApprovals.Snapshot,
            Safety = _safety.Snapshot,
            LastReconciliationId = _lastReconciliation?.ReconciliationId,
            ShadowOutcomes = _shadowOutcomes.Checkpoint,
            CleanShutdown = cleanShutdown
        }, cancellationToken).AsTask();
}
