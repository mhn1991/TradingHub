using Agent.Models;
using Agent.Strategies;
using Agent.Strategies.StructuralConfluence.Evidence;
using ChartAnnotator.Models;
using ChartAnnotator.Regime;

namespace Agent.Strategies.StructuralConfluence.Playbooks;

/// <summary>
/// Trend-continuation entry driven by indicator confluence: ADX/DMI establishes trend presence
/// and direction, hidden RSI divergence identifies a pullback inside that trend, fresh structural
/// price action proves resumption, and Bollinger position/expansion describes volatility context.
/// Expansion strengthens confidence when it is already present, but must not delay a structurally
/// confirmed entry until after the resumption has moved and trend strength has started to decay.
/// Directional RSI convergence may confirm current momentum, but cannot arm the hypothesis because
/// it normally describes a move already in progress. Each input answers a different question -
/// deliberately not stacking two indicators that measure the same thing. Has no zone/pool/level
/// to anchor risk to, so unlike the other three playbooks its stop/target is a plain ATR multiple.
/// </summary>
public sealed class IndicatorConfluencePlaybook : IStructuralPlaybook
{
    public const string StableId = "structural.indicator-confluence";
    private readonly StructuralConfluenceStrategyOptions _root;
    private readonly IndicatorConfluenceOptions _options;

    public IndicatorConfluencePlaybook(StructuralConfluenceStrategyOptions options)
    {
        _root = options;
        _options = options.IndicatorConfluence;
    }

    public string PlaybookId => StableId;
    public string Version => "2.0";

    public PlaybookEvaluation Evaluate(StructuralEvidencePacket evidence, PlaybookRuntimeState state)
    {
        IndicatorConfirmationPacket indicators = evidence.Indicators;
        AdxAnalysisSnapshot adx = indicators.AdxAnalysis;

        // ADX/DMI supplies both "is there a trend" and "which way" - there is no structural zone
        // to derive direction from here, so the trend indicator itself is the direction source.
        if (adx.DirectionalBias == PriceActionDirection.Neutral)
            return Dormant("StructuralIndicatorTrendUnavailable");

        PriceActionDirection direction = adx.DirectionalBias;
        bool buy = direction == PriceActionDirection.Bullish;
        decimal price = evidence.Trigger.LatestCandle.Prices.Close;
        // Prefer setup ATR for geometry so stop distance matches the structural TF; fall back to
        // trigger ATR when setup is not ready (should be rare once evidence packet is valid).
        decimal? atr = _options.PreferSetupAtrForGeometry
            ? evidence.Setup.Indicators.Atr ?? evidence.Indicators.Atr
            : evidence.Indicators.Atr ?? evidence.Setup.Indicators.Atr;

        bool trendPasses = adx.Adx is decimal adxValue && adxValue >= _options.MinimumAdx &&
            (!_options.RequireTrendStrengthening || adx.IsTrendStrengthening);

        // The shared policy supplies causal freshness/strength checks, current momentum/
        // volatility confirmation, and opposing-signal vetoes. This playbook then applies its own
        // hypothesis semantics: only hidden divergence identifies a pullback continuation
        // catalyst. Convergence confirms a move already in progress; regular divergence belongs
        // to reversal; a squeeze release by itself belongs to breakout.
        RsiBollingerSignalAssessment signal = RsiBollingerSignalPolicy.Evaluate(
            evidence.Trigger, direction, _options.RsiBollingerSignals);
        RsiRelationshipSnapshot? observedHiddenCatalyst =
            signal.AlignedRsiRelationship is { } relationship &&
            IsAlignedHiddenDivergence(relationship.Type, direction)
                ? relationship
                : null;
        bool signalNotVetoed = !signal.IsVetoed;
        bool confirmedExpansion = signal.BollingerPositionAligned &&
            evidence.Trigger.Indicators.BollingerAnalysis.IsExpansion;

        bool contextPasses = !_options.RequireContextAlignment || IsContextAligned(evidence, direction);
        bool setupStructurePasses = IsSetupStructureCompatible(evidence, direction);
        bool cooldownPasses = IsCooldownElapsed(evidence, state);

        decimal adxQuality = adx.Adx is decimal adxForQuality
            ? Math.Clamp(50m + (adxForQuality - _options.MinimumAdx) * 2m, 0m, 100m)
            : 0m;
        // Centered on 50 (neutral), shifted by the policy's own ±MaximumConfidenceAdjustment (8 by
        // default) range - keeps this in the same 0-100 scale every other gate's quality uses
        // without letting a merely-adequate signal silently sit below MinimumConfidence the way a
        // raw indicator value used directly as quality did before.
        decimal signalAdjustment = ContinuationSignalAdjustment(signal, observedHiddenCatalyst);
        decimal signalQuality = Math.Clamp(50m + signalAdjustment * 5m, 0m, 100m);
        decimal contextQuality = buy
            ? evidence.ContextEvidence.BullishQuality
            : evidence.ContextEvidence.BearishQuality;

        string vetoReasonCode = signal.VetoReasonCode is { } veto
            ? $"StructuralIndicator{veto}"
            : "StructuralIndicatorSignalVetoed";

        StructuralGeometry geometry = BuildAtrGeometry(direction, price, atr, evidence.ExecutableSpread);

        string? observedCatalystIdentity = observedHiddenCatalyst is null
            ? null
            : RelationshipIdentity(observedHiddenCatalyst);
        bool continuedWithoutGap = state.LastAvailableAt != DateTimeOffset.MinValue &&
            evidence.AvailableAt - state.LastAvailableAt <= StructuralPlaybookRules.Bars(evidence.Trigger.Interval, 2);
        PlaybookEvaluation? previous = state.LastEvaluation;
        bool previousIsActive = continuedWithoutGap &&
            previous is { SetupId: not null } &&
            previous.Direction == direction &&
            previous.Lifecycle is StructuralSetupLifecycle.Armed or
                StructuralSetupLifecycle.CatalystObserved or
                StructuralSetupLifecycle.AwaitingTrigger or
                StructuralSetupLifecycle.CandidateProduced;
        bool sameCatalyst = previous?.CatalystIdentity is null ||
            observedCatalystIdentity is null ||
            string.Equals(
                previous.CatalystIdentity, observedCatalystIdentity, StringComparison.Ordinal);
        bool continuingHypothesis = previousIsActive && sameCatalyst;

        string? catalystIdentity = observedCatalystIdentity ??
            (continuingHypothesis ? previous!.CatalystIdentity : null);
        DateTimeOffset? catalystAt = observedHiddenCatalyst?.ConfirmedAt ??
            (continuingHypothesis ? previous!.CatalystAt : null);
        bool hasActiveCatalyst = catalystIdentity is not null && catalystAt is not null;
        bool continuationCatalyst = hasActiveCatalyst &&
            signal.RsiMomentumAligned && signal.BollingerPositionAligned;

        // A hypothesis does not exist until its baseline trend/context/geometry is valid. This
        // prevents a relationship seen before ADX establishes a trend from being "invalidated
        // before birth", while still making later failure terminal once the hypothesis was armed.
        bool baselinePasses = trendPasses && signalNotVetoed && contextPasses &&
            setupStructurePasses && geometry.IsValid;
        string? setupId = null;
        if (continuingHypothesis)
        {
            setupId = previous!.SetupId;
        }
        else if (baselinePasses)
        {
            string setupCatalystIdentity = catalystIdentity is null
                ? $"trend-{direction}"
                : catalystIdentity;
            setupId = StructuralIdentity.Create(_root.StrategyVersion, evidence.Instrument, PlaybookId,
                null, null, setupCatalystIdentity,
                observedHiddenCatalyst?.ConfirmedAt ?? evidence.AvailableAt, direction);
        }

        bool terminalCatalyst = catalystIdentity is not null &&
            state.TerminalCatalystIdentities.Contains(catalystIdentity, StringComparer.Ordinal);
        DateTimeOffset? catalystExpiresAt = catalystAt is DateTimeOffset expiryStart
            ? StructuralPlaybookRules.TriggerWindowExpiresAt(
                expiryStart,
                evidence.Trigger.Interval,
                _root.MaximumTriggerBars,
                _root.MaximumArmedSetupBars)
            : null;
        bool catalystExpired = hasActiveCatalyst &&
            catalystExpiresAt is DateTimeOffset expiresAt &&
            evidence.AvailableAt > expiresAt;

        // RSI is the catalyst, not the entry. The actual entry trigger must be a fresh,
        // directionally aligned price-action resumption confirmed after that catalyst.
        (PriceActionEvent? Event, PriceActionSetup? Setup, decimal Quality) resumption =
            catalystAt is DateTimeOffset observedAt
                ? StructuralPlaybookRules.Trigger(
                    evidence, direction, StructuralTriggerProfile.IndicatorTrendContinuation,
                    _root.Trigger.MinimumPriceActionConfidence,
                    StructuralPlaybookRules.TriggerResponseBars(
                        _root.MaximumTriggerBars, _root.MaximumArmedSetupBars),
                    observedAt)
                : (null, null, 0m);
        bool priceActionTriggered = resumption.Event is not null || resumption.Setup is not null;

        PriceActionDirection opposingDirection = buy
            ? PriceActionDirection.Bearish
            : PriceActionDirection.Bullish;
        (PriceActionEvent? Event, PriceActionSetup? Setup, decimal Quality) structuralInvalidation =
            catalystAt is DateTimeOffset invalidationWindowStart
                ? StructuralPlaybookRules.Trigger(
                    evidence, opposingDirection,
                    StructuralTriggerProfile.IndicatorStructuralInvalidation,
                    _root.Trigger.MinimumPriceActionConfidence, _root.MaximumTriggerBars,
                    invalidationWindowStart)
                : (null, null, 0m);
        DateTimeOffset? resumptionAt =
            resumption.Event?.ConfirmedAt ?? resumption.Setup?.TriggeredAt;
        (PriceActionEvent? Event, PriceActionSetup? Setup, decimal Quality) entryConflict =
            resumptionAt is DateTimeOffset conflictWindowStart
                ? StructuralPlaybookRules.Trigger(
                    evidence, opposingDirection, StructuralTriggerProfile.IndicatorStrongOpposition,
                    _root.Trigger.MinimumPriceActionConfidence, _root.MaximumTriggerBars,
                    conflictWindowStart)
                : (null, null, 0m);
        bool structurallyInvalidated =
            structuralInvalidation.Event is not null || structuralInvalidation.Setup is not null;
        bool conflictingAtEntry =
            entryConflict.Event is not null || entryConflict.Setup is not null;
        bool strongOpposition = _root.Context.StrongOppositionVeto &&
            (structurallyInvalidated || conflictingAtEntry);
        (PriceActionEvent? Event, PriceActionSetup? Setup, decimal Quality) opposition =
            structurallyInvalidated ? structuralInvalidation : entryConflict;
        bool priceActionNotOpposed = !strongOpposition;

        var gates = new List<MandatoryGate>
        {
            Gate("Trend", trendPasses, adxQuality, "StructuralIndicatorTrendBelowMinimum"),
            Gate("SignalVeto", signalNotVetoed, signalNotVetoed ? 70m : 0m, vetoReasonCode),
            Gate("ContinuationCatalyst", continuationCatalyst, signalQuality,
                "StructuralIndicatorContinuationSignalMissing"),
            Gate("TerminalCatalyst", !terminalCatalyst, terminalCatalyst ? 0m : 70m,
                "StructuralIndicatorCatalystTerminated"),
            Gate("CatalystExpiry", !catalystExpired, catalystExpired ? 0m : 70m,
                "StructuralIndicatorCatalystExpired"),
            Gate("PriceActionOpposition", priceActionNotOpposed, priceActionNotOpposed ? 70m : 0m,
                "StructuralIndicatorStrongOpposingPriceAction"),
            Gate("PriceActionTrigger", priceActionTriggered, resumption.Quality,
                "StructuralIndicatorPriceActionTriggerMissing"),
            Gate("Context", contextPasses, Math.Clamp(contextQuality, 0m, 100m),
                "StructuralIndicatorContextOpposed"),
            Gate("SetupStructure", setupStructurePasses, setupStructurePasses ? 70m : 0m,
                "StructuralIndicatorSetupStructureOpposed"),
            Gate("Cooldown", cooldownPasses, cooldownPasses ? 70m : 0m,
                "StructuralIndicatorCooldownActive"),
            Gate("Geometry", geometry.IsValid, geometry.Quality, geometry.ReasonCode)
        };

        // Sticky setup and catalyst identities prevent a selected signal from firing twice, even
        // when an evaluation gap causes the deterministic setup identity to be minted differently.
        bool setupAlreadySignaled = setupId is not null &&
            string.Equals(setupId, state.LastReadySetupId, StringComparison.Ordinal);
        bool catalystAlreadySignaled = catalystIdentity is not null &&
            string.Equals(
                catalystIdentity, state.LastReadyCatalystIdentity, StringComparison.Ordinal);
        bool notAlreadySignaled = !setupAlreadySignaled && !catalystAlreadySignaled;
        gates.Add(Gate("NotAlreadySignaled", notAlreadySignaled, notAlreadySignaled ? 70m : 0m,
            "StructuralIndicatorAlreadySignaled"));

        decimal confidence = StructuralPlaybookRules.Confidence(gates, 0m, signalAdjustment, 0m);
        bool ready = gates.All(item => item.Passed) && confidence >= _options.MinimumConfidence;
        StructuralSetupLifecycle lifecycle = ready
            ? StructuralSetupLifecycle.CandidateProduced :
            terminalCatalyst || strongOpposition
                ? StructuralSetupLifecycle.Invalidated :
            !baselinePasses
                ? continuingHypothesis
                    ? StructuralSetupLifecycle.Invalidated
                    : StructuralSetupLifecycle.Dormant :
            catalystExpired ? StructuralSetupLifecycle.Expired :
            !hasActiveCatalyst ? StructuralSetupLifecycle.Armed :
            !continuationCatalyst || !priceActionTriggered
                ? StructuralSetupLifecycle.CatalystObserved :
            StructuralSetupLifecycle.AwaitingTrigger;

        return new PlaybookEvaluation
        {
            PlaybookId = PlaybookId,
            Version = Version,
            Direction = direction,
            Lifecycle = lifecycle,
            SetupId = setupId,
            CatalystAt = catalystAt,
            CatalystIdentity = catalystIdentity,
            MandatoryGates = gates.AsReadOnly(),
            SupportingEvidence = BuildSupport(
                trendPasses, signal, observedHiddenCatalyst, hasActiveCatalyst,
                resumption.Event, resumption.Setup, confirmedExpansion, contextPasses,
                setupStructurePasses),
            ConflictingEvidence = BuildConflicts(
                signal, vetoReasonCode, terminalCatalyst, opposition.Event, opposition.Setup,
                strongOpposition),
            ConfidenceContributions =
            [
                new("Trend", 0m, trendPasses ? "StructuralIndicatorTrendAligned" : "StructuralIndicatorTrendMissing"),
                new("IndicatorSignal", signalAdjustment,
                    !hasActiveCatalyst
                        ? "No hidden-divergence continuation catalyst."
                        : signal.Explanation)
            ],
            ContextQuality = Math.Max(adxQuality, contextQuality),
            LocationQuality = 0m,
            CatalystQuality = hasActiveCatalyst
                ? observedHiddenCatalyst is not null
                    ? signalQuality
                    : previous?.CatalystQuality ?? signalQuality
                : 0m,
            TriggerQuality = priceActionTriggered ? resumption.Quality : 0m,
            ConfirmationQuality = confirmedExpansion ? signalQuality : 0m,
            GeometryQuality = geometry.Quality,
            Confidence = confidence,
            ExpiresAt = catalystExpiresAt,
            ReasonCode = ready ? "StructuralIndicatorConfluenceCandidate" : FirstFailure(gates, confidence, _options.MinimumConfidence),
            IsReady = ready,
            Geometry = geometry,
            TriggerEvent = resumption.Event,
            TriggerSetup = resumption.Setup
        };
    }

    private bool IsCooldownElapsed(StructuralEvidencePacket evidence, PlaybookRuntimeState state)
    {
        if (_options.MinimumBarsBetweenEntries <= 0)
            return true;
        // Only enforce after we have already produced a ready (and typically traded) identity.
        // Reads LastReadyCatalystAt (only ever updated by a ready evaluation), not
        // LastEvaluation.CatalystAt (updated by every evaluation, ready or not) - the latter let a
        // non-ready bar's freshly minted catalyst keep pushing the cooldown window forward forever,
        // permanently locking the playbook out after its first trade.
        if (state.LastReadySetupId is null)
            return true;
        DateTimeOffset? lastSignalAt = state.LastReadyCatalystAt;
        if (lastSignalAt is null)
            return true;
        TimeSpan cooldown = StructuralPlaybookRules.Bars(evidence.Trigger.Interval, _options.MinimumBarsBetweenEntries);
        return evidence.AvailableAt >= lastSignalAt.Value + cooldown;
    }

    private static decimal ContinuationSignalAdjustment(
        RsiBollingerSignalAssessment signal,
        RsiRelationshipSnapshot? relationship)
    {
        decimal adjustment = 0m;
        if (relationship is not null)
            adjustment += Math.Min(4m, relationship.Strength / 20m);
        if (signal.RsiMomentumAligned)
            adjustment += 1m;
        else if (signal.RsiMomentumOpposes)
            adjustment -= 2m;
        if (signal.BollingerExpansionAligned)
            adjustment += 2m;
        else if (signal.BollingerPositionAligned)
            adjustment += 0.5m;
        if (signal.BollingerExpansionOpposes)
            adjustment -= 4m;
        if (signal.OpposingRsiRelationship is not null)
            adjustment -= Math.Min(5m, signal.OpposingRsiRelationship.Strength / 15m);
        return Math.Clamp(adjustment, -8m, 8m);
    }

    private static bool IsAlignedHiddenDivergence(
        RsiRelationshipType type,
        PriceActionDirection direction) =>
        direction == PriceActionDirection.Bullish
            ? type == RsiRelationshipType.HiddenBullishDivergence
            : type == RsiRelationshipType.HiddenBearishDivergence;

    private static bool IsSetupStructureCompatible(
        StructuralEvidencePacket evidence,
        PriceActionDirection direction)
    {
        MarketStructureDirection structure = evidence.Setup.MarketStructure.Direction;
        MarketRegime regime = evidence.Setup.MarketRegime.Regime;
        return direction == PriceActionDirection.Bullish
            ? structure != MarketStructureDirection.Falling &&
                regime is not (MarketRegime.TrendingDown or MarketRegime.BreakoutExpansionDown)
            : structure != MarketStructureDirection.Rising &&
                regime is not (MarketRegime.TrendingUp or MarketRegime.BreakoutExpansionUp);
    }

    private static bool IsContextAligned(StructuralEvidencePacket evidence, PriceActionDirection direction)
    {
        if (evidence.Context.MarketRegime.Regime == MarketRegime.IlliquidUnsafe)
            return false;

        MarketRegime regime = evidence.Context.MarketRegime.Regime;
        bool structureOk = direction == PriceActionDirection.Bullish
            ? regime is MarketRegime.TrendingUp or MarketRegime.BreakoutExpansionUp
                || evidence.Context.MarketStructure.Direction == MarketStructureDirection.Rising
            : regime is MarketRegime.TrendingDown or MarketRegime.BreakoutExpansionDown
                || evidence.Context.MarketStructure.Direction == MarketStructureDirection.Falling;

        EvidenceAlignment alignment = direction == PriceActionDirection.Bullish
            ? evidence.ContextEvidence.BullishAlignment
            : evidence.ContextEvidence.BearishAlignment;

        // Allow when context is explicitly aligned or structure/regime agrees; block hard conflict.
        if (alignment == EvidenceAlignment.Conflicting)
            return false;
        if (alignment == EvidenceAlignment.Aligned)
            return true;
        return structureOk;
    }

    private StructuralGeometry BuildAtrGeometry(
        PriceActionDirection direction,
        decimal price,
        decimal? atr,
        decimal executableSpread)
    {
        if (atr is not > 0m)
            return Invalid(price, "StructuralIndicatorAtrUnavailable");

        bool buy = direction == PriceActionDirection.Bullish;
        decimal entry = price + (buy ? executableSpread : -executableSpread) / 2m;
        decimal stop = entry + (buy ? -1m : 1m) * _options.StopAtr * atr.Value;
        decimal target = entry + (buy ? 1m : -1m) * _options.TargetAtr * atr.Value;
        decimal risk = buy ? entry - stop : stop - entry;
        decimal reward = buy ? target - entry : entry - target;
        if (risk <= 0m || reward <= 0m)
            return Invalid(entry, "StructuralIndicatorGeometryInvalid");
        if (executableSpread > 0m &&
            risk < executableSpread * _root.Geometry.MinimumRiskToSpreadMultiple)
            return Invalid(entry, "StructuralStopDistanceBelowExecutionCostFloor");
        if (executableSpread > 0m && reward < executableSpread * _options.MinimumRewardToSpreadMultiple)
            return Invalid(entry, "StructuralIndicatorRewardBelowCostFloor");

        decimal rewardRisk = reward / risk;
        // Matches the other three playbooks' floor (via StructuralGeometryBuilder.Build), which
        // this ATR-multiple geometry bypassed entirely before - StopAtr/TargetAtr fixed the ratio
        // regardless of _root.MinimumRewardRisk, so raising --minimum-rr silently had no effect
        // here. Reason code matches StructuralGeometryBuilder's for consistent funnel reporting.
        if (rewardRisk < _root.MinimumRewardRisk)
            return Invalid(entry, "StructuralRewardRiskBelowMinimum");
        decimal quality = Math.Clamp(50m + Math.Min(40m, (rewardRisk - 1m) * 15m), 0m, 100m);
        return new StructuralGeometry
        {
            IsValid = true,
            Entry = entry,
            Stop = stop,
            Target = target,
            RewardRisk = rewardRisk,
            Quality = quality,
            StopSource = $"AtrMultiple:{_options.StopAtr:F2}",
            TargetSource = $"AtrMultiple:{_options.TargetAtr:F2}",
            ReasonCode = "StructuralIndicatorGeometryValid"
        };
    }

    private static StructuralGeometry Invalid(decimal entry, string reasonCode) => new()
    {
        IsValid = false,
        Entry = entry,
        ReasonCode = reasonCode
    };

    private PlaybookEvaluation Dormant(string reasonCode) => new()
    {
        PlaybookId = PlaybookId,
        Version = Version,
        Direction = PriceActionDirection.Neutral,
        Lifecycle = StructuralSetupLifecycle.Dormant,
        ReasonCode = reasonCode
    };

    private static string RelationshipIdentity(RsiRelationshipSnapshot relationship) =>
        $"{relationship.Type}:{relationship.ConfirmedAt:O}:{relationship.SecondPivotTime:O}";

    private static IReadOnlyList<string> BuildSupport(
        bool trend,
        RsiBollingerSignalAssessment signal,
        RsiRelationshipSnapshot? observedHiddenCatalyst,
        bool hasActiveCatalyst,
        PriceActionEvent? triggerEvent,
        PriceActionSetup? triggerSetup,
        bool confirmedExpansion,
        bool context,
        bool setupStructure)
    {
        var result = new List<string>();
        if (trend) result.Add("StructuralIndicatorTrendAligned");
        if (signal.RsiMomentumAligned) result.Add("StructuralIndicatorMomentumAligned");
        if (observedHiddenCatalyst is not null)
            result.Add($"StructuralIndicatorRsi{observedHiddenCatalyst.Type}");
        else if (hasActiveCatalyst)
            result.Add("StructuralIndicatorHiddenDivergenceCatalystActive");
        if (triggerEvent is not null)
            result.Add($"StructuralIndicatorPriceAction{triggerEvent.Type}");
        if (triggerSetup is not null)
            result.Add($"StructuralIndicatorPriceAction{triggerSetup.Type}");
        if (confirmedExpansion)
            result.Add("StructuralIndicatorBollingerConfirmedExpansionAligned");
        else if (signal.BollingerExpansionAligned)
            result.Add("StructuralIndicatorBollingerWidthExpanding");
        else if (signal.BollingerPositionAligned)
            result.Add("StructuralIndicatorBollingerPositionAligned");
        if (context) result.Add("StructuralIndicatorContextAligned");
        if (setupStructure) result.Add("StructuralIndicatorSetupStructureCompatible");
        return result.AsReadOnly();
    }

    private static IReadOnlyList<string> BuildConflicts(
        RsiBollingerSignalAssessment signal,
        string vetoReasonCode,
        bool terminalCatalyst,
        PriceActionEvent? opposingEvent,
        PriceActionSetup? opposingSetup,
        bool strongOpposition)
    {
        var result = new List<string>();
        if (signal.IsVetoed)
            result.Add(vetoReasonCode);
        if (terminalCatalyst)
            result.Add("StructuralIndicatorCatalystTerminated");
        if (strongOpposition && opposingEvent is not null)
            result.Add($"StructuralIndicatorOpposingPriceAction{opposingEvent.Type}");
        if (strongOpposition && opposingSetup is not null)
            result.Add($"StructuralIndicatorOpposingPriceAction{opposingSetup.Type}");
        return result.AsReadOnly();
    }

    private static MandatoryGate Gate(string name, bool passed, decimal quality, string failure) =>
        new(name, passed, Math.Clamp(quality, 0m, 100m), passed ? $"{name}Passed" : failure);

    private static string FirstFailure(IReadOnlyList<MandatoryGate> gates, decimal confidence, decimal minimum) =>
        gates.FirstOrDefault(item => !item.Passed)?.ReasonCode ??
        (confidence < minimum ? "StructuralConfidenceBelowMinimum" : "StructuralCandidateNotReady");
}
