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
    public string Version => "1.9";

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
            return ArmedPool(evidence) ?? Dormant("StructuralAcceptedBreakUnavailable");

        var evaluations = new List<PlaybookEvaluation>(candidates.Length);
        foreach (LiquidityEvent candidate in candidates)
        {
            PlaybookEvaluation evaluation = EvaluateCandidate(candidate, pools[candidate.PoolId], evidence, state);
            evaluations.Add(evaluation);
        }

        // A newer untouched break must not supersede a still-viable pool that has already retested.
        // This selection only chooses the lifecycle represented in state/diagnostics; every
        // candidate still has to pass the same gates before IsReady can become true.
        return StructuralPlaybookRules.SelectRepresentativeCandidate(evaluations);
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
        int triggerResponseBars = StructuralPlaybookRules.TriggerResponseBars(
            _root.MaximumTriggerBars, _root.MaximumArmedSetupBars);
        DateTimeOffset acceptedBreakExpiresAt = acceptedBreak.AvailableAt +
            StructuralPlaybookRules.Bars(
                evidence.Setup.Interval, _options.MaximumBarsSinceAcceptedBreak);
        DateTimeOffset? triggerExpiresAt = retest is null
            ? null
            : StructuralPlaybookRules.TriggerWindowExpiresAt(
                retest.AvailableAt,
                evidence.Trigger.Interval,
                _root.MaximumTriggerBars,
                _root.MaximumArmedSetupBars);
        bool triggerWindowExpired = triggerExpiresAt is DateTimeOffset expiredTriggerAt &&
            evidence.AvailableAt > expiredTriggerAt;
        // The setup-timeframe liquidity retest already proves the exact location and that price
        // closed on the accepted side of this pool. Trigger-timeframe price action is a separate
        // structural model: its retest reference is a local 5m swing and will not
        // normally equal the narrow 30m liquidity band. Require aligned evidence published after
        // the pool retest; displacement or a local retest-hold confirms continuation, while an
        // unanchored generic rejection does not. Retain the pool as location provenance and stop.
        (PriceActionEvent? triggerEvent, PriceActionSetup? triggerSetup, decimal triggerQuality) =
            StructuralPlaybookRules.Trigger(evidence, direction,
                StructuralTriggerProfile.BreakRetestContinuation,
                _root.Trigger.MinimumPriceActionConfidence, triggerResponseBars, catalystAt);
        bool triggerPresent = triggerEvent is not null || triggerSetup is not null || !_options.RequirePriceActionTrigger;
        CciAssessment cci = StructuralPlaybookRules.AssessCci(evidence, direction, continuation: true);
        bool cciPasses = StructuralPlaybookRules.ConfirmationPasses(_options.CciMode, cci);
        bool expansion = ExpansionAligned(evidence, direction);
        bool expansionPasses = _options.ExpansionMode != StructuralConfirmationMode.Required || expansion;

        // Keep one identity from the visible pool through accepted break, retest and trigger. The
        // state store can now represent one coherent hypothesis instead of minting a new setup at
        // the catalyst. A pool is terminal after this interpretation, so this still prevents a
        // second entry from the same structural thesis.
        string setupId = StructuralIdentity.Create(_root.StrategyVersion, evidence.Instrument, PlaybookId,
            pool.PoolId, null, $"pool-{pool.PoolId:N}", pool.AvailableAt, direction);
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
            Gate("Retest", !_options.RequireRetestEvent || retest is not null, retest is null ? 0m : 65m,
                "StructuralRetestMissing", _options.RequireRetestEvent),
            Gate("RetestDistance", retest is null || retestDistanceAtr <= _options.MaximumRetestDistanceAtr,
                retest is null ? 0m : Math.Clamp(100m - retestDistanceAtr * 100m, 0m, 100m),
                "StructuralRetestTooFar", retest is not null),
            Gate("AcceptedSide", acceptedSide, 60m, "StructuralRetestDidNotHold"),
            Gate("TriggerWindow", !triggerWindowExpired, triggerWindowExpired ? 0m : 60m,
                "StructuralTriggerWindowExpired"),
            Gate("Trigger", triggerPresent, triggerPresent ? Math.Max(triggerQuality, 50m) : 0m,
                "StructuralTriggerMissing", _options.RequirePriceActionTrigger),
            Gate("Cci", cciPasses, cci.Quality, "StructuralCciConfirmationMissing",
                _options.CciMode == StructuralConfirmationMode.Required),
            Gate("Expansion", expansionPasses, expansion ? 65m : 40m,
                "StructuralExpansionConfirmationMissing",
                _options.ExpansionMode == StructuralConfirmationMode.Required)
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
            geometry = _geometry.BuildTieredFixed(evidence, direction, rawStop, stopSource, [stopSource]);
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
        return new PlaybookEvaluation
        {
            PlaybookId = PlaybookId,
            Version = Version,
            Direction = direction,
            Lifecycle = ready ? StructuralSetupLifecycle.CandidateProduced :
                failure ? StructuralSetupLifecycle.Invalidated :
                gates.Any(item => item.ReasonCode == "StructuralAcceptedBreakExpired" && !item.Passed)
                    ? StructuralSetupLifecycle.Expired :
                triggerWindowExpired ? StructuralSetupLifecycle.Expired :
                retest is null ? StructuralSetupLifecycle.CatalystObserved :
                StructuralSetupLifecycle.AwaitingTrigger,
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
            ExpiresAt = triggerExpiresAt is DateTimeOffset responseDeadline
                ? StructuralPlaybookRules.Earlier(
                    acceptedBreakExpiresAt, responseDeadline)
                : acceptedBreakExpiresAt,
            ReasonCode = ready ? "StructuralLiquidityBreakRetestCandidate" : FirstFailure(gates, confidence, _options.MinimumConfidence),
            IsReady = ready,
            Geometry = geometry,
            Pool = pool,
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
            .Where(item => item.QualityScore >= _options.MinimumPoolQuality ||
                item.DistinctTouchCount >= _options.MinimumDistinctTouchesForQualityExemption)
            .OrderByDescending(item => item.QualityScore)
            .ThenBy(item => item.PoolId)
            .FirstOrDefault();
        if (pool is null)
            return null;

        PriceActionDirection direction = pool.Side == LiquiditySide.BuySide
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
            ReasonCode = "StructuralAwaitingAcceptedBreak",
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

    private bool ExpansionAligned(StructuralEvidencePacket evidence, PriceActionDirection direction)
    {
        AdxAnalysisSnapshot adx = evidence.Indicators.AdxAnalysis;
        bool dmi = adx.Adx >= _options.MinimumAdx && adx.DirectionalBias == direction;
        bool efficiency = evidence.Indicators.EfficiencyRatio >= _options.MinimumEfficiencyRatio;
        BollingerAnalysisSnapshot bollinger = evidence.Indicators.BollingerAnalysis;
        bool directionalVolatility = bollinger.PercentB is decimal percentB &&
            (bollinger.IsExpansion || bollinger.WidthDirection == VolatilityDirection.Expanding) &&
            (direction == PriceActionDirection.Bullish ? percentB >= 55m : percentB <= 45m);
        bool squeezeRelease = bollinger.SqueezeReleased && bollinger.PercentB is decimal releasePercentB &&
            (direction == PriceActionDirection.Bullish ? releasePercentB >= 70m : releasePercentB <= 30m);
        DonchianSnapshot donchian = evidence.Trigger.Indicators.Donchian;
        bool channelBreak = direction == PriceActionDirection.Bullish
            ? donchian.ClosedAbovePreviousUpper
            : donchian.ClosedBelowPreviousLower;
        bool breakoutRegime = direction == PriceActionDirection.Bullish
            ? evidence.Context.MarketRegime.Regime == MarketRegime.BreakoutExpansionUp
            : evidence.Context.MarketRegime.Regime == MarketRegime.BreakoutExpansionDown;
        bool trendRegime = direction == PriceActionDirection.Bullish
            ? evidence.Context.MarketRegime.Regime is MarketRegime.TrendingUp or MarketRegime.BreakoutExpansionUp
            : evidence.Context.MarketRegime.Regime is MarketRegime.TrendingDown or MarketRegime.BreakoutExpansionDown;
        bool expansionCatalyst = squeezeRelease || channelBreak || breakoutRegime || directionalVolatility;
        return expansionCatalyst && (dmi || efficiency || trendRegime);
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

    private static MandatoryGate Gate(
        string name,
        bool passed,
        decimal quality,
        string failure,
        bool limitsConfidenceFloor = true) =>
        new(name, passed, Math.Clamp(quality, 0m, 100m),
            passed ? $"{name}Passed" : failure, limitsConfidenceFloor);

    private static IReadOnlyList<string> BuildSupport(bool retest, bool expansion, CciAssessment cci)
    {
        var result = new List<string> { "AcceptedLiquidityBreak" };
        if (retest) result.Add("LiquidityRetestHeld");
        if (expansion) result.Add("ExpansionAligned");
        if (cci.Alignment == EvidenceAlignment.Aligned) result.Add(cci.State);
        return result.AsReadOnly();
    }

    private static string FirstFailure(IReadOnlyList<MandatoryGate> gates, decimal confidence, decimal minimum) =>
        gates.FirstOrDefault(item => !item.Passed)?.ReasonCode ??
        (confidence < minimum ? "StructuralConfidenceBelowMinimum" : "StructuralCandidateNotReady");
}
