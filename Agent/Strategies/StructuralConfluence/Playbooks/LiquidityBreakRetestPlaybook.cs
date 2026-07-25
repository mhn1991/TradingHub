using Agent.Models;
using Agent.Strategies.StructuralConfluence.Evidence;
using ChartAnnotator.Liquidity;
using ChartAnnotator.Models;
using ChartAnnotator.Regime;
using ChartAnnotator.TargetManagement;

namespace Agent.Strategies.StructuralConfluence.Playbooks;

public sealed class LiquidityBreakRetestPlaybook : IStructuralPlaybook
{
    public const string StableId = "structural.liquidity-break-retest";
    private readonly StructuralConfluenceStrategyOptions _root;
    private readonly LiquidityBreakRetestOptions _options;
    private readonly StructuralGeometryBuilder _geometry;

    public LiquidityBreakRetestPlaybook(StructuralConfluenceStrategyOptions options)
    {
        _root = options;
        _options = options.LiquidityBreakRetest;
        _geometry = new StructuralGeometryBuilder(options);
    }

    public string PlaybookId => StableId;
    public string Version => "1.0";

    public PlaybookEvaluation Evaluate(StructuralEvidencePacket evidence, PlaybookRuntimeState state)
    {
        Dictionary<Guid, LiquidityPool> pools = evidence.Liquidity.Pools.ToDictionary(item => item.PoolId);
        // One candidate per distinct pool (its own most recent accepted break), not just the single
        // most recent accepted break system-wide - a pool mid-retest must not get starved out of
        // consideration just because some other, unrelated pool broke more recently this same bar.
        // Confirmed via replay against real candles: with the old single-candidate pick, 1,942 of
        // 1,946 distinct accepted-break setups over a 2.5-month window never got a chance to retest
        // (94.6% displaced within 1 bar / 30 minutes), while only 8 (0.4%) ever ran out the
        // freshness window - the bottleneck was never the window, it was only ever looking at one
        // candidate at a time.
        LiquidityEvent[] candidates = evidence.Liquidity.Events
            .Where(item => item.EventType == LiquidityEventType.AcceptedBreak && pools.ContainsKey(item.PoolId))
            .GroupBy(item => item.PoolId)
            .Select(group => group.OrderByDescending(item => item.AvailableAt).ThenBy(item => item.EventId).First())
            .OrderByDescending(item => item.AvailableAt).ThenBy(item => item.EventId)
            .ToArray();
        if (candidates.Length == 0)
            return Dormant("StructuralAcceptedBreakUnavailable");

        PlaybookEvaluation? best = null;
        foreach (LiquidityEvent candidate in candidates)
        {
            PlaybookEvaluation evaluation = EvaluateCandidate(candidate, pools[candidate.PoolId], evidence, state);
            if (evaluation.IsReady && (best is null || evaluation.Confidence > best.Confidence))
                best = evaluation;
        }

        // Nothing ready this bar - report on the freshest candidate, matching the previous
        // single-candidate telemetry/lifecycle for the common (non-ready) case.
        return best ?? EvaluateCandidate(candidates[0], pools[candidates[0].PoolId], evidence, state);
    }

    private PlaybookEvaluation EvaluateCandidate(
        LiquidityEvent acceptedBreak, LiquidityPool pool, StructuralEvidencePacket evidence, PlaybookRuntimeState state)
    {
        PriceActionDirection direction = pool.Side == LiquiditySide.BuySide
            ? PriceActionDirection.Bullish
            : PriceActionDirection.Bearish;
        bool buy = direction == PriceActionDirection.Bullish;
        decimal atr = evidence.Indicators.Atr ?? evidence.Setup.Indicators.Atr ?? 0m;
        LiquidityEvent? retest = evidence.Liquidity.Events
            .Where(item => item.PoolId == pool.PoolId && item.EventType == LiquidityEventType.Retest &&
                item.AvailableAt >= acceptedBreak.AvailableAt)
            .OrderByDescending(item => item.AvailableAt).ThenBy(item => item.EventId).FirstOrDefault();
        bool failure = evidence.Liquidity.Events.Any(item => item.PoolId == pool.PoolId &&
            item.EventType == LiquidityEventType.Failure && item.AvailableAt >= acceptedBreak.AvailableAt);
        decimal displacementAtr = atr > 0m ? Math.Abs(acceptedBreak.Price - pool.ReferencePrice) / atr : 0m;
        decimal retestDistanceAtr = retest is not null && atr > 0m ? Math.Abs(retest.Price - pool.ReferencePrice) / atr : decimal.MaxValue;
        decimal currentPrice = evidence.Trigger.LatestCandle.Prices.Close;
        bool acceptedSide = buy ? currentPrice >= pool.LowerPrice : currentPrice <= pool.UpperPrice;
        DateTimeOffset catalystAt = retest?.AvailableAt ?? acceptedBreak.AvailableAt;
        (PriceActionEvent? triggerEvent, PriceActionSetup? triggerSetup, decimal triggerQuality) =
            StructuralPlaybookRules.Trigger(evidence, direction, _root.Trigger.MinimumPriceActionConfidence,
                _root.MaximumTriggerBars, catalystAt);
        bool triggerPresent = triggerEvent is not null || triggerSetup is not null || !_options.RequirePriceActionTrigger;
        CciAssessment cci = StructuralPlaybookRules.AssessCci(evidence, direction, continuation: true);
        bool cciPasses = StructuralPlaybookRules.ConfirmationPasses(_options.CciMode, cci);
        bool expansion = ExpansionAligned(evidence, direction);
        bool expansionPasses = _options.ExpansionMode != StructuralConfirmationMode.Required || expansion;

        // Identity is tied to the accepted-break event, not to whether it already produced a
        // trade - see LiquiditySweepReversalPlaybook's identical guard for the observed bug this
        // prevents (same identity re-arming and re-entering immediately after a prior attempt on
        // it already closed). state.LastReadySetupId is sticky across non-ready frames and frozen
        // for the whole holding period (StructuralConfluenceAgent skips Evaluate entirely while a
        // position is open), so this only blocks a genuine repeat, never a setup that's simply
        // still armed across consecutive pre-entry frames.
        string setupId = StructuralIdentity.Create(_root.StrategyVersion, evidence.Instrument, PlaybookId,
            pool.PoolId, null, acceptedBreak.EventId.ToString("N"), acceptedBreak.AvailableAt, direction);
        bool notAlreadySignaled = setupId != state.LastReadySetupId;

        var gates = new List<MandatoryGate>
        {
            Gate("NotAlreadySignaled", notAlreadySignaled, pool.QualityScore * 100m, "StructuralPoolAlreadySignaled"),
            Gate("PoolPreExisting", pool.AvailableAt <= acceptedBreak.OccurredAt, pool.QualityScore * 100m, "StructuralPoolNotPreExisting"),
            Gate("PoolQuality",
                pool.QualityScore >= _options.MinimumPoolQuality ||
                pool.DistinctTouchCount >= _options.MinimumDistinctTouchesForQualityExemption,
                pool.QualityScore * 100m, "StructuralPoolQualityLow"),
            // Timeframe-unit fix: the AcceptedBreak/Retest events being checked here come from the
            // setup-interval LiquidityAnalyzer (see StructuralEvidencePacketFactory), not the
            // trigger interval - measuring their freshness against Trigger.Interval understated the
            // real window by 6x (5m trigger vs 30m setup) and expired every candidate long before a
            // retest could realistically occur. Confirmed via a live reason-code funnel.
            Gate("AcceptedBreakFresh", StructuralPlaybookRules.IsFresh(acceptedBreak.AvailableAt, evidence.AvailableAt, evidence.Setup.Interval, _options.MaximumBarsSinceAcceptedBreak), pool.QualityScore * 100m, "StructuralAcceptedBreakExpired"),
            Gate("Displacement", displacementAtr >= _options.MinimumDisplacementAtr, Math.Clamp(displacementAtr / Math.Max(_options.MinimumDisplacementAtr, 0.01m) * 60m, 0m, 100m), "StructuralBreakDisplacementLow"),
            Gate("NoFailure", !failure, pool.QualityScore * 100m, "StructuralAcceptedBreakFailed"),
            Gate("Retest", !_options.RequireRetestEvent || retest is not null, retest is null ? 0m : 65m, "StructuralRetestMissing"),
            Gate("RetestDistance", retest is null || retestDistanceAtr <= _options.MaximumRetestDistanceAtr, retest is null ? 0m : Math.Clamp(100m - retestDistanceAtr * 100m, 0m, 100m), "StructuralRetestTooFar"),
            Gate("AcceptedSide", acceptedSide, 60m, "StructuralRetestDidNotHold"),
            Gate("Trigger", triggerPresent, triggerPresent ? Math.Max(triggerQuality, 50m) : 0m, "StructuralTriggerMissing"),
            Gate("Cci", cciPasses, cci.Quality, "StructuralCciConfirmationMissing"),
            Gate("Expansion", expansionPasses, expansion ? 65m : 40m, "StructuralExpansionConfirmationMissing")
        };

        decimal rawStop = buy ? pool.LowerPrice : pool.UpperPrice;
        string stopSource = $"BrokenLiquidityPool:{pool.PoolId:N}";
        // Exit-policy routing (plan §4.1): an accepted break with aligned context and volatility
        // expansion gets a managed-expansion runner; aligned context with neutral expansion gets
        // a partial-then-runner; conflicting context rejects outright (a break/retest thesis
        // depends on the higher-timeframe trend actually continuing, unlike a sweep reversal).
        StructuralGeometry geometry;
        if (!_root.AdaptiveTargetManagement.Enabled)
        {
            geometry = _geometry.Build(evidence, direction, rawStop, stopSource);
        }
        else if (!IsContextAligned(evidence, direction))
        {
            decimal rejectedEntry = evidence.Trigger.LatestCandle.Prices.Close +
                (buy ? evidence.ExecutableSpread : -evidence.ExecutableSpread) / 2m;
            geometry = new StructuralGeometry { IsValid = false, Entry = rejectedEntry, ReasonCode = "StructuralV2ContextConflict" };
        }
        else
        {
            geometry = _geometry.BuildAdaptive(evidence, direction, rawStop, stopSource,
                expansion ? TradeExitPolicy.ManagedExpansion : TradeExitPolicy.PartialThenRunner,
                [stopSource]);
        }
        gates.Add(Gate("Geometry", geometry.IsValid, geometry.Quality, geometry.ReasonCode));
        decimal contextQuality = ContextQuality(evidence, direction);
        decimal confirmationAdjustment = StructuralPlaybookRules.ConfirmationAdjustment(_options.CciMode, cci, _root.Confirmation);
        if (expansion)
            confirmationAdjustment = Math.Min(10m, confirmationAdjustment + 3m);
        decimal confidence = StructuralPlaybookRules.Confidence(gates, (contextQuality - 50m) / 6.25m,
            confirmationAdjustment, 0m);
        bool ready = gates.All(item => item.Passed) && confidence >= _options.MinimumConfidence;
        // Name-based, not positional (gates.Take(N)) - a magic-number slice silently misclassifies
        // lifecycle the moment a gate gets reordered or inserted without updating the count.
        bool catalystPassed = gates.Where(item => item.Name is not ("Trigger" or "Cci" or "Expansion" or "Geometry")).All(item => item.Passed);

        return new PlaybookEvaluation
        {
            PlaybookId = PlaybookId,
            Version = Version,
            Direction = direction,
            Lifecycle = ready ? StructuralSetupLifecycle.CandidateProduced :
                failure ? StructuralSetupLifecycle.Invalidated :
                gates.Any(item => item.ReasonCode == "StructuralAcceptedBreakExpired" && !item.Passed)
                    ? StructuralSetupLifecycle.Expired :
                catalystPassed ? StructuralSetupLifecycle.AwaitingTrigger : StructuralSetupLifecycle.CatalystObserved,
            SetupId = setupId,
            PrimaryPoolId = pool.PoolId,
            CatalystAt = catalystAt,
            MandatoryGates = gates.AsReadOnly(),
            SupportingEvidence = BuildSupport(retest is not null, expansion, cci),
            ConflictingEvidence = cci.Alignment == EvidenceAlignment.Conflicting ? ["CciConflicting"] : [],
            ConfidenceContributions =
            [
                new("Context", Math.Clamp((contextQuality - 50m) / 6.25m, -8m, 8m), "StructuralContextAdjustment"),
                new("ContinuationConfirmation", confirmationAdjustment, cci.State)
            ],
            ContextQuality = contextQuality,
            LocationQuality = pool.QualityScore * 100m,
            CatalystQuality = Math.Clamp(Math.Min(pool.QualityScore * 100m, displacementAtr * 100m), 0m, 100m),
            TriggerQuality = triggerPresent ? Math.Max(triggerQuality, 50m) : 0m,
            ConfirmationQuality = Math.Max(cci.Quality, expansion ? 65m : 0m),
            GeometryQuality = geometry.Quality,
            Confidence = confidence,
            ExpiresAt = acceptedBreak.AvailableAt + StructuralPlaybookRules.Bars(evidence.Setup.Interval, _options.MaximumBarsSinceAcceptedBreak),
            ReasonCode = ready ? "StructuralLiquidityBreakRetestCandidate" : FirstFailure(gates, confidence, _options.MinimumConfidence),
            IsReady = ready,
            Geometry = geometry,
            Pool = pool,
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

    private bool ExpansionAligned(StructuralEvidencePacket evidence, PriceActionDirection direction)
    {
        AdxAnalysisSnapshot adx = evidence.Indicators.AdxAnalysis;
        bool dmi = adx.Adx >= _options.MinimumAdx && adx.DirectionalBias == direction;
        bool efficiency = evidence.Indicators.EfficiencyRatio >= _options.MinimumEfficiencyRatio;
        bool volatility = evidence.Indicators.BollingerAnalysis.IsExpansion;
        bool regime = direction == PriceActionDirection.Bullish
            ? evidence.Context.MarketRegime.Regime is MarketRegime.TrendingUp or MarketRegime.BreakoutExpansionUp
            : evidence.Context.MarketRegime.Regime is MarketRegime.TrendingDown or MarketRegime.BreakoutExpansionDown;
        return dmi || efficiency && (volatility || regime);
    }

    private static bool IsContextAligned(StructuralEvidencePacket evidence, PriceActionDirection direction)
    {
        bool buy = direction == PriceActionDirection.Bullish;
        MarketRegime regime = evidence.Context.MarketRegime.Regime;
        return buy
            ? regime is MarketRegime.TrendingUp or MarketRegime.BreakoutExpansionUp || evidence.Context.MarketStructure.Direction == MarketStructureDirection.Rising
            : regime is MarketRegime.TrendingDown or MarketRegime.BreakoutExpansionDown || evidence.Context.MarketStructure.Direction == MarketStructureDirection.Falling;
    }

    private static decimal ContextQuality(StructuralEvidencePacket evidence, PriceActionDirection direction) =>
        direction == PriceActionDirection.Bullish ? evidence.ContextEvidence.BullishQuality : evidence.ContextEvidence.BearishQuality;

    private static MandatoryGate Gate(string name, bool passed, decimal quality, string failure) =>
        new(name, passed, Math.Clamp(quality, 0m, 100m), passed ? $"{name}Passed" : failure);

    private static IReadOnlyList<string> BuildSupport(bool retest, bool expansion, CciAssessment cci)
    {
        var result = new List<string> { "AcceptedLiquidityBreak", cci.State };
        if (retest) result.Add("LiquidityRetestHeld");
        if (expansion) result.Add("ExpansionAligned");
        return result.AsReadOnly();
    }

    private static string FirstFailure(IReadOnlyList<MandatoryGate> gates, decimal confidence, decimal minimum) =>
        gates.FirstOrDefault(item => !item.Passed)?.ReasonCode ??
        (confidence < minimum ? "StructuralConfidenceBelowMinimum" : "StructuralCandidateNotReady");
}
