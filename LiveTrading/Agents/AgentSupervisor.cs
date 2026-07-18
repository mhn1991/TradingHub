using Agent.Abstractions;
using System.Diagnostics;
using Agent.Models;
using Brokers.Abstractions;
using Brokers.Models;
using LiveTrading.Actors;
using LiveTrading.Configuration;
using LiveTrading.Shadow;
using Microsoft.Extensions.Logging;
using TradingCore.Pipeline;

namespace LiveTrading.Agents;

/// <summary>Compact, durable outcome for one agent's participation in a market decision epoch.</summary>
public sealed record AgentEvaluationAudit
{
    public required AgentInstanceKey Agent { get; init; }
    public required long DecisionEpoch { get; init; }
    public required long SnapshotVersion { get; init; }
    public required string AnalysisProfileHash { get; init; }
    public required StrategyActivationMode ActivationMode { get; init; }
    public required long MarketSequence { get; init; }
    public required DateTimeOffset AvailableAt { get; init; }
    public required AgentEvaluationOutcome Outcome { get; init; }
    public string? PipelineStatus { get; init; }
    public string? DecisionId { get; init; }
    public string? CandidateId { get; init; }
    public AgentAction? Action { get; init; }
    public required string StateBefore { get; init; }
    public required string StateAfter { get; init; }
    public decimal? Confidence { get; init; }
    public string? ReasonCode { get; init; }
    public IReadOnlyDictionary<string, string> Diagnostics { get; init; } =
        new Dictionary<string, string>();
    public string? Error { get; init; }
}

/// <summary>
/// Evaluates every registered agent instance against live <see cref="MarketAnalysisUpdate"/>s
/// through the exact same <see cref="SafeTradingPipeline"/> the simulator drives (built via <see
/// cref="LiveAgentFactory"/>), replicating <c>StrategySimulationSession.ProcessFrameAsync</c>'s
/// trigger-interval-closed-and-every-required-snapshot-available gate. Fully observe-only: every
/// pipeline is built with a <see cref="ShadowExecutionCoordinator"/>, so nothing this class does
/// can ever place, amend, or cancel an order.
/// </summary>
/// <remarks>
/// Multi-agent architecture Phase 6: <see cref="ApplyAsync"/> dispatches every eligible agent
/// instance on one market update concurrently (bounded by <paramref name="maxConcurrentEvaluations"/>),
/// then submits any resulting candidates to <paramref name="epochCoordinator"/> in the same fixed
/// order the eligible-instance list was built in - never completion order - mirroring the
/// simulator's own positional-array-indexing determinism pattern
/// (<c>StreamingComparativeEngine.ProcessParallelWorkersBatchAsync</c>). Each registered instance
/// gets its own dispatch-independent serialization gate (<see cref="_instanceGates"/>) so a timed
/// out evaluation that keeps running in the background can never overlap with a later
/// <see cref="ApplyAsync"/> call's evaluation of that same instance, even though timing out lets
/// <em>this</em> call move on immediately. Production dispatch is routed through each instance's
/// <see cref="IAgentRuntime"/> and exact immutable <see cref="MarketAnalysisSnapshot"/> profile;
/// the inline path remains only for compatibility with focused legacy tests.
/// </remarks>
public sealed class AgentSupervisor(
    IBrokerClient broker,
    LiveDecisionEpochCoordinator epochCoordinator,
    TimeProvider timeProvider,
    ILogger<AgentSupervisor> logger,
    int maxConcurrentEvaluations = 4,
    TimeSpan? evaluationTimeout = null)
{
    private readonly Dictionary<AgentInstanceKey, AgentInstanceState> _instances = [];
    /// <summary>One serialization gate per registered instance - not for cross-agent concurrency
    /// (that's <see cref="_dispatchGate"/>'s job), but so a timed-out evaluation that keeps
    /// running in the background can never overlap with a *later* <see cref="ApplyAsync"/> call's
    /// evaluation of that same instance. Timing out releases <see cref="_dispatchGate"/>
    /// immediately (other agents must not wait on one slow one) but this gate stays held until
    /// the straggling evaluation truly finishes - see <see cref="ObserveLateCompletionAsync"/>.</summary>
    private readonly Dictionary<AgentInstanceKey, SemaphoreSlim> _instanceGates = [];
    private readonly SemaphoreSlim _dispatchGate = new(
        Math.Max(1, maxConcurrentEvaluations), Math.Max(1, maxConcurrentEvaluations));
    private readonly TimeSpan _evaluationTimeout = evaluationTimeout ?? TimeSpan.FromSeconds(4);

    public void Register(AgentInstanceState instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (!_instances.TryAdd(instance.Key, instance))
            throw new InvalidOperationException($"Agent instance '{instance.Key}' is already registered.");
        _instanceGates.Add(instance.Key, new SemaphoreSlim(1, 1));
    }

    public IReadOnlyCollection<AgentInstanceState> Instances => _instances.Values;

    public async Task<IReadOnlyList<AgentEvaluationAudit>> ApplyAsync(
        MarketAnalysisUpdate update,
        decimal? executableSpread,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        long epoch = LiveDecisionEpochCoordinator.EpochFor(update.AvailableAt);

        // Establishes "current" before any dispatch starts, so a still-in-flight evaluation for
        // an older sequence (typically one that timed out) is detectable by
        // LiveDecisionEpochCoordinator.SubmitCandidate the moment it eventually completes, even
        // though this update's own candidates haven't been submitted yet.
        epochCoordinator.NotifyMarketSequence(update.Instrument, update.MarketSequence);

        // Fixed-order eligible list, built once before any concurrent dispatch - this exact
        // order is what determines candidate submission order below, independent of which
        // evaluation happens to finish first.
        List<AgentInstanceState> eligible = _instances.Values
            .Where(instance => instance.Key.Instrument == update.Instrument)
            .Where(instance => ShouldTrigger(instance, update))
            .Where(instance => update.MarketSequence > instance.LastEvaluatedMarketSequence)
            .OrderBy(instance => instance.Key.DeploymentId, StringComparer.Ordinal)
            .ThenBy(instance => instance.Key.Instrument.Value, StringComparer.Ordinal)
            .ThenBy(instance => instance.Key.StrategyId, StringComparer.Ordinal)
            .ThenBy(instance => instance.Key.PolicyBundleId)
            .ThenBy(instance => instance.Key.Revision)
            .ToList();

        var candidates = new LiveTradeCandidate?[eligible.Count];
        epochCoordinator.ExpectAgents(epoch, update.Instrument, eligible.Select(item => item.Key).ToArray());
        AgentEvaluationAudit[] audits = await Task.WhenAll(eligible.Select((instance, index) =>
            DispatchOneAsync(instance, index, update, executableSpread, candidates, cancellationToken)))
            .ConfigureAwait(false);

        // Fixed order (matches `eligible`), not completion order.
        foreach (LiveTradeCandidate? candidate in candidates)
        {
            if (candidate is not null)
                epochCoordinator.SubmitCandidate(candidate);
        }
        for (int index = 0; index < eligible.Count; index++)
            epochCoordinator.NotifyAgentEvaluated(epoch, eligible[index].Key, audits[index].Outcome);

        // Every chronological market update completes that instrument's barrier slot, even when
        // none of its Agents trigger on this candle. Otherwise a 5m Agent can make every intervening
        // M1 epoch look unavailable and valid candidates from other markets are rejected after the
        // timeout. Notification happens only after all eligible Agents on this market have finished.
        epochCoordinator.NotifyEvaluated(epoch, update.Instrument);

        return audits;
    }

    private async Task<AgentEvaluationAudit> DispatchOneAsync(
        AgentInstanceState instance,
        int index,
        MarketAnalysisUpdate update,
        decimal? executableSpread,
        LiveTradeCandidate?[] candidates,
        CancellationToken cancellationToken)
    {
        SemaphoreSlim instanceGate = _instanceGates[instance.Key];
        int mailboxDepth = Interlocked.Increment(ref instance.MailboxDepth);
        int observedPeak;
        do
        {
            observedPeak = Volatile.Read(ref instance.PeakMailboxDepth);
        } while (mailboxDepth > observedPeak &&
                 Interlocked.CompareExchange(ref instance.PeakMailboxDepth, mailboxDepth, observedPeak) != observedPeak);
        long started = Stopwatch.GetTimestamp();
        bool instanceGateAcquired = false;
        bool dispatchGateAcquired = false;
        bool instanceGateHandedOff = false;
        string stateBefore = instance.LastStatus ?? "NotEvaluated";
        try
        {
            await instanceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            instanceGateAcquired = true;
            stateBefore = instance.LastStatus ?? "NotEvaluated";
            await _dispatchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            dispatchGateAcquired = true;
            instance.LastEvaluatedEpoch = LiveDecisionEpochCoordinator.EpochFor(update.AvailableAt);
            if (instance.AnalysisProfile is { } profile && update.TryGetSnapshot(profile, out MarketAnalysisSnapshot snapshot))
                instance.LastSnapshotVersion = snapshot.SnapshotVersion;
            instance.LastEvaluationError = null;
            // Task.Run, not a direct call: SafeTradingPipeline.ProcessAsync's downstream Agent
            // logic in this codebase is typically synchronous/CPU-bound (pattern/indicator
            // evaluation, no real I/O), so calling EvaluateAsync directly can block this method's
            // own thread for its entire duration before ever reaching the Task.Delay(...) line
            // below - which would mean the "timeout" never gets a chance to start racing at all.
            // Dispatching onto the thread pool first guarantees the race is always genuinely set
            // up, regardless of whether the agent's own logic yields or blocks.
            using var evaluationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task<LiveTradeCandidate?> evaluation = Task.Run(
                () => EvaluateAsync(instance, update, executableSpread, evaluationCancellation.Token),
                evaluationCancellation.Token);
            Task timeout = Task.Delay(_evaluationTimeout, timeProvider, cancellationToken);
            Task first = await Task.WhenAny(evaluation, timeout).ConfigureAwait(false);
            if (first == timeout)
            {
                await evaluationCancellation.CancelAsync().ConfigureAwait(false);
                instance.TimeoutCount++;
                instance.LastStatus = "TimedOut";
                logger.LogWarning(
                    "Agent {StrategyId}/{Instrument} evaluation exceeded {Timeout} - marked unhealthy. " +
                    "The evaluation keeps running; a late result is discarded if a newer market update " +
                    "has already begun.",
                    instance.Key.StrategyId,
                    instance.Key.Instrument,
                    _evaluationTimeout);
                // Hand instanceGate's release off to the observer below - it must stay held until
                // the straggling evaluation truly finishes, not just until this dispatch call
                // gives up on it. _dispatchGate releases immediately in `finally` regardless, so
                // other agents are never blocked by one slow one.
                instanceGateHandedOff = true;
                _ = ObserveLateCompletionAsync(evaluation, instance, instanceGate);
                return BuildAudit(
                    instance, update, stateBefore, AgentEvaluationOutcome.TimedOut, candidates[index]);
            }

            candidates[index] = await evaluation.ConfigureAwait(false);
            return BuildAudit(
                instance, update, stateBefore, AgentEvaluationOutcome.Completed, candidates[index]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            instance.LastEvaluationError = ex;
            instance.LastEvaluationErrorAt = timeProvider.GetUtcNow();
            instance.LastStatus = "Failed";
            logger.LogError(
                ex,
                "Agent {StrategyId}/{Instrument} evaluation failed.",
                instance.Key.StrategyId,
                instance.Key.Instrument);
            return BuildAudit(
                instance, update, stateBefore, AgentEvaluationOutcome.Failed, candidates[index]);
        }
        finally
        {
            if (instanceGateAcquired)
            {
                instance.LastEvaluatedMarketSequence = update.MarketSequence;
                instance.LastEvaluatedAt = timeProvider.GetUtcNow();
            }
            instance.RecordEvaluationDuration(Stopwatch.GetElapsedTime(started));
            Interlocked.Decrement(ref instance.MailboxDepth);
            if (instanceGateAcquired && !instanceGateHandedOff)
                instanceGate.Release();
            if (dispatchGateAcquired)
                _dispatchGate.Release();
        }
    }

    private static AgentEvaluationAudit BuildAudit(
        AgentInstanceState instance,
        MarketAnalysisUpdate update,
        string stateBefore,
        AgentEvaluationOutcome outcome,
        LiveTradeCandidate? candidate)
    {
        var diagnostics = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["outcome"] = outcome.ToString(),
            ["activationMode"] = instance.Assignment.Mode.ToString()
        };
        if (!string.IsNullOrWhiteSpace(instance.LastRejection))
            diagnostics["rejection"] = instance.LastRejection;
        if (instance.LastEvaluationError is { } error)
            diagnostics["errorType"] = error.GetType().FullName ?? error.GetType().Name;

        return new AgentEvaluationAudit
        {
            Agent = instance.Key,
            DecisionEpoch = LiveDecisionEpochCoordinator.EpochFor(update.AvailableAt),
            SnapshotVersion = instance.LastSnapshotVersion,
            AnalysisProfileHash = instance.AnalysisProfile?.ProfileHash ?? string.Empty,
            ActivationMode = instance.Assignment.Mode,
            MarketSequence = update.MarketSequence,
            AvailableAt = update.AvailableAt,
            Outcome = outcome,
            PipelineStatus = instance.LastStatus,
            DecisionId = candidate?.DecisionId ?? instance.LastDecisionId,
            CandidateId = candidate?.CandidateId,
            Action = candidate?.Action ?? instance.LastAction,
            StateBefore = stateBefore,
            StateAfter = instance.LastStatus ?? outcome.ToString(),
            Confidence = candidate?.RawConfidence ?? instance.LastConfidence,
            ReasonCode = instance.LastReasonCode,
            Diagnostics = diagnostics,
            Error = instance.LastEvaluationError?.Message
        };
    }

    /// <summary>Observes a timed-out evaluation's eventual completion, releasing its instance
    /// gate only once the straggler truly finishes - see <see cref="_instanceGates"/>'s remarks.
    /// Its candidate, if any, was already dropped from the ApplyAsync call that timed out on it
    /// (the array slot was never written), so a late completion can only ever affect diagnostics/
    /// error bookkeeping, never candidate submission.</summary>
    private async Task ObserveLateCompletionAsync(
        Task<LiveTradeCandidate?> evaluation, AgentInstanceState instance, SemaphoreSlim instanceGate)
    {
        try
        {
            await evaluation.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A timed-out evaluation is cancelled cooperatively. Synchronous Agent code may
            // finish its current call first, but the cancellation check before state mutation
            // prevents its late result from becoming the runtime's current decision.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                ex,
                "Agent {StrategyId}/{Instrument} evaluation failed after already timing out.",
                instance.Key.StrategyId,
                instance.Key.Instrument);
        }
        finally
        {
            instanceGate.Release();
        }
    }

    /// <summary>Exactly replicates <c>StrategySimulationSession.ProcessFrameAsync</c>'s
    /// <c>HasRequiredSnapshots</c> gate, driven by a <see cref="MarketAnalysisUpdate"/> instead of
    /// a simulator <c>MarketFrame</c>.</summary>
    private static bool ShouldTrigger(AgentInstanceState instance, MarketAnalysisUpdate update)
    {
        ITradingAgent agent = instance.Runtime.Agent;
        MultiTimeframeAnalysis? analysis = AnalysisFor(instance, update);
        return analysis is not null &&
               update.ClosedIntervals.Contains(agent.TriggerInterval) &&
               agent.RequiredIntervals.All(interval =>
                   analysis.TryGet(interval, out ChartAnnotator.Models.AnalysisSnapshot snapshot) &&
                   snapshot.AvailableAt <= update.AvailableAt);
    }

    private async Task<LiveTradeCandidate?> EvaluateAsync(
        AgentInstanceState instance, MarketAnalysisUpdate update, decimal? executableSpread, CancellationToken cancellationToken)
    {
        AgentMarketContext context;
        TradingPipelineResult result;
        if (instance.IsolatedRuntime is { } isolatedRuntime && instance.AnalysisProfile is { } profile)
        {
            if (!update.TryGetSnapshot(profile, out MarketAnalysisSnapshot snapshot))
            {
                throw new InvalidOperationException(
                    $"Analysis profile {profile.ProfileHash} was not published for {update.Instrument}.");
            }
            AgentRuntimeResult runtimeResult = isolatedRuntime is LiveAgentRuntime liveRuntime
                ? await liveRuntime.EvaluateAsync(snapshot, executableSpread, cancellationToken).ConfigureAwait(false)
                : await isolatedRuntime.EvaluateAsync(snapshot, cancellationToken).ConfigureAwait(false);
            if (runtimeResult.Failure is { } failure)
                throw new InvalidOperationException("The isolated agent runtime failed.", failure);
            if (runtimeResult.Native is not LiveAgentEvaluation evaluation)
                throw new InvalidOperationException("The live agent runtime returned an unexpected native result.");
            context = evaluation.Context;
            result = evaluation.PipelineResult;
        }
        else
        {
            AccountSnapshot account = (await broker.Accounts.GetAccountsAsync(cancellationToken).ConfigureAwait(false)).Single();
            IReadOnlyList<BrokerPosition> positions =
                await broker.Positions.GetOpenPositionsAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<BrokerOrder> openOrders =
                await broker.Orders.GetOpenOrdersAsync(update.Instrument, cancellationToken).ConfigureAwait(false);

            context = new AgentMarketContext
            {
                Instrument = update.Instrument,
                Timestamp = update.AvailableAt,
                Analysis = update.Analysis,
                Account = account,
                Positions = positions,
                OpenOrders = openOrders,
                ExecutableSpread = executableSpread,
                MarketDataAvailableAt = update.AvailableAt,
                StrategyId = instance.Key.StrategyId,
                CurrencyStrength = null
            };

            var shadowBroker = new ShadowBrokerClient(broker);
            result = await instance.Runtime.Pipeline
                .ProcessAsync(context, shadowBroker, cancellationToken)
                .ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        instance.LastStatus = result.Status.ToString();
        instance.LastDecisionId = result.Decision?.DecisionId;
        instance.LastAction = result.Decision?.Action;
        instance.LastConfidence = result.Decision?.Confidence;
        instance.LastReasonCode = result.Decision?.ReasonCode;
        instance.LastRejection = result.Status == TradingPipelineStatus.Processed
            ? null
            : result.Decision?.ReasonCode ?? result.Status.ToString();
        instance.Evaluations++;
        long epoch = LiveDecisionEpochCoordinator.EpochFor(update.AvailableAt);

        switch (result.Status)
        {
            case TradingPipelineStatus.Processed when result.Decision is { Action: AgentAction.Buy or AgentAction.Sell }:
                LiveTradeCandidate candidate = SignalFunnel.BuildCandidate(
                    instance.Key.StrategyId,
                    context,
                    result,
                    epoch,
                    update.MarketSequence,
                    instance.Key,
                    instance.AnalysisProfile);
                instance.LastDecisionId = candidate.DecisionId;
                if (instance.LastCandidate?.DecisionId == candidate.DecisionId)
                {
                    instance.DuplicateDecisions++;
                }

                instance.LastCandidate = candidate;
                if (candidate.Action == AgentAction.Buy)
                {
                    instance.CandidatesBuy++;
                }
                else
                {
                    instance.CandidatesSell++;
                }

                return candidate;
            case TradingPipelineStatus.Observed:
                instance.CandidatesObserved++;
                string reasonCode = result.Decision?.ReasonCode ?? "Unspecified";
                instance.ObserveReasonCounts[reasonCode] =
                    instance.ObserveReasonCounts.GetValueOrDefault(reasonCode) + 1;
                break;
            case TradingPipelineStatus.RejectedByMetaLabel:
                instance.CandidatesRejectedByMetaLabel++;
                break;
            case TradingPipelineStatus.RejectedBySetupCalibration:
                instance.CandidatesRejectedBySetupCalibration++;
                break;
            case TradingPipelineStatus.RejectedByTradingConditions:
            case TradingPipelineStatus.DelayedByTradingConditions:
                instance.CandidatesRejectedByTradingCondition++;
                break;
            case TradingPipelineStatus.RejectedByDataQuality:
                instance.CandidatesRejectedByDataQuality++;
                break;
            case TradingPipelineStatus.RejectedBySafety:
                instance.CandidatesRejectedBySafety++;
                break;
        }

        return null;
    }

    private static MultiTimeframeAnalysis? AnalysisFor(
        AgentInstanceState instance, MarketAnalysisUpdate update)
    {
        if (instance.AnalysisProfile is not { } profile)
            return update.Analysis;
        if (!update.TryGetSnapshot(profile, out MarketAnalysisSnapshot snapshot))
            return null;
        return new MultiTimeframeAnalysis(snapshot.Instrument, snapshot.AvailableAt, snapshot.Timeframes);
    }
}
