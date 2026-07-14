using Agent.Abstractions;
using Agent.Models;
using Brokers.Models;
using ChartAnnotator.Models;

namespace Agent.Strategies;

public abstract class ProgressiveStrategyBase : ITradingAgent
{
    protected enum SetupSide { Buy, Sell }
    protected enum SetupStage { WaitingForTrend, WaitingForConfirmation, WaitingForEntry }

    protected sealed record ScopeState(
        string SetupId,
        SetupSide Side,
        SetupStage Stage,
        DateTimeOffset StartedAt,
        DateTimeOffset ExpiresAt,
        DateTimeOffset LastTrendAt,
        DateTimeOffset? LastConfirmationAt);

    private sealed record EvidenceAssessment(
        int Aligned,
        int Required,
        bool StrongOpposition,
        DateTimeOffset? LatestAlignedAt,
        string Role)
    {
        public bool Satisfied => Aligned >= Required;
    }

    private readonly Dictionary<InstrumentKey, ScopeState> _states = [];
    protected readonly ProgressiveStrategyOptions Options;

    protected ProgressiveStrategyBase(ProgressiveStrategyOptions? options)
    {
        Options = options ?? new ProgressiveStrategyOptions();
        Options.Validate();
        RequiredIntervals = Options.AllRequiredIntervals.ToHashSet();
    }

    public abstract string Name { get; }

    /// <summary>Legacy uses protective-stop exit; Improved uses full brackets.</summary>
    public abstract AgentExitManagementMode ExitManagementMode { get; }

    public IReadOnlySet<BarInterval> RequiredIntervals { get; }
    public BarInterval TriggerInterval => Options.EntryInterval;

    public Task<AgentDecision> EvaluateAsync(
        AgentMarketContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        AnalysisSnapshot trend = context.Analysis.Get(Options.TrendInterval);
        AnalysisSnapshot confirmation = context.Analysis.Get(Options.ConfirmationInterval);
        AnalysisSnapshot entry = context.Analysis.Get(Options.EntryInterval);
        AnalysisSnapshot[] secondaryTrend = Options.SecondaryTrendIntervals
            .Select(context.Analysis.Get)
            .ToArray();
        AnalysisSnapshot[] setup = Options.SetupIntervals
            .Select(context.Analysis.Get)
            .ToArray();
        AnalysisSnapshot[] confirmations = Options.ConfirmationIntervals
            .Select(context.Analysis.Get)
            .ToArray();

        BrokerPosition? position = context.Positions
            .FirstOrDefault(item => item.Instrument == context.Instrument);
        if (position is not null)
        {
            _states.Remove(context.Instrument);
            return Task.FromResult(EvaluateOpenPosition(
                context,
                position,
                trend,
                confirmation,
                entry));
        }

        SetupSide? trendSide = DetectSide(trend, Options.MinimumTrendConfidence);
        ScopeState? state = _states.GetValueOrDefault(context.Instrument);

        if (state is null)
        {
            if (trendSide is null)
            {
                return Task.FromResult(Observe(
                    context,
                    $"Waiting for a valid primary trend on {Options.TrendInterval}.") with
                {
                    ReasonCode = "PrimaryTrendNotReady"
                });
            }

            state = StartScope(context.Instrument, trend, trendSide.Value);
        }
        else
        {
            bool hasFreshTrendCandle = trend.AvailableAt > state.LastTrendAt;
            if (hasFreshTrendCandle)
            {
                if (trendSide is null)
                {
                    _states.Remove(context.Instrument);
                    return Task.FromResult(Observe(
                        context,
                        "The new primary-trend candle invalidated the scoped setup.") with
                    {
                        ReasonCode = "PrimaryTrendInvalidated"
                    });
                }

                if (trendSide != state.Side)
                {
                    state = StartScope(context.Instrument, trend, trendSide.Value);
                }
                else
                {
                    state = state with
                    {
                        LastTrendAt = trend.AvailableAt,
                        ExpiresAt = state.Stage == SetupStage.WaitingForConfirmation
                            ? Options.TrendInterval.AddTo(trend.AvailableAt)
                            : state.ExpiresAt
                    };
                    _states[context.Instrument] = state;
                }
            }
            else if (state.Stage == SetupStage.WaitingForConfirmation &&
                     context.Timestamp >= state.ExpiresAt)
            {
                _states.Remove(context.Instrument);
                return Task.FromResult(Observe(
                    context,
                    "The primary-trend setup expired before confirmation.") with
                {
                    ReasonCode = "PrimaryTrendSetupExpired"
                });
            }
        }

        if (Opposes(state.Side, trend) || HasOpposingBreak(state.Side, trend))
        {
            _states.Remove(context.Instrument);
            return Task.FromResult(Observe(
                context,
                "Primary higher-timeframe structure invalidated the setup.") with
            {
                ReasonCode = "PrimaryTrendOpposition"
            });
        }

        EvidenceAssessment secondaryAssessment = AssessEvidence(
            state.Side,
            secondaryTrend,
            Options.MinimumSecondaryTrendConfidence,
            Options.MinimumSecondaryTrendAlignments,
            "secondary trend",
            state.StartedAt,
            opposingDirectionIsStrong: false);
        AgentDecision? secondaryFailure = HandleEvidenceFailure(
            context,
            state,
            secondaryAssessment,
            invalidateOnOpposition: true);
        if (secondaryFailure is not null)
            return Task.FromResult(secondaryFailure);

        EvidenceAssessment setupAssessment = AssessEvidence(
            state.Side,
            setup,
            Options.MinimumSetupConfidence,
            Options.MinimumSetupAlignments,
            "setup",
            state.StartedAt,
            opposingDirectionIsStrong: true);
        AgentDecision? setupFailure = HandleEvidenceFailure(
            context,
            state,
            setupAssessment,
            invalidateOnOpposition: true);
        if (setupFailure is not null)
            return Task.FromResult(setupFailure);

        EvidenceAssessment confirmationAssessment = AssessEvidence(
            state.Side,
            confirmations,
            Options.MinimumConfirmationConfidence,
            Options.MinimumConfirmationAlignments,
            "confirmation",
            state.StartedAt,
            opposingDirectionIsStrong: true);

        if (state.Stage == SetupStage.WaitingForConfirmation)
        {
            AgentDecision? confirmationFailure = HandleEvidenceFailure(
                context,
                state,
                confirmationAssessment,
                invalidateOnOpposition: true);
            if (confirmationFailure is not null)
                return Task.FromResult(confirmationFailure);

            DateTimeOffset confirmationAt = confirmationAssessment.LatestAlignedAt ??
                confirmation.AvailableAt;
            state = state with
            {
                Stage = SetupStage.WaitingForEntry,
                LastConfirmationAt = confirmationAt,
                ExpiresAt = AddBars(confirmationAt, Options.EntryInterval, Options.MaximumEntryCandles)
            };
            _states[context.Instrument] = state;
        }
        else
        {
            if (confirmationAssessment.StrongOpposition && Options.StrongOppositionVeto)
            {
                _states.Remove(context.Instrument);
                return Task.FromResult(Observe(
                    context,
                    "A confirmation timeframe produced strong opposing structure.") with
                {
                    ReasonCode = "ConfirmationOppositionVeto"
                });
            }

            if (!confirmationAssessment.Satisfied)
            {
                _states.Remove(context.Instrument);
                return Task.FromResult(Observe(
                    context,
                    $"Confirmation consensus fell to {confirmationAssessment.Aligned}/" +
                    $"{confirmationAssessment.Required}; the setup was invalidated.") with
                {
                    ReasonCode = "ConfirmationConsensusLost"
                });
            }

            DateTimeOffset latestConfirmation = confirmationAssessment.LatestAlignedAt ??
                state.LastConfirmationAt ?? confirmation.AvailableAt;
            if (state.LastConfirmationAt is null || latestConfirmation > state.LastConfirmationAt)
            {
                state = state with
                {
                    LastConfirmationAt = latestConfirmation,
                    ExpiresAt = AddBars(latestConfirmation, Options.EntryInterval, Options.MaximumEntryCandles)
                };
                _states[context.Instrument] = state;
            }
            else if (context.Timestamp >= state.ExpiresAt)
            {
                _states.Remove(context.Instrument);
                return Task.FromResult(Observe(
                    context,
                    "The lower-timeframe entry window expired.") with
                {
                    ReasonCode = "EntryWindowExpired"
                });
            }
        }

        SetupSide? entrySide = DetectSide(entry, Options.MinimumEntryConfidence);
        bool entryBelongsToConfirmation = state.LastConfirmationAt is DateTimeOffset confirmedAt &&
            entry.AvailableAt >= confirmedAt;
        if (state.Stage != SetupStage.WaitingForEntry ||
            !entryBelongsToConfirmation ||
            entrySide != state.Side)
        {
            return Task.FromResult(Observe(
                context,
                $"Scoped {state.Side} setup is waiting for the {Options.EntryInterval} entry trigger.") with
            {
                ReasonCode = "EntryTriggerNotReady"
            });
        }

        PriceActionDirection expectedPriceAction = state.Side == SetupSide.Buy
            ? PriceActionDirection.Bullish
            : PriceActionDirection.Bearish;
        PriceActionDirection opposingPriceAction = state.Side == SetupSide.Buy
            ? PriceActionDirection.Bearish
            : PriceActionDirection.Bullish;
        decimal opposingScore = opposingPriceAction == PriceActionDirection.Bullish
            ? entry.PriceAction.BullishScore
            : entry.PriceAction.BearishScore;
        if (Options.PriceActionConfirmation != PriceActionConfirmationMode.Disabled &&
            Options.RejectStrongOpposingPriceAction &&
            entry.PriceAction.Bias == opposingPriceAction &&
            opposingScore >= Options.MinimumPriceActionConfidence)
        {
            return Task.FromResult(Observe(
                context,
                $"The {Options.EntryInterval} price-action evidence opposes the scoped {state.Side} setup " +
                $"with score {opposingScore:F1}.") with
            {
                ReasonCode = "OpposingPriceAction",
                PriceActionConfidence = opposingScore
            });
        }

        if (Options.PriceActionConfirmation == PriceActionConfirmationMode.Required &&
            !entry.PriceAction.HasConfirmedTrigger(
                expectedPriceAction,
                Options.MinimumPriceActionConfidence))
        {
            string diagnostic = entry.PriceAction.Diagnostics
                .Where(item => !item.Accepted)
                .Select(item => item.ReasonCode)
                .FirstOrDefault() ?? "NoConfirmedPriceActionTrigger";
            return Task.FromResult(Observe(
                context,
                $"Scoped {state.Side} setup is waiting for a confirmed {Options.EntryInterval} " +
                $"price-action trigger ({diagnostic}).") with
            {
                ReasonCode = diagnostic
            });
        }

        AgentDecision decision = CreateEntryDecision(
            context,
            state,
            trend,
            confirmation,
            entry);
        if (decision.Action is AgentAction.Buy or AgentAction.Sell)
        {
            PriceActionEvent? trigger = entry.PriceAction.Events
                .Where(item => item.Direction == expectedPriceAction)
                .OrderByDescending(item => item.Confidence)
                .FirstOrDefault();
            if (trigger is not null)
            {
                decision = decision with
                {
                    PriceActionTrigger = trigger.Type,
                    PriceActionConfidence = trigger.Confidence,
                    ReasonCode = trigger.ReasonCode
                };
            }

            decision = decision with
            {
                Reason = decision.Reason +
                    $" MTF evidence: secondary {secondaryAssessment.Aligned}/" +
                    $"{secondaryAssessment.Required}, setup {setupAssessment.Aligned}/" +
                    $"{setupAssessment.Required}, confirmation {confirmationAssessment.Aligned}/" +
                    $"{confirmationAssessment.Required}."
            };
            _states.Remove(context.Instrument);
        }

        return Task.FromResult(decision);
    }

    private AgentDecision? HandleEvidenceFailure(
        AgentMarketContext context,
        ScopeState state,
        EvidenceAssessment assessment,
        bool invalidateOnOpposition)
    {
        if (assessment.StrongOpposition && Options.StrongOppositionVeto)
        {
            if (invalidateOnOpposition)
                _states.Remove(context.Instrument);
            return Observe(
                context,
                $"The {assessment.Role} layer produced a strong opposing structural signal.") with
            {
                ReasonCode = $"{ToReasonPrefix(assessment.Role)}OppositionVeto"
            };
        }

        if (!assessment.Satisfied)
        {
            return Observe(
                context,
                $"Waiting for {assessment.Role} consensus: {assessment.Aligned}/" +
                $"{assessment.Required} aligned with {state.Side}.") with
            {
                ReasonCode = $"{ToReasonPrefix(assessment.Role)}ConsensusNotReady"
            };
        }

        return null;
    }

    private EvidenceAssessment AssessEvidence(
        SetupSide side,
        IReadOnlyList<AnalysisSnapshot> snapshots,
        decimal minimumConfidence,
        int required,
        string role,
        DateTimeOffset notBefore,
        bool opposingDirectionIsStrong)
    {
        if (snapshots.Count == 0)
            return new EvidenceAssessment(0, required, false, null, role);

        int aligned = 0;
        bool opposition = false;
        DateTimeOffset? latestAligned = null;
        foreach (AnalysisSnapshot snapshot in snapshots)
        {
            if (snapshot.AvailableAt < notBefore)
                continue;

            SetupSide? detected = DetectSide(snapshot, minimumConfidence);
            if (detected == side)
            {
                aligned++;
                latestAligned = latestAligned is null || snapshot.AvailableAt > latestAligned
                    ? snapshot.AvailableAt
                    : latestAligned;
            }

            if (HasOpposingBreak(side, snapshot) ||
                (opposingDirectionIsStrong && detected is not null && detected != side &&
                 snapshot.Confidence.Total >= minimumConfidence))
            {
                opposition = true;
            }
        }

        return new EvidenceAssessment(aligned, required, opposition, latestAligned, role);
    }

    private static DateTimeOffset AddBars(
        DateTimeOffset start,
        BarInterval interval,
        int count)
    {
        DateTimeOffset result = start;
        for (int index = 0; index < count; index++)
            result = interval.AddTo(result);
        return result;
    }

    private static string ToReasonPrefix(string role) => role switch
    {
        "secondary trend" => "SecondaryTrend",
        "setup" => "Setup",
        "confirmation" => "Confirmation",
        _ => "Timeframe"
    };

    private ScopeState StartScope(
        InstrumentKey instrument,
        AnalysisSnapshot trend,
        SetupSide side)
    {
        var state = new ScopeState(
            SetupId: $"{Name}:{instrument.Value}:{trend.AvailableAt:O}:{side}",
            Side: side,
            Stage: SetupStage.WaitingForConfirmation,
            StartedAt: trend.AvailableAt,
            ExpiresAt: Options.TrendInterval.AddTo(trend.AvailableAt),
            LastTrendAt: trend.AvailableAt,
            LastConfirmationAt: null);
        _states[instrument] = state;
        return state;
    }

    protected abstract AgentDecision CreateEntryDecision(
        AgentMarketContext context,
        ScopeState state,
        AnalysisSnapshot trend,
        AnalysisSnapshot confirmation,
        AnalysisSnapshot entry);

    protected abstract AgentDecision EvaluateOpenPosition(
        AgentMarketContext context,
        BrokerPosition position,
        AnalysisSnapshot trend,
        AnalysisSnapshot confirmation,
        AnalysisSnapshot entry);

    protected AgentDecision Trade(
        AgentMarketContext context,
        ScopeState state,
        AgentAction action,
        decimal confidence,
        decimal reference,
        decimal? stop,
        decimal? target,
        string reason,
        string? stopSource = null,
        string? targetSource = null)
    {
        decimal? rr = stop is not null && target is not null && stop != reference
            ? Math.Abs(target.Value - reference) / Math.Abs(reference - stop.Value)
            : null;
        return new AgentDecision
        {
            DecisionId = $"{state.SetupId}:{context.Timestamp:O}:{action}",
            SetupId = state.SetupId,
            StrategyName = Name,
            SetupStartedAt = state.StartedAt,
            ConfirmationAt = state.LastConfirmationAt,
            SignalInterval = Options.EntryInterval,
            Action = action,
            Instrument = context.Instrument,
            SuggestedQuantity = Options.Quantity,
            ReferencePrice = reference,
            StopLossPrice = stop,
            TakeProfitPrice = target,
            StopSource = stopSource,
            TargetSource = targetSource,
            ExpectedRewardRisk = rr,
            Confidence = Math.Clamp(confidence, 0m, 100m),
            CreatedAt = context.Timestamp,
            Reason = reason
        };
    }

    protected AgentDecision Close(
        AgentMarketContext context,
        BrokerPosition position,
        decimal confidence,
        string reason) => new()
    {
        DecisionId = $"{Name}:{context.Instrument.Value}:close:{context.Timestamp:O}",
        StrategyName = Name,
        Action = AgentAction.Close,
        Instrument = context.Instrument,
        SuggestedQuantity = position.Quantity,
        QuantityUnit = QuantityUnit.Units,
        ReferencePrice = context.Analysis.Get(Options.EntryInterval).LatestCandle.Prices.Close,
        Confidence = confidence,
        CreatedAt = context.Timestamp,
        Reason = reason
    };

    protected AgentDecision Observe(AgentMarketContext context, string reason) => new()
    {
        StrategyName = Name,
        Action = AgentAction.Observe,
        Instrument = context.Instrument,
        Confidence = 0m,
        CreatedAt = context.Timestamp,
        Reason = reason
    };

    protected decimal PriceActionConfidenceAdjustment(AnalysisSnapshot snapshot, SetupSide side)
    {
        if (Options.PriceActionConfirmation == PriceActionConfirmationMode.Disabled)
            return 0m;
        decimal aligned = side == SetupSide.Buy
            ? snapshot.PriceAction.BullishScore
            : snapshot.PriceAction.BearishScore;
        decimal opposing = side == SetupSide.Buy
            ? snapshot.PriceAction.BearishScore
            : snapshot.PriceAction.BullishScore;
        return Math.Clamp((aligned - opposing) * 0.10m, -10m, 10m);
    }

    protected static string PriceActionSummary(AnalysisSnapshot snapshot, SetupSide side)
    {
        PriceActionDirection direction = side == SetupSide.Buy
            ? PriceActionDirection.Bullish
            : PriceActionDirection.Bearish;
        PriceActionEvent? strongest = snapshot.PriceAction.Events
            .Where(item => item.Direction == direction)
            .OrderByDescending(item => item.Confidence)
            .FirstOrDefault();
        return strongest is null
            ? "no independent price-action trigger"
            : $"{strongest.Type} ({strongest.Confidence:F1})";
    }

    protected static SetupSide? DetectSide(
        AnalysisSnapshot snapshot,
        decimal minimumConfidence)
    {
        if (snapshot.Confidence.Total < minimumConfidence || snapshot.Indicators.Rsi is null)
            return null;
        decimal close = snapshot.LatestCandle.Prices.Close;
        decimal open = snapshot.LatestCandle.Prices.Open;
        decimal? middle = snapshot.Indicators.BollingerMiddle;
        bool bullish = snapshot.MarketStructure.Direction == MarketStructureDirection.Rising ||
            (middle is decimal bullishMiddle && close > open && close >= bullishMiddle);
        bool bearish = snapshot.MarketStructure.Direction == MarketStructureDirection.Falling ||
            (middle is decimal bearishMiddle && close < open && close <= bearishMiddle);
        if (bullish && snapshot.Indicators.Rsi is >= 45m and < 75m)
            return SetupSide.Buy;
        if (bearish && snapshot.Indicators.Rsi is <= 55m and > 25m)
            return SetupSide.Sell;
        return null;
    }

    protected static bool Opposes(SetupSide side, AnalysisSnapshot snapshot) =>
        side == SetupSide.Buy
            ? snapshot.MarketStructure.Direction == MarketStructureDirection.Falling
            : snapshot.MarketStructure.Direction == MarketStructureDirection.Rising;

    protected static bool HasOpposingBreak(SetupSide side, AnalysisSnapshot snapshot) =>
        side == SetupSide.Buy
            ? snapshot.MarketStructure.Break == MarketStructureBreak.Bearish
            : snapshot.MarketStructure.Break == MarketStructureBreak.Bullish;
}
