using Agent.Abstractions;
using ExecutionManager;
using TradingJournal;
using Agent.Models;
using RiskManager.Safety;
using TradingCore.MarketData;
using Brokers.Abstractions;
using Brokers.Models;
using ChartAnnotator.Models;
using PortfolioManager.Risk;
using RiskManager.Conditions;
using RiskManager.Calibration;

namespace TradingCore.Pipeline;

public enum TradingPipelineStatus
{
    Observed,
    RejectedByDataQuality,
    RejectedBySafety,
    Processed,
    DelayedByTradingConditions,
    RejectedByTradingConditions,
    RejectedBySetupCalibration,
    RejectedByMetaLabel
}

public sealed record TradingPipelineResult
{
    public required TradingPipelineStatus Status { get; init; }
    public required DataQualityResult DataQuality { get; init; }
    public AgentDecision? Decision { get; init; }
    public OrderSubmission? Submission { get; init; }
    public string? Message { get; init; }
    public TradingConditionDecision? TradingCondition { get; init; }
    public SetupCalibrationDecision? SetupCalibration { get; init; }
    public MetaLabelDecision? MetaLabel { get; init; }
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
    private readonly ITradingConditionFilter? _tradingConditions;
    private readonly ISetupCalibrationPolicy? _setupCalibration;
    private readonly ISetupMetaModel? _metaModel;

    public SafeTradingPipeline(
        ITradingAgent agent,
        IExecutionCoordinator execution,
        IMarketDataQualityGate? dataQuality = null,
        ITradingSafetyController? safety = null,
        ITradeJournal? journal = null,
        ITradingConditionFilter? tradingConditions = null,
        ISetupCalibrationPolicy? setupCalibration = null,
        ISetupMetaModel? metaModel = null)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _execution = execution ?? throw new ArgumentNullException(nameof(execution));
        _dataQuality = dataQuality ?? new MarketDataQualityGate();
        _safety = safety ?? new TradingSafetyController();
        _journal = journal ?? NullTradeJournal.Instance;
        _tradingConditions = tradingConditions;
        _setupCalibration = setupCalibration;
        _metaModel = metaModel;
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
        if (!string.IsNullOrWhiteSpace(context.StrategyId) && string.IsNullOrWhiteSpace(decision.StrategyId))
            decision = decision with { StrategyId = context.StrategyId };

        TradingConditionDecision? condition = null;
        if (_tradingConditions is not null && decision.Action is AgentAction.Buy or AgentAction.Sell)
        {
            AnalysisSnapshot conditionSnapshot = context.Analysis.Timeframes.Values
                .OrderBy(snapshot => snapshot.Interval, Comparer<BarInterval>.Create(
                    (left, right) => BarIntervalParser.CompareDuration(left, right)))
                .First();
            condition = _tradingConditions.Evaluate(new TradingConditionContext
            {
                Timestamp = context.Timestamp,
                Instrument = context.Instrument,
                CurrentSpread = context.ExecutableSpread,
                Atr = conditionSnapshot.Indicators.Atr,
                MarketDataAvailableAt = context.MarketDataAvailableAt ?? conditionSnapshot.AvailableAt,
                Regime = conditionSnapshot.MarketRegime.Regime,
                IsNewEntry = true
            });
            Append(
                TradeJournalEventType.TradingConditionEvaluated,
                context,
                decision,
                $"[{condition.ReasonCode}] {condition.Explanation}");
            if (condition.Action is TradingConditionAction.DelayEntry or TradingConditionAction.RejectEntry)
            {
                Append(
                    TradeJournalEventType.TradingConditionRejected,
                    context,
                    decision,
                    $"[{condition.ReasonCode}] {condition.Explanation}");
                return new TradingPipelineResult
                {
                    Status = condition.Action == TradingConditionAction.DelayEntry
                        ? TradingPipelineStatus.DelayedByTradingConditions
                        : TradingPipelineStatus.RejectedByTradingConditions,
                    DataQuality = quality,
                    Decision = decision,
                    Message = condition.Explanation,
                    TradingCondition = condition
                };
            }

            if (condition.Action == TradingConditionAction.AllowWithReducedRisk)
            {
                decision = decision with
                {
                    TradingConditionRiskMultiplier = condition.RiskMultiplier,
                    TradingConditionReasonCode = condition.ReasonCode,
                    SpreadAtr = condition.SpreadAtr
                };
            }
        }

        SetupCalibrationDecision? calibration = null;
        if (_setupCalibration is not null && decision.Action is AgentAction.Buy or AgentAction.Sell)
        {
            AnalysisSnapshot calibrationSnapshot = ShortestSnapshot(context.Analysis);
            calibration = _setupCalibration.Evaluate(
                decision.StrategyId ?? decision.StrategyName ?? "unknown",
                InstrumentGroup(decision.Instrument),
                (decision.RegimeLabel ?? calibrationSnapshot.MarketRegime.Regime).ToString(),
                decision.Confidence);
            Append(
                TradeJournalEventType.SetupCalibrationEvaluated,
                context,
                decision,
                $"[{calibration.ReasonCode}] {calibration.Explanation}");
            if (!calibration.Trade)
            {
                Append(
                    TradeJournalEventType.SetupCalibrationRejected,
                    context,
                    decision,
                    $"[{calibration.ReasonCode}] {calibration.Explanation}");
                return new TradingPipelineResult
                {
                    Status = TradingPipelineStatus.RejectedBySetupCalibration,
                    DataQuality = quality,
                    Decision = decision,
                    Message = calibration.Explanation,
                    TradingCondition = condition,
                    SetupCalibration = calibration
                };
            }
            decision = decision with { SetupCalibrationRiskMultiplier = calibration.RiskMultiplier };
        }

        MetaLabelDecision? metaLabel = null;
        if (_metaModel is not null && decision.Action is AgentAction.Buy or AgentAction.Sell)
        {
            metaLabel = _metaModel.Evaluate(MetaLabelFeatureFactory.Create(
                decision,
                context.Analysis,
                condition?.SpreadAtr ?? decision.SpreadAtr,
                currencyStrengthDifferential: CurrencyStrengthDifferential(context.CurrencyStrength, decision.Instrument)));
            metaLabel.Validate();
            Append(
                TradeJournalEventType.MetaLabelEvaluated,
                context,
                decision,
                $"[{metaLabel.ReasonCode}] model={metaLabel.ModelVersion}, probability={metaLabel.Probability:F4}, risk={metaLabel.RiskMultiplier:F3}.");
            if (!metaLabel.Trade)
            {
                Append(
                    TradeJournalEventType.MetaLabelRejected,
                    context,
                    decision,
                    $"[{metaLabel.ReasonCode}] The validated deterministic candidate was rejected by model {metaLabel.ModelVersion}.");
                return new TradingPipelineResult
                {
                    Status = TradingPipelineStatus.RejectedByMetaLabel,
                    DataQuality = quality,
                    Decision = decision,
                    Message = metaLabel.ReasonCode,
                    TradingCondition = condition,
                    SetupCalibration = calibration,
                    MetaLabel = metaLabel
                };
            }
            decision = decision with
            {
                MetaLabelRiskMultiplier = metaLabel.RiskMultiplier,
                MetaLabelProbability = metaLabel.Probability,
                MetaLabelModelVersion = metaLabel.ModelVersion,
                MetaLabelReasonCode = metaLabel.ReasonCode
            };
        }

        TradingSafetySnapshot safety = _safety.Snapshot;
        if (decision.Action is AgentAction.Buy or AgentAction.Sell &&
            safety.EquityProtection.CurrentRiskMultiplier < 1m)
        {
            // Composes multiplicatively with any regime risk multiplier the agent
            // already applied - equity protection is an account/strategy-level
            // safety scalar layered on top, not a replacement for it.
            decision = decision with
            {
                EquityProtectionRiskMultiplier = safety.EquityProtection.CurrentRiskMultiplier,
                EquityProtectionActivatedTierIds = string.Join(",", safety.EquityProtection.ActivatedTierIds)
            };
        }

        Append(
            TradeJournalEventType.SignalEvaluated,
            context,
            decision,
            decision.Reason);

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
                Message = message,
                TradingCondition = condition,
                SetupCalibration = calibration,
                MetaLabel = metaLabel
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
            Message = submission?.RejectionReason ?? decision.Reason,
            TradingCondition = condition,
            SetupCalibration = calibration,
            MetaLabel = metaLabel
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

    private static AnalysisSnapshot ShortestSnapshot(MultiTimeframeAnalysis analysis) =>
        analysis.Timeframes.Values
            .OrderBy(snapshot => BarIntervalParser.ApproximateSeconds(snapshot.Interval))
            .First();

    private static string InstrumentGroup(InstrumentKey instrument)
    {
        string value = instrument.Value;
        int separator = value.IndexOf(':');
        return separator > 0 ? value[..separator].ToUpperInvariant() : "Unknown";
    }

    /// <summary>
    /// Base-minus-quote currency strength for the traded instrument. The context's
    /// snapshot is already leave-one-out for this instrument (see
    /// CrossMarketAnalysisCoordinator.GetSnapshot). Null (not zero) when either
    /// currency has no score - soft evidence must never masquerade as "neutral".
    /// </summary>
    private static decimal? CurrencyStrengthDifferential(CurrencyStrengthSnapshot? snapshot, InstrumentKey instrument)
    {
        if (snapshot is null) return null;
        (string baseCurrency, string quoteCurrency) = CurrencyExposureCalculator.ParseCurrencies(instrument);
        if (!snapshot.Scores.TryGetValue(baseCurrency, out decimal baseScore) ||
            !snapshot.Scores.TryGetValue(quoteCurrency, out decimal quoteScore))
            return null;
        return baseScore - quoteScore;
    }
}
