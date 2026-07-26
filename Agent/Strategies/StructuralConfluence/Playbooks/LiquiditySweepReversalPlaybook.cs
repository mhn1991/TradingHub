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
    public string Version => "1.8";

    public PlaybookEvaluation Evaluate(StructuralEvidencePacket evidence, PlaybookRuntimeState state)
    {
        Dictionary<Guid, LiquidityPool> pools = evidence.Liquidity.Pools.ToDictionary(item => item.PoolId);
        // One candidate per distinct pool (its own most recent sweep), not just the single most
        // recent sweep system-wide - see LiquidityBreakRetestPlaybook's identical fix for the
        // starvation bug this prevents: a sweep awaiting its confirmation trigger must not get
        // dropped just because a different, unrelated pool swept more recently this same bar.
        LiquiditySweepEvent[] candidates = evidence.Liquidity.Sweeps
            .Where(item => pools.ContainsKey(item.PoolId))
            .GroupBy(item => item.PoolId)
            .Select(group => group.OrderByDescending(item => item.AvailableAt).ThenBy(item => item.SweepId).First())
            .OrderByDescending(item => item.AvailableAt).ThenBy(item => item.SweepId)
            .ToArray();
        if (candidates.Length == 0)
            return ArmedPool(evidence) ?? Dormant("StructuralSweepUnavailable");

        var evaluations = new List<PlaybookEvaluation>(candidates.Length);
        foreach (LiquiditySweepEvent candidate in candidates)
        {
            PlaybookEvaluation evaluation = EvaluateCandidate(candidate, pools[candidate.PoolId], evidence, state);
            evaluations.Add(evaluation);
        }

        // Keep diagnostics/runtime state attached to the most-progressed viable hypothesis.
        // A merely fresher sweep must not hide one that is already awaiting its reversal trigger.
        return StructuralPlaybookRules.SelectRepresentativeCandidate(evaluations);
    }

    private PlaybookEvaluation EvaluateCandidate(
        LiquiditySweepEvent sweep, LiquidityPool pool, StructuralEvidencePacket evidence, PlaybookRuntimeState state)
    {
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
        int triggerResponseBars = StructuralPlaybookRules.TriggerResponseBars(
            _root.MaximumTriggerBars, _root.MaximumArmedSetupBars);
        DateTimeOffset sweepExpiresAt = sweep.AvailableAt +
            StructuralPlaybookRules.Bars(
                evidence.Setup.Interval, _options.MaximumBarsSinceSweep);
        DateTimeOffset triggerExpiresAt =
            StructuralPlaybookRules.TriggerWindowExpiresAt(
                sweep.AvailableAt,
                evidence.Trigger.Interval,
                _root.MaximumTriggerBars,
                _root.MaximumArmedSetupBars);
        bool triggerWindowExpired = evidence.AvailableAt > triggerExpiresAt;
        (PriceActionEvent? triggerEvent, PriceActionSetup? triggerSetup, decimal triggerQuality) =
            StructuralPlaybookRules.Trigger(evidence, direction, StructuralTriggerProfile.SweepReversal,
                _root.Trigger.MinimumPriceActionConfidence, triggerResponseBars, sweep.AvailableAt,
                new StructuralTriggerAnchor(pool.LowerPrice, pool.UpperPrice));
        bool triggerPresent = triggerEvent is not null || triggerSetup is not null || !_options.RequirePriceActionTrigger;
        bool triggerShowsShift = triggerEvent?.Type is
                PriceActionEventType.BullishChangeOfCharacter or
                PriceActionEventType.BearishChangeOfCharacter ||
            triggerSetup?.Type is
                PriceActionSetupType.BullishSweepChoCh or
                PriceActionSetupType.BearishSweepChoCh or
                PriceActionSetupType.BullishChoChRetestHold or
                PriceActionSetupType.BearishChoChRetestHold;
        bool reversalShift = sweep.StructureShiftConfirmed || triggerShowsShift;
        bool microBreak = !_options.RequireMicroStructureBreak ||
            evidence.Trigger.MarketStructure.Break == (buy ? MarketStructureBreak.Bullish : MarketStructureBreak.Bearish);
        CciAssessment cci = StructuralPlaybookRules.AssessCci(evidence, direction, continuation: false);
        bool cciPasses = StructuralPlaybookRules.ConfirmationPasses(_options.CciMode, cci);

        // Keep one identity from the visible pool through sweep, structure shift and trigger. A
        // later confirmation therefore advances the existing hypothesis instead of replacing it.
        string setupId = StructuralIdentity.Create(_root.StrategyVersion, evidence.Instrument, PlaybookId,
            pool.PoolId, null, $"pool-{pool.PoolId:N}", pool.AvailableAt, direction);
        bool notAlreadySignaled = setupId != state.LastReadySetupId;

        var gates = new List<MandatoryGate>
        {
            Gate("NotAlreadySignaled", notAlreadySignaled, sweep.QualityScore * 100m, "StructuralSweepAlreadySignaled"),
            Gate("PoolPreExisting", pool.AvailableAt <= sweep.SweepStartedAt, pool.QualityScore * 100m, "StructuralPoolNotPreExisting"),
            Gate("PoolQuality", pool.QualityScore >= _options.MinimumPoolQuality, pool.QualityScore * 100m, "StructuralPoolQualityLow"),
            Gate("PoolType", _options.AllowedPoolTypes.Contains(pool.Type), pool.QualityScore * 100m, "StructuralPoolTypeRejected"),
            Gate("ClosedBackInside", !_options.RequireClosedBackInside || sweep.ClosedBackInside,
                sweep.RejectionStrength * 100m, "StructuralSweepNotReclaimed",
                _options.RequireClosedBackInside),
            Gate("ReclaimStrength", sweep.RejectionStrength >= _options.MinimumReclaimBodyRatio, sweep.RejectionStrength * 100m, "StructuralReclaimBodyTooWeak"),
            Gate("Penetration", sweep.PenetrationAtr >= _options.MinimumSweepPenetrationAtr && sweep.PenetrationAtr <= _options.MaximumSweepPenetrationAtr, sweep.QualityScore * 100m, "StructuralSweepPenetrationInvalid"),
            Gate("SweepFresh", StructuralPlaybookRules.IsFresh(sweep.AvailableAt, evidence.AvailableAt, evidence.Setup.Interval, _options.MaximumBarsSinceSweep), sweep.QualityScore * 100m, "StructuralSweepExpired"),
            Gate("NotSuperseded", !superseded, sweep.QualityScore * 100m, "StructuralSweepSupersededByAcceptedBreak"),
            Gate("SupplyDemandConfluence", confluencePasses, zone?.QualityScore * 100m ?? 50m,
                "StructuralZoneConfluenceMissing",
                _options.SupplyDemandConfluence == StructuralConfluenceRequirement.Required),
            Gate("TriggerWindow", !triggerWindowExpired, triggerWindowExpired ? 0m : 60m,
                "StructuralTriggerWindowExpired"),
            Gate("StructureShift", reversalShift, reversalShift ? Math.Max(triggerQuality, 65m) : 0m, "StructuralReversalShiftMissing"),
            Gate("Trigger", triggerPresent && microBreak,
                triggerPresent ? Math.Max(triggerQuality, 50m) : 0m,
                "StructuralTriggerMissing", _options.RequirePriceActionTrigger),
            Gate("Cci", cciPasses, cci.Quality, "StructuralCciConfirmationMissing",
                _options.CciMode == StructuralConfirmationMode.Required)
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
            : _geometry.BuildTieredFixed(evidence, direction, rawStop, stopSource,
                [$"LiquidityPool:{pool.PoolId:N}"]);
        gates.Add(Gate("Geometry", geometry.IsValid, geometry.Quality, geometry.ReasonCode));

        bool ready = gates.All(item => item.Passed);
        decimal contextQuality = DirectionContextQuality(evidence, direction);
        decimal confirmationAdjustment = StructuralPlaybookRules.ConfirmationAdjustment(_options.CciMode, cci, _root.Confirmation);
        decimal confluenceAdjustment = zone is null ? 0m : Math.Min(8m, zone.QualityScore * 8m);
        decimal confidence = StructuralPlaybookRules.Confidence(gates, (contextQuality - 50m) / 6.25m,
            confirmationAdjustment, confluenceAdjustment);
        ready &= confidence >= _options.MinimumConfidence;
        bool coreValid = gates.Where(item => item.Name is not
            ("TriggerWindow" or "StructureShift" or "Trigger" or "Cci" or "Geometry"))
            .All(item => item.Passed);
        StructuralSetupLifecycle lifecycle = ready
            ? StructuralSetupLifecycle.CandidateProduced
            : gates.Any(item => item.ReasonCode == "StructuralSweepExpired" && !item.Passed)
                ? StructuralSetupLifecycle.Expired :
                triggerWindowExpired ? StructuralSetupLifecycle.Expired :
                !coreValid ? StructuralSetupLifecycle.Invalidated :
                !reversalShift ? StructuralSetupLifecycle.CatalystObserved :
                StructuralSetupLifecycle.AwaitingTrigger;

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
            ExpiresAt = StructuralPlaybookRules.Earlier(
                sweepExpiresAt, triggerExpiresAt),
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

    private PlaybookEvaluation? ArmedPool(StructuralEvidencePacket evidence)
    {
        LiquidityPool? pool = evidence.Liquidity.Pools
            .Where(item => item.State is LiquidityPoolState.Active or
                LiquidityPoolState.Approached or LiquidityPoolState.Touched)
            .Where(item => item.QualityScore >= _options.MinimumPoolQuality &&
                _options.AllowedPoolTypes.Contains(item.Type))
            .OrderByDescending(item => item.QualityScore)
            .ThenBy(item => item.PoolId)
            .FirstOrDefault();
        if (pool is null)
            return null;

        PriceActionDirection direction = pool.Side == LiquiditySide.SellSide
            ? PriceActionDirection.Bullish
            : PriceActionDirection.Bearish;
        string setupId = StructuralIdentity.Create(_root.StrategyVersion, evidence.Instrument, PlaybookId,
            pool.PoolId, null, $"pool-{pool.PoolId:N}", pool.AvailableAt, direction);
        return new PlaybookEvaluation
        {
            PlaybookId = PlaybookId,
            Version = Version,
            Direction = direction,
            Lifecycle = StructuralSetupLifecycle.Armed,
            SetupId = setupId,
            PrimaryPoolId = pool.PoolId,
            LocationQuality = pool.QualityScore * 100m,
            ReasonCode = "StructuralAwaitingLiquiditySweep",
            Pool = pool
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

    private static MandatoryGate Gate(
        string name,
        bool passed,
        decimal quality,
        string failure,
        bool limitsConfidenceFloor = true) =>
        new(name, passed, Math.Clamp(quality, 0m, 100m),
            passed ? $"{name}Passed" : failure, limitsConfidenceFloor);

    private static decimal DirectionContextQuality(StructuralEvidencePacket evidence, PriceActionDirection direction) =>
        direction == PriceActionDirection.Bullish ? evidence.ContextEvidence.BullishQuality : evidence.ContextEvidence.BearishQuality;

    private static IReadOnlyList<string> BuildSupport(
        LiquidityPool pool,
        LiquiditySweepEvent sweep,
        SupplyDemandZone? zone,
        CciAssessment cci)
    {
        var reasons = new List<string> { $"LiquidityPool:{pool.Type}", "LiquiditySweepReclaimed" };
        if (zone is not null)
            reasons.Add($"SupplyDemandConfluence:{zone.Type}");
        if (sweep.StructureShiftConfirmed)
            reasons.Add("SweepStructureShift");
        if (cci.Alignment == EvidenceAlignment.Aligned)
            reasons.Add(cci.State);
        return reasons.AsReadOnly();
    }

    private static string FirstFailure(IReadOnlyList<MandatoryGate> gates, decimal confidence, decimal minimum) =>
        gates.FirstOrDefault(item => !item.Passed)?.ReasonCode ??
        (confidence < minimum ? "StructuralConfidenceBelowMinimum" : "StructuralCandidateNotReady");
}
