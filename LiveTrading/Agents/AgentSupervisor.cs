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
public sealed class AgentSupervisor(
    IBrokerClient broker,
    LiveDecisionEpochCoordinator epochCoordinator,
    TimeProvider timeProvider,
    ILogger<AgentSupervisor> logger)
{
    private readonly Dictionary<AgentInstanceKey, AgentInstanceState> _instances = [];

    public void Register(AgentInstanceState instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        _instances[instance.Key] = instance;
    }

    public IReadOnlyCollection<AgentInstanceState> Instances => _instances.Values;

    public async Task ApplyAsync(MarketAnalysisUpdate update, decimal? executableSpread, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        long epoch = LiveDecisionEpochCoordinator.EpochFor(update.AvailableAt);

        foreach (AgentInstanceState instance in _instances.Values)
        {
            if (instance.Key.Instrument != update.Instrument)
            {
                continue;
            }

            if (!ShouldTrigger(instance.Runtime.Agent, update))
            {
                continue;
            }

            if (update.MarketSequence <= instance.LastEvaluatedMarketSequence)
            {
                continue;
            }

            try
            {
                await EvaluateAsync(instance, update, executableSpread, cancellationToken).ConfigureAwait(false);
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
            }
        }

        // Every chronological market update completes that instrument's barrier slot, even when
        // none of its Agents trigger on this candle. Otherwise a 5m Agent can make every intervening
        // M1 epoch look unavailable and valid candidates from other markets are rejected after the
        // timeout. Notification happens only after all eligible Agents on this market have finished.
        epochCoordinator.NotifyEvaluated(epoch, update.Instrument);
    }

    /// <summary>Exactly replicates <c>StrategySimulationSession.ProcessFrameAsync</c>'s
    /// <c>HasRequiredSnapshots</c> gate, driven by a <see cref="MarketAnalysisUpdate"/> instead of
    /// a simulator <c>MarketFrame</c>.</summary>
    private static bool ShouldTrigger(ITradingAgent agent, MarketAnalysisUpdate update) =>
        update.ClosedIntervals.Contains(agent.TriggerInterval) &&
        agent.RequiredIntervals.All(interval =>
            update.Analysis.TryGet(interval, out ChartAnnotator.Models.AnalysisSnapshot snapshot) &&
            snapshot.AvailableAt <= update.AvailableAt);

    private async Task EvaluateAsync(
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
                    instance.Key.StrategyId, context, result, epoch);
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

                epochCoordinator.SubmitCandidate(candidate);
                break;
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
    }
}
