using Agent.Models;
using Agent.Strategies.StructuralConfluence.Evidence;
using ChartAnnotator.Models;
using ChartAnnotator.Regime;
using ChartAnnotator.SupplyDemand;
using ChartAnnotator.TargetManagement;

namespace Agent.Strategies.StructuralConfluence.Playbooks;

public sealed class SupplyDemandPullbackPlaybook : IStructuralPlaybook
{
    public const string StableId = "structural.supply-demand-pullback";
    private readonly StructuralConfluenceStrategyOptions _root;
    private readonly SupplyDemandPullbackOptions _options;
    private readonly StructuralGeometryBuilder _geometry;

    public SupplyDemandPullbackPlaybook(StructuralConfluenceStrategyOptions options)
    {
        _root = options;
        _options = options.SupplyDemandPullback;
        _geometry = new StructuralGeometryBuilder(options);
    }

    public string PlaybookId => StableId;
    public string Version => "1.9";

    public PlaybookEvaluation Evaluate(StructuralEvidencePacket evidence, PlaybookRuntimeState state)
    {
        decimal price = evidence.Trigger.LatestCandle.Prices.Close;
        // Prefer nearer zones, then HTF-aligned side (demand in bullish context / supply in
        // bearish), then quality. Avoids always taking a counter-trend nearest print. Evaluates
        // every permitted zone (not just the single top-ranked one) so a zone still waiting on its
        // trigger/confirmation isn't dropped from consideration just because a closer or
        // higher-quality zone also happens to be live this same bar - see
        // LiquidityBreakRetestPlaybook's identical fix for the starvation bug this prevents.
        SupplyDemandZone[] candidates = evidence.SupplyDemand.Zones
            .Where(item => _options.PermittedZoneStates.Contains(item.State))
            .OrderBy(item => Distance(price, item))
            .ThenBy(item => ContextAffinityRank(evidence, item))
            .ThenByDescending(item => item.QualityScore)
            .ThenByDescending(item => item.AvailableAt)
            .ThenBy(item => item.ZoneId)
            .ToArray();
        if (candidates.Length == 0)
            return Dormant("StructuralZoneUnavailable");

        var evaluations = new List<PlaybookEvaluation>(candidates.Length);
        foreach (SupplyDemandZone candidate in candidates)
        {
            PlaybookEvaluation evaluation = EvaluateCandidate(candidate, evidence, state);
            evaluations.Add(evaluation);
        }

        // Keep diagnostics/runtime state attached to the most-progressed viable hypothesis.
        // A merely nearer zone must not hide an older reaction that is already awaiting a trigger.
        return StructuralPlaybookRules.SelectRepresentativeCandidate(evaluations);
    }

    private PlaybookEvaluation EvaluateCandidate(
        SupplyDemandZone zone, StructuralEvidencePacket evidence, PlaybookRuntimeState state)
    {
        decimal price = evidence.Trigger.LatestCandle.Prices.Close;
        PriceActionDirection direction = zone.Type == SupplyDemandZoneType.Demand
            ? PriceActionDirection.Bullish
            : PriceActionDirection.Bearish;
        bool buy = direction == PriceActionDirection.Bullish;
        SupplyDemandZoneEvent? reaction = evidence.SupplyDemand.Events
            .Where(item => item.ZoneId == zone.ZoneId &&
                item.EventType is SupplyDemandZoneEventType.Touched or SupplyDemandZoneEventType.PartiallyMitigated)
            .OrderByDescending(item => item.AvailableAt).ThenBy(item => item.EventId).FirstOrDefault();
        // Without this, a zone touched once stayed a valid reaction catalyst forever - entry
        // (built off current price) vs. a stop anchored to that ancient zone routinely blew past
        // Geometry's MaximumStopDistanceAtr many bars later, once price had run on. See
        // MaximumBarsSinceReaction's doc comment for the replay evidence behind this fix.
        bool reactionFresh = reaction is not null &&
            StructuralPlaybookRules.IsFresh(reaction.AvailableAt, evidence.AvailableAt, evidence.Setup.Interval, _options.MaximumBarsSinceReaction);
        bool invalidated = evidence.SupplyDemand.Events.Any(item => item.ZoneId == zone.ZoneId &&
            (item.EventType is SupplyDemandZoneEventType.Invalidated or SupplyDemandZoneEventType.Mitigated) &&
            item.AvailableAt <= evidence.AvailableAt);
        bool contextAligned = IsContextAligned(evidence, direction);
        bool illiquidUnsafe = evidence.Context.MarketRegime.Regime == MarketRegime.IlliquidUnsafe;
        // This playbook has one job: a continuation entry on the first return to a trend-aligned
        // zone. Range-boundary reversal belongs exclusively to LiquiditySweepReversalPlaybook.
        // RequireTrendAlignment=false remains an explicit compatibility override for experiments,
        // but it no longer changes trigger or confirmation semantics into a reversal hypothesis.
        bool contextAllowed = !illiquidUnsafe && !IsContextOpposed(evidence, direction) &&
            (!_options.RequireTrendAlignment || contextAligned);
        DateTimeOffset? catalystAt = reaction?.AvailableAt;
        int triggerResponseBars = StructuralPlaybookRules.TriggerResponseBars(
            _root.MaximumTriggerBars, _root.MaximumArmedSetupBars);
        DateTimeOffset? reactionExpiresAt = reaction is null
            ? null
            : reaction.AvailableAt + StructuralPlaybookRules.Bars(
                evidence.Setup.Interval, _options.MaximumBarsSinceReaction);
        DateTimeOffset? triggerExpiresAt = reaction is null
            ? null
            : StructuralPlaybookRules.TriggerWindowExpiresAt(
                reaction.AvailableAt,
                evidence.Trigger.Interval,
                _root.MaximumTriggerBars,
                _root.MaximumArmedSetupBars);
        bool triggerWindowExpired = triggerExpiresAt is DateTimeOffset expiredTriggerAt &&
            evidence.AvailableAt > expiredTriggerAt;
        (PriceActionEvent? triggerEvent, PriceActionSetup? triggerSetup, decimal triggerQuality) =
            catalystAt is DateTimeOffset observedAt
                ? StructuralPlaybookRules.Trigger(
                    evidence, direction, StructuralTriggerProfile.TrendPullback,
                    _root.Trigger.MinimumPriceActionConfidence, triggerResponseBars, observedAt,
                    new StructuralTriggerAnchor(zone.ProximalPrice, zone.DistalPrice))
                : (null, null, 0m);
        bool triggerPresent = triggerEvent is not null || triggerSetup is not null || !_options.RequirePriceActionTrigger;
        CciAssessment cci = StructuralPlaybookRules.AssessCci(
            evidence, direction, continuation: true);
        RsiAssessment rsi = StructuralPlaybookRules.AssessRsi(
            evidence, direction, continuation: true);
        RsiBollingerSignalAssessment rsiBollinger = RsiBollingerSignalPolicy.Evaluate(
            evidence.Trigger, direction, _root.IndicatorConfluence.RsiBollingerSignals);
        LocalTrendOppositionAssessment localTrendOpposition =
            StructuralPlaybookRules.AssessLocalTrendOpposition(
                direction, evidence.Indicators.AdxAnalysis, cci, rsiBollinger);
        bool bollingerReEntry = HasBollingerReEntry(evidence, direction);
        // A wick back inside a band is not reversal/continuation confirmation while that band is
        // actively expanding in the opposite direction.
        bool bollingerReEntryConfirms =
            bollingerReEntry && !rsiBollinger.BollingerExpansionOpposes;
        bool alternativeConfirmation =
            (_options.AllowRsiAlternativeConfirmation && rsi.Alignment == EvidenceAlignment.Aligned) ||
            (_options.AllowBollingerReEntryConfirmation && bollingerReEntryConfirms);
        bool confirmationPasses = _options.CciMode != StructuralConfirmationMode.Required ||
            cci.Alignment == EvidenceAlignment.Aligned ||
            (cci.Alignment != EvidenceAlignment.Conflicting && alternativeConfirmation);
        // QualityScore and FreshnessScore are both unit-interval [0, 1].
        decimal locationQuality = Math.Clamp(Math.Min(zone.QualityScore, zone.FreshnessScore) * 100m, 0m, 100m);

        // Identity is tied to the zone + catalyst reaction, not to whether it already produced a
        // trade - see LiquiditySweepReversalPlaybook's identical guard for the observed bug this
        // prevents (same identity re-arming and re-entering immediately after a prior attempt on
        // it already closed). state.LastReadySetupId is sticky across non-ready frames and frozen
        // for the whole holding period (StructuralConfluenceAgent skips Evaluate entirely while a
        // position is open), so this only blocks a genuine repeat, never a setup that's simply
        // still armed across consecutive pre-entry frames.
        // The identity starts when the zone is armed and remains unchanged when its first return
        // arrives. This lets the state store describe one coherent hypothesis instead of replacing
        // the setup at the exact moment its catalyst appears.
        string catalystIdentity = $"zone-{zone.ZoneId:N}";
        string setupId = StructuralIdentity.Create(_root.StrategyVersion, evidence.Instrument, PlaybookId,
            null, zone.ZoneId, catalystIdentity, zone.AvailableAt, direction);
        bool notAlreadySignaled = setupId != state.LastReadySetupId;

        var gates = new List<MandatoryGate>
        {
            Gate("NotAlreadySignaled", notAlreadySignaled, locationQuality, "StructuralZoneAlreadySignaled"),
            Gate("Context", contextAllowed, ContextQuality(evidence, direction),
                illiquidUnsafe ? "StructuralContextIlliquidUnsafe" : "StructuralContextOpposed"),
            Gate("ZoneQuality", zone.QualityScore >= _options.MinimumZoneQuality, locationQuality, "StructuralZoneQualityLow"),
            Gate("ZoneState", _options.PermittedZoneStates.Contains(zone.State), locationQuality, "StructuralZoneStateRejected"),
            // DistinctTouchCount (cooldown-throttled), not raw TouchCount: a zone price simply sat
            // on for several consecutive candles inflates TouchCount without representing multiple
            // genuine visits, which was disqualifying zones that were really only tested once (see
            // LiquidityPool.DistinctTouchCount's doc comment for the same fix applied to
            // break-retest's PoolQuality gate).
            Gate("ZoneTouches", zone.DistinctTouchCount <= _options.MaximumPriorTouches, locationQuality, "StructuralZoneTouchLimitExceeded"),
            Gate("ZonePenetration", zone.PenetrationRatio <= _options.MaximumPenetrationRatio, locationQuality, "StructuralZonePenetrationExceeded"),
            Gate("ZoneValid", !invalidated, locationQuality, "StructuralZoneInvalidated"),
            Gate("ZoneReaction", reaction is not null,
                reaction is null ? 0m : locationQuality, "StructuralZoneReactionMissing"),
            Gate("ReactionFresh", reaction is null || reactionFresh,
                reactionFresh ? locationQuality : 0m, "StructuralZoneReactionExpired"),
            Gate("TriggerWindow", !triggerWindowExpired, triggerWindowExpired ? 0m : 60m,
                "StructuralTriggerWindowExpired"),
            Gate("Trigger", triggerPresent, triggerPresent ? Math.Max(triggerQuality, 50m) : 0m,
                "StructuralTriggerMissing", _options.RequirePriceActionTrigger),
            Gate("Confirmation", confirmationPasses,
                Math.Max(cci.Quality, alternativeConfirmation ? 55m : 0m),
                "StructuralConfirmationMissing",
                _options.CciMode == StructuralConfirmationMode.Required),
            // This is a veto-only semantic contradiction, not a confidence input. Requiring three
            // independent feature families prevents one noisy indicator from rejecting a valid
            // pullback while still blocking entries into a coherent local trend the other way.
            Gate("LocalTrendResumption", !localTrendOpposition.IsCoherent,
                localTrendOpposition.IsCoherent ? 0m : 50m,
                "StructuralLocalTrendOpposed",
                limitsConfidenceFloor: false)
        };

        decimal rawStop = buy ? Math.Min(zone.ProximalPrice, zone.DistalPrice) : Math.Max(zone.ProximalPrice, zone.DistalPrice);
        string catalystSourceId = $"SupplyDemandZone:{zone.ZoneId:N}";
        // A trend-aligned pullback runs a partial-then-runner. Opposed context is rejected by the
        // Context gate above; range-boundary reversal belongs to the sweep playbook.
        StructuralGeometry geometry = _root.AdaptiveTargetManagement.Enabled
            ? _geometry.BuildAdaptive(evidence, direction, rawStop, catalystSourceId,
                contextAligned ? TradeExitPolicy.PartialThenRunner : TradeExitPolicy.FixedStructuralTarget,
                [catalystSourceId])
            : _geometry.BuildTieredFixed(evidence, direction, rawStop, catalystSourceId,
                [catalystSourceId]);
        gates.Add(Gate("Geometry", geometry.IsValid, geometry.Quality, geometry.ReasonCode));

        decimal contextQuality = ContextQuality(evidence, direction);
        decimal confirmationAdjustment = StructuralPlaybookRules.ConfirmationAdjustment(_options.CciMode, cci, _root.Confirmation);
        if (alternativeConfirmation && cci.Alignment != EvidenceAlignment.Conflicting)
            confirmationAdjustment = Math.Max(confirmationAdjustment, 3m);
        decimal confidence = StructuralPlaybookRules.Confidence(gates, (contextQuality - 50m) / 6.25m,
            confirmationAdjustment, 0m);
        bool ready = gates.All(item => item.Passed) && confidence >= _options.MinimumConfidence;
        // Name-based, not positional (gates.Take(N)) - a magic-number slice silently misclassifies
        // lifecycle the moment a gate gets reordered or inserted without updating the count.
        bool locationPassed = gates.Where(item => item.Name is not
            ("TriggerWindow" or "Trigger" or "Confirmation" or "LocalTrendResumption" or "Geometry"))
            .All(item => item.Passed);

        return new PlaybookEvaluation
        {
            PlaybookId = PlaybookId,
            Version = Version,
            Direction = direction,
            Lifecycle = ready ? StructuralSetupLifecycle.CandidateProduced :
                invalidated ? StructuralSetupLifecycle.Invalidated :
                reaction is not null && !reactionFresh ? StructuralSetupLifecycle.Expired :
                triggerWindowExpired ? StructuralSetupLifecycle.Expired :
                locationPassed ? StructuralSetupLifecycle.AwaitingTrigger : StructuralSetupLifecycle.Armed,
            SetupId = setupId,
            PrimaryZoneId = zone.ZoneId,
            CatalystAt = catalystAt,
            MandatoryGates = gates.AsReadOnly(),
            SupportingEvidence = BuildSupport(
                contextAligned, reaction is not null, cci, rsi, bollingerReEntryConfirms),
            ConflictingEvidence = BuildConflicts(cci, rsi, localTrendOpposition),
            ConfidenceContributions =
            [
                new("Context", Math.Clamp((contextQuality - 50m) / 6.25m, -8m, 8m), contextAligned ? "StructuralContextAligned" : "StructuralContextNeutral"),
                new("Confirmation", confirmationAdjustment, $"{cci.State};Rsi:{rsi.State}")
            ],
            ContextQuality = contextQuality,
            LocationQuality = locationQuality,
            CatalystQuality = reaction is null ? 50m : Math.Clamp(100m - zone.PenetrationRatio * 50m, 0m, 100m),
            TriggerQuality = triggerPresent ? Math.Max(triggerQuality, 50m) : 0m,
            ConfirmationQuality = Math.Max(cci.Quality,
                Math.Max(rsi.Quality, bollingerReEntryConfirms ? 60m : 0m)),
            GeometryQuality = geometry.Quality,
            Confidence = confidence,
            ExpiresAt = reactionExpiresAt is DateTimeOffset reactionDeadline &&
                triggerExpiresAt is DateTimeOffset responseDeadline
                    ? StructuralPlaybookRules.Earlier(
                        reactionDeadline, responseDeadline)
                    : null,
            ReasonCode = ready ? "StructuralSupplyDemandPullbackCandidate" : FirstFailure(gates, confidence, _options.MinimumConfidence),
            IsReady = ready,
            Geometry = geometry,
            Zone = zone,
            TriggerEvent = triggerEvent,
            TriggerSetup = triggerSetup,
            CciConfirmationState = cci.State
        };
    }

    private PlaybookEvaluation Dormant(string reasonCode) => new()
    {
        PlaybookId = PlaybookId,
        Version = Version,
        Direction = PriceActionDirection.Neutral,
        Lifecycle = StructuralSetupLifecycle.Dormant,
        ReasonCode = reasonCode
    };

    private static bool IsContextAligned(StructuralEvidencePacket evidence, PriceActionDirection direction)
    {
        if (evidence.Context.MarketRegime.Regime == MarketRegime.IlliquidUnsafe)
            return false;
        bool buy = direction == PriceActionDirection.Bullish;
        MarketRegime regime = evidence.Context.MarketRegime.Regime;
        return buy
            ? regime is MarketRegime.TrendingUp or MarketRegime.BreakoutExpansionUp || evidence.Context.MarketStructure.Direction == MarketStructureDirection.Rising
            : regime is MarketRegime.TrendingDown or MarketRegime.BreakoutExpansionDown || evidence.Context.MarketStructure.Direction == MarketStructureDirection.Falling;
    }

    /// <summary>
    /// 0 = zone side matches HTF context, 1 = neutral/unclear, 2 = opposed.
    /// Used only as a soft ranking key when multiple permitted zones exist near price.
    /// </summary>
    private static int ContextAffinityRank(StructuralEvidencePacket evidence, SupplyDemandZone zone)
    {
        PriceActionDirection direction = zone.Type == SupplyDemandZoneType.Demand
            ? PriceActionDirection.Bullish
            : PriceActionDirection.Bearish;
        if (IsContextAligned(evidence, direction))
            return 0;
        if (IsContextOpposed(evidence, direction))
            return 2;
        return 1;
    }

    private static bool IsContextOpposed(StructuralEvidencePacket evidence, PriceActionDirection direction)
    {
        if (evidence.Context.MarketRegime.Regime == MarketRegime.IlliquidUnsafe)
            return true;
        bool buy = direction == PriceActionDirection.Bullish;
        MarketRegime regime = evidence.Context.MarketRegime.Regime;
        return buy
            ? regime is MarketRegime.TrendingDown or MarketRegime.BreakoutExpansionDown
            : regime is MarketRegime.TrendingUp or MarketRegime.BreakoutExpansionUp;
    }

    private bool HasBollingerReEntry(StructuralEvidencePacket evidence, PriceActionDirection direction)
    {
        bool buy = direction == PriceActionDirection.Bullish;
        decimal close = evidence.Trigger.LatestCandle.Prices.Close;
        decimal high = evidence.Trigger.LatestCandle.Prices.High;
        decimal low = evidence.Trigger.LatestCandle.Prices.Low;
        decimal? lower = evidence.Trigger.Indicators.BollingerLower;
        decimal? upper = evidence.Trigger.Indicators.BollingerUpper;
        return buy
            ? lower is decimal lowerBand && low <= lowerBand && close > lowerBand
            : upper is decimal upperBand && high >= upperBand && close < upperBand;
    }

    private static decimal ContextQuality(StructuralEvidencePacket evidence, PriceActionDirection direction) =>
        direction == PriceActionDirection.Bullish ? evidence.ContextEvidence.BullishQuality : evidence.ContextEvidence.BearishQuality;

    private static decimal Distance(decimal price, SupplyDemandZone zone)
    {
        decimal lower = Math.Min(zone.ProximalPrice, zone.DistalPrice);
        decimal upper = Math.Max(zone.ProximalPrice, zone.DistalPrice);
        return price < lower ? lower - price : price > upper ? price - upper : 0m;
    }

    private static MandatoryGate Gate(
        string name,
        bool passed,
        decimal quality,
        string failure,
        bool limitsConfidenceFloor = true) =>
        new(name, passed, Math.Clamp(quality, 0m, 100m),
            passed ? $"{name}Passed" : failure, limitsConfidenceFloor);

    private static IReadOnlyList<string> BuildSupport(
        bool context,
        bool reaction,
        CciAssessment cci,
        RsiAssessment rsi,
        bool bollingerReEntry)
    {
        var result = new List<string>
        {
            context ? "StructuralTrendPullback" : "StructuralContextNeutral"
        };
        if (reaction) result.Add("StructuralZoneReaction");
        if (cci.Alignment == EvidenceAlignment.Aligned) result.Add(cci.State);
        if (rsi.Alignment == EvidenceAlignment.Aligned) result.Add($"StructuralRsi{rsi.State}");
        if (bollingerReEntry) result.Add("StructuralBollingerReEntry");
        return result.AsReadOnly();
    }

    private static IReadOnlyList<string> BuildConflicts(
        CciAssessment cci,
        RsiAssessment rsi,
        LocalTrendOppositionAssessment localTrendOpposition)
    {
        var result = new List<string>(5);
        if (cci.Alignment == EvidenceAlignment.Conflicting) result.Add("CciConflicting");
        if (rsi.Alignment == EvidenceAlignment.Conflicting) result.Add("RsiConflicting");
        if (localTrendOpposition.DmiOpposes) result.Add("DmiTrendOpposing");
        if (localTrendOpposition.BollingerExpansionOpposes) result.Add("BollingerExpansionOpposing");
        if (localTrendOpposition.IsCoherent) result.Add("CoherentLocalTrendOpposition");
        return result.AsReadOnly();
    }

    private static string FirstFailure(IReadOnlyList<MandatoryGate> gates, decimal confidence, decimal minimum) =>
        gates.FirstOrDefault(item => !item.Passed)?.ReasonCode ??
        (confidence < minimum ? "StructuralConfidenceBelowMinimum" : "StructuralCandidateNotReady");
}
