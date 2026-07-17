using Agent.Abstractions;
using Agent.Models;
using Brokers.Abstractions;
using Brokers.Models;
using LiveTrading.Actors;
using LiveTrading.Shadow;
using Microsoft.Extensions.Logging;
using TradingCore.Pipeline;

namespace LiveTrading.Agents;

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
/// <em>this</em> call move on immediately - the live host's pump loop only guarantees one call to
/// <see cref="ApplyAsync"/> at a time, not that every dispatched evaluation inside it has finished
/// before the next call starts. This is a lighter-weight, purpose-built substitute for wiring this
/// dispatch path through <c>LiveAgentRuntime</c> (Phase 4's standalone, not-yet-wired per-runtime
/// semaphore wrapper) - full <c>IAgentRuntime</c>/<see cref="TradingCore.Pipeline.MarketAnalysisSnapshot"/>
/// rewiring, which would also require converting every <see cref="MarketAnalysisUpdate"/> into a
/// <see cref="TradingCore.Pipeline.MarketAnalysisSnapshot"/> at the call site, is explicitly out
/// of scope here - a separate, larger change with its own review, not a silent side effect of
/// parallelizing dispatch.
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
        _instances[instance.Key] = instance;
        _instanceGates.TryAdd(instance.Key, new SemaphoreSlim(1, 1));
    }

    public IReadOnlyCollection<AgentInstanceState> Instances => _instances.Values;

    public async Task ApplyAsync(MarketAnalysisUpdate update, decimal? executableSpread, CancellationToken cancellationToken)
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
            .Where(instance => ShouldTrigger(instance.Runtime.Agent, update))
            .Where(instance => update.MarketSequence > instance.LastEvaluatedMarketSequence)
            .ToList();

        var candidates = new LiveTradeCandidate?[eligible.Count];
        await Task.WhenAll(eligible.Select((instance, index) =>
                DispatchOneAsync(instance, index, update, executableSpread, candidates, cancellationToken)))
            .ConfigureAwait(false);

        // Fixed order (matches `eligible`), not completion order.
        foreach (LiveTradeCandidate? candidate in candidates)
        {
            if (candidate is not null)
                epochCoordinator.SubmitCandidate(candidate);
        }

        // Every chronological market update completes that instrument's barrier slot, even when
        // none of its Agents trigger on this candle. Otherwise a 5m Agent can make every intervening
        // M1 epoch look unavailable and valid candidates from other markets are rejected after the
        // timeout. Notification happens only after all eligible Agents on this market have finished.
        epochCoordinator.NotifyEvaluated(epoch, update.Instrument);
    }

    private async Task DispatchOneAsync(
        AgentInstanceState instance,
        int index,
        MarketAnalysisUpdate update,
        decimal? executableSpread,
        LiveTradeCandidate?[] candidates,
        CancellationToken cancellationToken)
    {
        SemaphoreSlim instanceGate = _instanceGates[instance.Key];
        await _dispatchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        await instanceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool instanceGateHandedOff = false;
        try
        {
            // Task.Run, not a direct call: SafeTradingPipeline.ProcessAsync's downstream Agent
            // logic in this codebase is typically synchronous/CPU-bound (pattern/indicator
            // evaluation, no real I/O), so calling EvaluateAsync directly can block this method's
            // own thread for its entire duration before ever reaching the Task.Delay(...) line
            // below - which would mean the "timeout" never gets a chance to start racing at all.
            // Dispatching onto the thread pool first guarantees the race is always genuinely set
            // up, regardless of whether the agent's own logic yields or blocks.
            Task<LiveTradeCandidate?> evaluation = Task.Run(
                () => EvaluateAsync(instance, update, executableSpread, cancellationToken), cancellationToken);
            Task timeout = Task.Delay(_evaluationTimeout, timeProvider, cancellationToken);
            Task first = await Task.WhenAny(evaluation, timeout).ConfigureAwait(false);
            if (first == timeout)
            {
                instance.TimeoutCount++;
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
                return;
            }

            candidates[index] = await evaluation.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            instance.LastEvaluationError = ex;
            instance.LastEvaluationErrorAt = timeProvider.GetUtcNow();
            logger.LogError(
                ex,
                "Agent {StrategyId}/{Instrument} evaluation failed.",
                instance.Key.StrategyId,
                instance.Key.Instrument);
        }
        finally
        {
            instance.LastEvaluatedMarketSequence = update.MarketSequence;
            instance.LastEvaluatedAt = timeProvider.GetUtcNow();
            if (!instanceGateHandedOff)
                instanceGate.Release();
            _dispatchGate.Release();
        }
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
    private static bool ShouldTrigger(ITradingAgent agent, MarketAnalysisUpdate update) =>
        update.ClosedIntervals.Contains(agent.TriggerInterval) &&
        agent.RequiredIntervals.All(interval =>
            update.Analysis.TryGet(interval, out ChartAnnotator.Models.AnalysisSnapshot snapshot) &&
            snapshot.AvailableAt <= update.AvailableAt);

    private async Task<LiveTradeCandidate?> EvaluateAsync(
        AgentInstanceState instance, MarketAnalysisUpdate update, decimal? executableSpread, CancellationToken cancellationToken)
    {
        AccountSnapshot account = (await broker.Accounts.GetAccountsAsync(cancellationToken).ConfigureAwait(false)).Single();
        IReadOnlyList<BrokerPosition> positions =
            await broker.Positions.GetOpenPositionsAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<BrokerOrder> openOrders =
            await broker.Orders.GetOpenOrdersAsync(update.Instrument, cancellationToken).ConfigureAwait(false);

        var context = new AgentMarketContext
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
        TradingPipelineResult result = await instance.Runtime.Pipeline
            .ProcessAsync(context, shadowBroker, cancellationToken)
            .ConfigureAwait(false);

        instance.LastStatus = result.Status.ToString();
        instance.Evaluations++;
        long epoch = LiveDecisionEpochCoordinator.EpochFor(update.AvailableAt);

        switch (result.Status)
        {
            case TradingPipelineStatus.Processed when result.Decision is { Action: AgentAction.Buy or AgentAction.Sell }:
                LiveTradeCandidate candidate = SignalFunnel.BuildCandidate(
                    instance.Key.StrategyId, context, result, epoch, update.MarketSequence);
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
}
