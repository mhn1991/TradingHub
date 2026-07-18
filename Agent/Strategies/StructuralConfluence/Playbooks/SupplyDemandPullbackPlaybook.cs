using Agent.Models;
using Agent.Strategies.StructuralConfluence.Evidence;
using ChartAnnotator.Models;
using ChartAnnotator.Regime;
using ChartAnnotator.SupplyDemand;

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
    public string Version => "1.0";

    public PlaybookEvaluation Evaluate(StructuralEvidencePacket evidence, PlaybookRuntimeState state)
    {
        decimal price = evidence.Trigger.LatestCandle.Prices.Close;
        SupplyDemandZone? zone = evidence.SupplyDemand.Zones
            .Where(item => _options.PermittedZoneStates.Contains(item.State))
            .OrderBy(item => Distance(price, item))
            .ThenByDescending(item => item.QualityScore)
            .ThenByDescending(item => item.AvailableAt)
            .ThenBy(item => item.ZoneId)
            .FirstOrDefault();
        if (zone is null)
            return Dormant("StructuralZoneUnavailable");

        PriceActionDirection direction = zone.Type == SupplyDemandZoneType.Demand
            ? PriceActionDirection.Bullish
            : PriceActionDirection.Bearish;
        bool buy = direction == PriceActionDirection.Bullish;
        SupplyDemandZoneEvent? reaction = evidence.SupplyDemand.Events
            .Where(item => item.ZoneId == zone.ZoneId && item.EventType is SupplyDemandZoneEventType.Approached or SupplyDemandZoneEventType.Touched or SupplyDemandZoneEventType.PartiallyMitigated)
            .OrderByDescending(item => item.AvailableAt).ThenBy(item => item.EventId).FirstOrDefault();
        bool priceAtZone = Distance(price, zone) == 0m;
        bool invalidated = evidence.SupplyDemand.Events.Any(item => item.ZoneId == zone.ZoneId &&
            (item.EventType is SupplyDemandZoneEventType.Invalidated or SupplyDemandZoneEventType.Mitigated) &&
            item.AvailableAt <= evidence.AvailableAt);
        bool contextAligned = IsContextAligned(evidence, direction);
        bool contextAllowed = !_options.RequireTrendAlignment || contextAligned ||
            _options.AllowRangeBoundaryContext && evidence.Context.MarketRegime.Regime == MarketRegime.Range;
        DateTimeOffset catalystAt = reaction?.AvailableAt ?? zone.AvailableAt;
        (PriceActionEvent? triggerEvent, PriceActionSetup? triggerSetup, decimal triggerQuality) =
            StructuralPlaybookRules.Trigger(evidence, direction, _root.Trigger.MinimumPriceActionConfidence,
                _root.MaximumTriggerBars, catalystAt);
        bool triggerPresent = triggerEvent is not null || triggerSetup is not null || !_options.RequirePriceActionTrigger;
        CciAssessment cci = StructuralPlaybookRules.AssessCci(evidence, direction, continuation: true);
        bool alternativeConfirmation = HasAlternativeConfirmation(evidence, direction);
        bool confirmationPasses = _options.CciMode != StructuralConfirmationMode.Required ||
            cci.Alignment == EvidenceAlignment.Aligned || alternativeConfirmation;
        decimal locationQuality = Math.Clamp(Math.Min(zone.QualityScore, zone.FreshnessScore) * 100m, 0m, 100m);

        var gates = new List<MandatoryGate>
        {
            Gate("Context", contextAllowed, ContextQuality(evidence, direction), "StructuralContextOpposed"),
            Gate("ZoneQuality", zone.QualityScore >= _options.MinimumZoneQuality, locationQuality, "StructuralZoneQualityLow"),
            Gate("ZoneState", _options.PermittedZoneStates.Contains(zone.State), locationQuality, "StructuralZoneStateRejected"),
            Gate("ZoneTouches", zone.TouchCount <= _options.MaximumPriorTouches, locationQuality, "StructuralZoneTouchLimitExceeded"),
            Gate("ZonePenetration", zone.PenetrationRatio <= _options.MaximumPenetrationRatio, locationQuality, "StructuralZonePenetrationExceeded"),
            Gate("ZoneValid", !invalidated, locationQuality, "StructuralZoneInvalidated"),
            Gate("ZoneReaction", reaction is not null || priceAtZone, reaction is null ? 50m : locationQuality, "StructuralZoneReactionMissing"),
            Gate("Trigger", triggerPresent, triggerPresent ? Math.Max(triggerQuality, 50m) : 0m, "StructuralTriggerMissing"),
            Gate("Confirmation", confirmationPasses, Math.Max(cci.Quality, alternativeConfirmation ? 55m : 0m), "StructuralConfirmationMissing")
        };

        decimal rawStop = buy ? Math.Min(zone.ProximalPrice, zone.DistalPrice) : Math.Max(zone.ProximalPrice, zone.DistalPrice);
        StructuralGeometry geometry = _geometry.Build(evidence, direction, rawStop, $"SupplyDemandZone:{zone.ZoneId:N}");
        gates.Add(Gate("Geometry", geometry.IsValid, geometry.Quality, geometry.ReasonCode));

        string catalystIdentity = reaction?.EventId.ToString("N") ?? $"zone-{zone.ZoneId:N}";
        string setupId = StructuralIdentity.Create(_root.StrategyVersion, evidence.Instrument, PlaybookId,
            null, zone.ZoneId, catalystIdentity, catalystAt, direction);
        decimal contextQuality = ContextQuality(evidence, direction);
        decimal confirmationAdjustment = StructuralPlaybookRules.ConfirmationAdjustment(_options.CciMode, cci, _root.Confirmation);
        if (alternativeConfirmation && cci.Alignment != EvidenceAlignment.Conflicting)
            confirmationAdjustment = Math.Max(confirmationAdjustment, 3m);
        decimal confidence = StructuralPlaybookRules.Confidence(gates, (contextQuality - 50m) / 6.25m,
            confirmationAdjustment, 0m);
        bool ready = gates.All(item => item.Passed) && confidence >= _options.MinimumConfidence;
        bool locationPassed = gates.Take(7).All(item => item.Passed);

        return new PlaybookEvaluation
        {
            PlaybookId = PlaybookId,
            Version = Version,
            Direction = direction,
            Lifecycle = ready ? StructuralSetupLifecycle.CandidateProduced :
                invalidated ? StructuralSetupLifecycle.Invalidated :
                locationPassed ? StructuralSetupLifecycle.AwaitingTrigger : StructuralSetupLifecycle.Armed,
            SetupId = setupId,
            PrimaryZoneId = zone.ZoneId,
            CatalystAt = catalystAt,
            MandatoryGates = gates.AsReadOnly(),
            SupportingEvidence = BuildSupport(contextAligned, reaction is not null || priceAtZone, cci, alternativeConfirmation),
            ConflictingEvidence = cci.Alignment == EvidenceAlignment.Conflicting ? ["CciConflicting"] : [],
            ConfidenceContributions =
            [
                new("Context", Math.Clamp((contextQuality - 50m) / 6.25m, -8m, 8m), contextAligned ? "StructuralContextAligned" : "StructuralContextNeutral"),
                new("Confirmation", confirmationAdjustment, cci.State)
            ],
            ContextQuality = contextQuality,
            LocationQuality = locationQuality,
            CatalystQuality = reaction is null ? 50m : Math.Clamp(100m - zone.PenetrationRatio * 50m, 0m, 100m),
            TriggerQuality = triggerPresent ? Math.Max(triggerQuality, 50m) : 0m,
            ConfirmationQuality = Math.Max(cci.Quality, alternativeConfirmation ? 55m : 0m),
            GeometryQuality = geometry.Quality,
            Confidence = confidence,
            ExpiresAt = catalystAt + StructuralPlaybookRules.Bars(evidence.Trigger.Interval, _root.MaximumArmedSetupBars),
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
        bool buy = direction == PriceActionDirection.Bullish;
        MarketRegime regime = evidence.Context.MarketRegime.Regime;
        return buy
            ? regime is MarketRegime.TrendingUp or MarketRegime.BreakoutExpansionUp || evidence.Context.MarketStructure.Direction == MarketStructureDirection.Rising
            : regime is MarketRegime.TrendingDown or MarketRegime.BreakoutExpansionDown || evidence.Context.MarketStructure.Direction == MarketStructureDirection.Falling;
    }

    private bool HasAlternativeConfirmation(StructuralEvidencePacket evidence, PriceActionDirection direction)
    {
        bool buy = direction == PriceActionDirection.Bullish;
        bool rsi = _options.AllowRsiAlternativeConfirmation && evidence.Indicators.Rsi.HasValue &&
            (buy ? evidence.Indicators.RsiAnalysis.MomentumDirection == MomentumDirection.Rising : evidence.Indicators.RsiAnalysis.MomentumDirection == MomentumDirection.Falling);
        bool bollinger = _options.AllowBollingerReEntryConfirmation &&
            evidence.Trigger.Indicators.BollingerMiddle.HasValue &&
            evidence.Trigger.Indicators.BollingerLower.HasValue &&
            evidence.Trigger.Indicators.BollingerUpper.HasValue &&
            (buy ? evidence.Trigger.LatestCandle.Prices.Close >= evidence.Trigger.Indicators.BollingerLower.Value :
                evidence.Trigger.LatestCandle.Prices.Close <= evidence.Trigger.Indicators.BollingerUpper.Value);
        return rsi || bollinger;
    }

    private static decimal ContextQuality(StructuralEvidencePacket evidence, PriceActionDirection direction) =>
        direction == PriceActionDirection.Bullish ? evidence.ContextEvidence.BullishQuality : evidence.ContextEvidence.BearishQuality;

    private static decimal Distance(decimal price, SupplyDemandZone zone)
    {
        decimal lower = Math.Min(zone.ProximalPrice, zone.DistalPrice);
        decimal upper = Math.Max(zone.ProximalPrice, zone.DistalPrice);
        return price < lower ? lower - price : price > upper ? price - upper : 0m;
    }

    private static MandatoryGate Gate(string name, bool passed, decimal quality, string failure) =>
        new(name, passed, Math.Clamp(quality, 0m, 100m), passed ? $"{name}Passed" : failure);

    private static IReadOnlyList<string> BuildSupport(bool context, bool reaction, CciAssessment cci, bool alternative)
    {
        var result = new List<string> { context ? "StructuralContextAligned" : "StructuralContextNeutral", cci.State };
        if (reaction) result.Add("StructuralZoneReaction");
        if (alternative) result.Add("AlternativeIndicatorConfirmation");
        return result.AsReadOnly();
    }

    private static string FirstFailure(IReadOnlyList<MandatoryGate> gates, decimal confidence, decimal minimum) =>
        gates.FirstOrDefault(item => !item.Passed)?.ReasonCode ??
        (confidence < minimum ? "StructuralConfidenceBelowMinimum" : "StructuralCandidateNotReady");
}
