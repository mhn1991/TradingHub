using Agent.Models;
using Agent.Strategies.StructuralConfluence.Evidence;
using ChartAnnotator.Liquidity;
using ChartAnnotator.Models;
using ChartAnnotator.Regime;
using ChartAnnotator.SupplyDemand;
using ChartAnnotator.TargetManagement;

namespace Agent.Strategies.StructuralConfluence.Playbooks;

public sealed class LiquiditySweepReversalPlaybook : IStructuralPlaybook
{
    public const string StableId = "structural.liquidity-sweep-reversal";
    private readonly StructuralConfluenceStrategyOptions _root;
    private readonly LiquiditySweepReversalOptions _options;
    private readonly StructuralGeometryBuilder _geometry;

    public LiquiditySweepReversalPlaybook(StructuralConfluenceStrategyOptions options)
    {
        _root = options;
        _options = options.LiquiditySweepReversal;
        _geometry = new StructuralGeometryBuilder(options);
    }

    public string PlaybookId => StableId;
    public string Version => "1.0";

    public PlaybookEvaluation Evaluate(StructuralEvidencePacket evidence, PlaybookRuntimeState state)
    {
        Dictionary<Guid, LiquidityPool> pools = evidence.Liquidity.Pools.ToDictionary(item => item.PoolId);
        LiquiditySweepEvent? sweep = evidence.Liquidity.Sweeps
            .Where(item => pools.ContainsKey(item.PoolId))
            .OrderByDescending(item => item.AvailableAt)
            .ThenBy(item => item.SweepId)
            .FirstOrDefault();
        if (sweep is null)
            return Dormant("StructuralSweepUnavailable");

        LiquidityPool pool = pools[sweep.PoolId];
        PriceActionDirection direction = pool.Side == LiquiditySide.SellSide
            ? PriceActionDirection.Bullish
            : PriceActionDirection.Bearish;
        bool buy = direction == PriceActionDirection.Bullish;
        bool superseded = evidence.Liquidity.Events.Any(item => item.PoolId == pool.PoolId &&
            item.EventType == LiquidityEventType.AcceptedBreak && item.AvailableAt >= sweep.AvailableAt);
        SupplyDemandZoneType desiredZone = buy ? SupplyDemandZoneType.Demand : SupplyDemandZoneType.Supply;
        decimal atr = evidence.Indicators.Atr ?? evidence.Setup.Indicators.Atr ?? 0m;
        SupplyDemandZone? zone = atr > 0m
            ? evidence.SupplyDemand.Zones
                .Where(item => item.Type == desiredZone && IsUsable(item))
                .Select(item => new { Zone = item, Distance = Distance(pool.ReferencePrice, item) / atr })
                .Where(item => item.Distance <= _options.MaximumZonePoolDistanceAtr)
                .OrderBy(item => item.Distance).ThenByDescending(item => item.Zone.QualityScore)
                .ThenBy(item => item.Zone.ZoneId).Select(item => item.Zone).FirstOrDefault()
            : null;
        bool confluencePasses = _options.SupplyDemandConfluence != StructuralConfluenceRequirement.Required || zone is not null;
        (PriceActionEvent? triggerEvent, PriceActionSetup? triggerSetup, decimal triggerQuality) =
            StructuralPlaybookRules.Trigger(evidence, direction, _root.Trigger.MinimumPriceActionConfidence,
                _root.MaximumTriggerBars, sweep.AvailableAt);
        bool triggerPresent = triggerEvent is not null || triggerSetup is not null || !_options.RequirePriceActionTrigger;
        bool microBreak = !_options.RequireMicroStructureBreak ||
            evidence.Trigger.MarketStructure.Break == (buy ? MarketStructureBreak.Bullish : MarketStructureBreak.Bearish);
        CciAssessment cci = StructuralPlaybookRules.AssessCci(evidence, direction, continuation: false);
        bool cciPasses = StructuralPlaybookRules.ConfirmationPasses(_options.CciMode, cci);

        // Identity is tied to the sweep event itself (PoolId/ZoneId/SweepId/AvailableAt), not to
        // whether it already produced a trade - a later candle's confirmation trigger firing off
        // the SAME still-fresh sweep after an earlier attempt on it already closed would
        // otherwise re-arm and re-enter on a thesis the market just invalidated at that exact
        // level (observed 2026-07-20: same setupId, two entries 25 minutes apart, second one
        // stopped out in 1 minute). state.LastReadySetupId is sticky across non-ready frames and
        // frozen for the whole holding period (StructuralConfluenceAgent skips Evaluate entirely
        // while a position is open), so this only blocks a genuine repeat of the same identity,
        // never a setup that's simply still armed across consecutive frames pre-entry.
        string setupId = StructuralIdentity.Create(_root.StrategyVersion, evidence.Instrument, PlaybookId,
            pool.PoolId, zone?.ZoneId, sweep.SweepId.ToString("N"), sweep.AvailableAt, direction);
        bool notAlreadySignaled = setupId != state.LastReadySetupId;

        var gates = new List<MandatoryGate>
        {
            Gate("NotAlreadySignaled", notAlreadySignaled, sweep.QualityScore * 100m, "StructuralSweepAlreadySignaled"),
            Gate("PoolPreExisting", pool.AvailableAt <= sweep.SweepStartedAt, pool.QualityScore * 100m, "StructuralPoolNotPreExisting"),
            Gate("PoolQuality", pool.QualityScore >= _options.MinimumPoolQuality, pool.QualityScore * 100m, "StructuralPoolQualityLow"),
            Gate("PoolType", _options.AllowedPoolTypes.Contains(pool.Type), pool.QualityScore * 100m, "StructuralPoolTypeRejected"),
            Gate("ClosedBackInside", !_options.RequireClosedBackInside || sweep.ClosedBackInside, sweep.RejectionStrength * 100m, "StructuralSweepNotReclaimed"),
            Gate("ReclaimStrength", sweep.RejectionStrength >= _options.MinimumReclaimBodyRatio, sweep.RejectionStrength * 100m, "StructuralReclaimBodyTooWeak"),
            Gate("Penetration", sweep.PenetrationAtr >= _options.MinimumSweepPenetrationAtr && sweep.PenetrationAtr <= _options.MaximumSweepPenetrationAtr, sweep.QualityScore * 100m, "StructuralSweepPenetrationInvalid"),
            Gate("SweepFresh", StructuralPlaybookRules.IsFresh(sweep.AvailableAt, evidence.AvailableAt, evidence.Trigger.Interval, _options.MaximumBarsSinceSweep), sweep.QualityScore * 100m, "StructuralSweepExpired"),
            Gate("NotSuperseded", !superseded, sweep.QualityScore * 100m, "StructuralSweepSupersededByAcceptedBreak"),
            Gate("SupplyDemandConfluence", confluencePasses, zone?.QualityScore * 100m ?? 50m, "StructuralZoneConfluenceMissing"),
            Gate("Trigger", triggerPresent && microBreak, triggerPresent ? Math.Max(triggerQuality, 50m) : 0m, "StructuralTriggerMissing"),
            Gate("Cci", cciPasses, cci.Quality, "StructuralCciConfirmationMissing")
        };

        decimal rawStop = sweep.ExtremePrice;
        if (zone is not null)
            rawStop = buy ? Math.Min(rawStop, Math.Min(zone.ProximalPrice, zone.DistalPrice)) : Math.Max(rawStop, Math.Max(zone.ProximalPrice, zone.DistalPrice));
        string stopSource = zone is null ? $"Sweep:{sweep.SweepId:N}" : $"SweepAndZone:{sweep.SweepId:N}:{zone.ZoneId:N}";
        // Exit-policy routing (plan §4.1): a range/countertrend sweep takes a fixed target; a
        // sweep aligned with context after displacement and a confirmed structure shift is
        // treated as continuation and gets a partial-then-runner instead. A sweep never rejects
        // purely on context here - it is a reversal trade, being countertrend is expected.
        bool alignedContinuation = _root.AdaptiveTargetManagement.Enabled &&
            IsContextAligned(evidence, direction) && sweep.DisplacementConfirmed && sweep.StructureShiftConfirmed;
        StructuralGeometry geometry = _root.AdaptiveTargetManagement.Enabled
            ? _geometry.BuildAdaptive(evidence, direction, rawStop, stopSource,
                alignedContinuation ? TradeExitPolicy.PartialThenRunner : TradeExitPolicy.FixedStructuralTarget,
                [$"LiquidityPool:{pool.PoolId:N}"])
            : _geometry.Build(evidence, direction, rawStop, stopSource);
        gates.Add(Gate("Geometry", geometry.IsValid, geometry.Quality, geometry.ReasonCode));

        bool structuralPassed = gates.Where(item => item.Name is not ("Trigger" or "Cci" or "Geometry")).All(item => item.Passed);
        bool ready = gates.All(item => item.Passed);
        decimal contextQuality = DirectionContextQuality(evidence, direction);
        decimal confirmationAdjustment = StructuralPlaybookRules.ConfirmationAdjustment(_options.CciMode, cci, _root.Confirmation);
        decimal confluenceAdjustment = zone is null ? 0m : Math.Min(8m, zone.QualityScore * 8m);
        decimal confidence = StructuralPlaybookRules.Confidence(gates, (contextQuality - 50m) / 6.25m,
            confirmationAdjustment, confluenceAdjustment);
        ready &= confidence >= _options.MinimumConfidence;
        StructuralSetupLifecycle lifecycle = ready
            ? StructuralSetupLifecycle.CandidateProduced
            : structuralPassed ? StructuralSetupLifecycle.AwaitingTrigger :
                gates.Any(item => item.ReasonCode == "StructuralSweepExpired" && !item.Passed)
                    ? StructuralSetupLifecycle.Expired : StructuralSetupLifecycle.Invalidated;

        return new PlaybookEvaluation
        {
            PlaybookId = PlaybookId,
            Version = Version,
            Direction = direction,
            Lifecycle = lifecycle,
            SetupId = setupId,
            PrimaryPoolId = pool.PoolId,
            PrimarySweepId = sweep.SweepId,
            PrimaryZoneId = zone?.ZoneId,
            CatalystAt = sweep.AvailableAt,
            MandatoryGates = gates.AsReadOnly(),
            SupportingEvidence = BuildSupport(pool, sweep, zone, cci),
            ConflictingEvidence = cci.Alignment == EvidenceAlignment.Conflicting ? ["CciConflicting"] : [],
            ConfidenceContributions =
            [
                new("Context", Math.Clamp((contextQuality - 50m) / 6.25m, -8m, 8m), "StructuralContextAdjustment"),
                new("Cci", confirmationAdjustment, cci.State),
                new("ZoneConfluence", confluenceAdjustment, zone is null ? "StructuralZoneNeutral" : "StructuralZoneAligned")
            ],
            ContextQuality = contextQuality,
            LocationQuality = pool.QualityScore * 100m,
            CatalystQuality = sweep.QualityScore * 100m,
            TriggerQuality = triggerPresent ? Math.Max(triggerQuality, 50m) : 0m,
            ConfirmationQuality = cci.Quality,
            GeometryQuality = geometry.Quality,
            Confidence = confidence,
            ExpiresAt = sweep.AvailableAt + StructuralPlaybookRules.Bars(evidence.Trigger.Interval, _options.MaximumBarsSinceSweep),
            ReasonCode = ready ? "StructuralSweepReversalCandidate" : FirstFailure(gates, confidence, _options.MinimumConfidence),
            IsReady = ready,
            Geometry = geometry,
            Pool = pool,
            Sweep = sweep,
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

    private static bool IsUsable(SupplyDemandZone zone) => zone.State is not
        (SupplyDemandZoneState.Invalidated or SupplyDemandZoneState.Mitigated or SupplyDemandZoneState.Expired or SupplyDemandZoneState.Merged);

    private static decimal Distance(decimal price, SupplyDemandZone zone)
    {
        decimal lower = Math.Min(zone.ProximalPrice, zone.DistalPrice);
        decimal upper = Math.Max(zone.ProximalPrice, zone.DistalPrice);
        return price < lower ? lower - price : price > upper ? price - upper : 0m;
    }

    private static MandatoryGate Gate(string name, bool passed, decimal quality, string failure) =>
        new(name, passed, Math.Clamp(quality, 0m, 100m), passed ? $"{name}Passed" : failure);

    private static decimal DirectionContextQuality(StructuralEvidencePacket evidence, PriceActionDirection direction) =>
        direction == PriceActionDirection.Bullish ? evidence.ContextEvidence.BullishQuality : evidence.ContextEvidence.BearishQuality;

    private static IReadOnlyList<string> BuildSupport(
        LiquidityPool pool,
        LiquiditySweepEvent sweep,
        SupplyDemandZone? zone,
        CciAssessment cci)
    {
        var reasons = new List<string> { $"LiquidityPool:{pool.Type}", "LiquiditySweepReclaimed", cci.State };
        if (zone is not null)
            reasons.Add($"SupplyDemandConfluence:{zone.Type}");
        if (sweep.StructureShiftConfirmed)
            reasons.Add("SweepStructureShift");
        return reasons.AsReadOnly();
    }

    private static string FirstFailure(IReadOnlyList<MandatoryGate> gates, decimal confidence, decimal minimum) =>
        gates.FirstOrDefault(item => !item.Passed)?.ReasonCode ??
        (confidence < minimum ? "StructuralConfidenceBelowMinimum" : "StructuralCandidateNotReady");
}
