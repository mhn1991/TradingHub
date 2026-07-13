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

    private readonly Dictionary<InstrumentKey, ScopeState> _states = [];
    protected readonly ProgressiveStrategyOptions Options;

    protected ProgressiveStrategyBase(ProgressiveStrategyOptions? options)
    {
        Options = options ?? new ProgressiveStrategyOptions();
        Options.Validate();
        RequiredIntervals = new HashSet<BarInterval>
        {
            Options.TrendInterval, Options.ConfirmationInterval, Options.EntryInterval
        };
    }

    public abstract string Name { get; }

    /// <summary>Legacy uses protective-stop exit; Improved uses full brackets.</summary>
    public abstract AgentExitManagementMode ExitManagementMode { get; }

    public IReadOnlySet<BarInterval> RequiredIntervals { get; }
    public BarInterval TriggerInterval => Options.EntryInterval;

    public Task<AgentDecision> EvaluateAsync(AgentMarketContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        AnalysisSnapshot trend = context.Analysis.Get(Options.TrendInterval);
        AnalysisSnapshot confirmation = context.Analysis.Get(Options.ConfirmationInterval);
        AnalysisSnapshot entry = context.Analysis.Get(Options.EntryInterval);

        BrokerPosition? position = context.Positions.FirstOrDefault(p => p.Instrument == context.Instrument);
        if (position is not null)
        {
            // Any stale entry scope is irrelevant once a position exists.
            _states.Remove(context.Instrument);
            return Task.FromResult(EvaluateOpenPosition(context, position, trend, confirmation, entry));
        }

        SetupSide? trendSide = DetectSide(trend, Options.MinimumTrendConfidence);
        ScopeState? state = _states.GetValueOrDefault(context.Instrument);

        if (state is null)
        {
            if (trendSide is null)
            {
                return Task.FromResult(Observe(context, "Waiting for a valid higher-timeframe partial setup."));
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
                    return Task.FromResult(Observe(context, "The new trend candle invalidated the scoped setup."));
                }

                if (trendSide != state.Side)
                {
                    // A confirmed opposite higher-timeframe setup supersedes the old one
                    // immediately. It may continue through a confirmation candle that
                    // closed at the same timestamp, but never through older snapshots.
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
                return Task.FromResult(Observe(context, "The higher-timeframe partial setup expired before confirmation."));
            }
        }

        if (Opposes(state.Side, trend) || HasOpposingBreak(state.Side, trend))
        {
            _states.Remove(context.Instrument);
            return Task.FromResult(Observe(context, "Higher-timeframe structure invalidated the setup."));
        }

        SetupSide? confirmationSide = DetectSide(confirmation, Options.MinimumConfirmationConfidence);
        if (state.Stage == SetupStage.WaitingForConfirmation)
        {
            bool confirmationBelongsToSetup = confirmation.AvailableAt >= state.StartedAt;
            if (!confirmationBelongsToSetup || confirmationSide != state.Side)
            {
                return Task.FromResult(Observe(
                    context,
                    $"{Options.ConfirmationInterval} has not confirmed {state.Side}."));
            }

            if (HasOpposingBreak(state.Side, confirmation))
            {
                _states.Remove(context.Instrument);
                return Task.FromResult(Observe(context, "The confirmation timeframe invalidated the setup."));
            }

            state = state with
            {
                Stage = SetupStage.WaitingForEntry,
                LastConfirmationAt = confirmation.AvailableAt,
                ExpiresAt = Options.ConfirmationInterval.AddTo(confirmation.AvailableAt)
            };
            _states[context.Instrument] = state;
        }
        else
        {
            bool hasFreshConfirmation = state.LastConfirmationAt is null ||
                confirmation.AvailableAt > state.LastConfirmationAt.Value;
            if (hasFreshConfirmation)
            {
                if (confirmationSide != state.Side || HasOpposingBreak(state.Side, confirmation))
                {
                    _states.Remove(context.Instrument);
                    return Task.FromResult(Observe(context, "The confirmation timeframe invalidated the setup."));
                }

                state = state with
                {
                    LastConfirmationAt = confirmation.AvailableAt,
                    ExpiresAt = Options.ConfirmationInterval.AddTo(confirmation.AvailableAt)
                };
                _states[context.Instrument] = state;
            }
            else if (context.Timestamp >= state.ExpiresAt)
            {
                _states.Remove(context.Instrument);
                return Task.FromResult(Observe(context, "The lower-timeframe entry window expired."));
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
                $"Scoped {state.Side} setup is waiting for {Options.EntryInterval} entry confirmation."));
        }

        AgentDecision decision = CreateEntryDecision(context, state, trend, confirmation, entry);
        if (decision.Action is AgentAction.Buy or AgentAction.Sell)
        {
            _states.Remove(context.Instrument);
        }

        return Task.FromResult(decision);
    }

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
        AgentMarketContext context, ScopeState state, AnalysisSnapshot trend,
        AnalysisSnapshot confirmation, AnalysisSnapshot entry);

    protected abstract AgentDecision EvaluateOpenPosition(
        AgentMarketContext context, BrokerPosition position, AnalysisSnapshot trend,
        AnalysisSnapshot confirmation, AnalysisSnapshot entry);

    protected AgentDecision Trade(
        AgentMarketContext context, ScopeState state, AgentAction action, decimal confidence,
        decimal reference, decimal? stop, decimal? target, string reason,
        string? stopSource = null, string? targetSource = null)
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

    protected AgentDecision Close(AgentMarketContext context, BrokerPosition position, decimal confidence, string reason) => new()
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

    protected static SetupSide? DetectSide(AnalysisSnapshot snapshot, decimal minimumConfidence)
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
        if (bullish && snapshot.Indicators.Rsi is >= 45m and < 75m) return SetupSide.Buy;
        if (bearish && snapshot.Indicators.Rsi is <= 55m and > 25m) return SetupSide.Sell;
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
