using Agent.Abstractions;
using ExecutionManager;
using TradingJournal;
using Agent.Models;
using RiskManager.Safety;
using TradingCore.MarketData;
using Brokers.Abstractions;
using Brokers.Models;

namespace TradingCore.Pipeline;

public enum TradingPipelineStatus
{
    Observed,
    RejectedByDataQuality,
    RejectedBySafety,
    Processed
}

public sealed record TradingPipelineResult
{
    public required TradingPipelineStatus Status { get; init; }
    public required DataQualityResult DataQuality { get; init; }
    public AgentDecision? Decision { get; init; }
    public OrderSubmission? Submission { get; init; }
    public string? Message { get; init; }
}

/// <summary>
/// Places safety and data-integrity controls in front of strategy evaluation and execution.
/// Existing positions remain visible to the caller even when new entries are blocked.
/// </summary>
public sealed class SafeTradingPipeline
{
    private readonly ITradingAgent _agent;
    private readonly IExecutionCoordinator _execution;
    private readonly IMarketDataQualityGate _dataQuality;
    private readonly ITradingSafetyController _safety;
    private readonly ITradeJournal _journal;

    public SafeTradingPipeline(
        ITradingAgent agent,
        IExecutionCoordinator execution,
        IMarketDataQualityGate? dataQuality = null,
        ITradingSafetyController? safety = null,
        ITradeJournal? journal = null)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _execution = execution ?? throw new ArgumentNullException(nameof(execution));
        _dataQuality = dataQuality ?? new MarketDataQualityGate();
        _safety = safety ?? new TradingSafetyController();
        _journal = journal ?? NullTradeJournal.Instance;
    }

    public async Task<TradingPipelineResult> ProcessAsync(
        AgentMarketContext context,
        ITradingBrokerClient broker,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(broker);

        DataQualityResult quality = _dataQuality.Evaluate(context.Analysis);
        if (!quality.IsValid)
        {
            string message = string.Join(
                " ",
                quality.Issues.Select(issue => $"[{issue.Code}] {issue.Message}"));
            Append(
                TradeJournalEventType.DataQualityRejected,
                context,
                null,
                message);

            if (quality.ShouldTrip && _safety.TripOnCriticalDataQualityIssue)
            {
                TradingSafetySnapshot snapshot = _safety.Trip(
                    SafetyTripReason.DataQuality,
                    message,
                    context.Timestamp);
                Append(
                    TradeJournalEventType.SafetyStateChanged,
                    context,
                    null,
                    $"Trading safety changed to {snapshot.State}: {snapshot.Reason}.");
            }

            return new TradingPipelineResult
            {
                Status = TradingPipelineStatus.RejectedByDataQuality,
                DataQuality = quality,
                Message = message
            };
        }

        AgentDecision decision = await _agent
            .EvaluateAsync(context, cancellationToken)
            .ConfigureAwait(false);
        Append(
            TradeJournalEventType.SignalEvaluated,
            context,
            decision,
            decision.Reason);

        TradingSafetySnapshot safety = _safety.Snapshot;
        if (!safety.CanOpenNewTrades && decision.Action is AgentAction.Buy or AgentAction.Sell)
        {
            string message =
                $"New entries are blocked because trading safety is {safety.State}: " +
                $"{safety.Message ?? safety.Reason.ToString()}.";
            Append(
                TradeJournalEventType.SignalRejected,
                context,
                decision,
                message);
            return new TradingPipelineResult
            {
                Status = TradingPipelineStatus.RejectedBySafety,
                DataQuality = quality,
                Decision = decision,
                Message = message
            };
        }

        OrderSubmission? submission = await _execution
            .ProcessAsync(decision, broker, cancellationToken)
            .ConfigureAwait(false);
        return new TradingPipelineResult
        {
            Status = decision.Action == AgentAction.Observe
                ? TradingPipelineStatus.Observed
                : TradingPipelineStatus.Processed,
            DataQuality = quality,
            Decision = decision,
            Submission = submission,
            Message = submission?.RejectionReason ?? decision.Reason
        };
    }

    private void Append(
        TradeJournalEventType type,
        AgentMarketContext context,
        AgentDecision? decision,
        string message)
    {
        _journal.Append(new TradeJournalEntry
        {
            Sequence = 0,
            Timestamp = context.Timestamp,
            Type = type,
            Instrument = context.Instrument,
            Action = decision?.Action,
            DecisionId = decision?.DecisionId,
            Confidence = decision?.Confidence,
            Message = message
        });
    }
}
