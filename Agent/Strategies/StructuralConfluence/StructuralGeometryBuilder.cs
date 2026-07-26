using Agent.Strategies.StructuralConfluence.Evidence;
using Agent.Strategies.StructuralConfluence.Playbooks;
using Agent.Strategies.StructuralConfluence.TargetManagement;
using ChartAnnotator.Liquidity;
using ChartAnnotator.Models;
using ChartAnnotator.SupplyDemand;
using ChartAnnotator.TargetManagement;

namespace Agent.Strategies.StructuralConfluence;

public sealed class StructuralGeometryBuilder(StructuralConfluenceStrategyOptions options)
{
    private readonly TargetMapBuilder _targetMap = new(options);

    public StructuralGeometry Build(
        StructuralEvidencePacket evidence,
        PriceActionDirection direction,
        decimal rawStop,
        string stopSource)
    {
        // Stop invalidation is setup-interval structure (sweep/zone). Buffer and risk gates must
        // use setup ATR so a 15m stop is not cushioned by a much smaller 5m trigger ATR.
        // Entry price still comes from the trigger bar close below.
        decimal? atrValue = ResolveGeometryAtr(evidence);
        decimal close = evidence.Trigger.LatestCandle.Prices.Close;
        bool buy = direction == PriceActionDirection.Bullish;
        decimal entry = close + (buy ? evidence.ExecutableSpread : -evidence.ExecutableSpread) / 2m;
        if (atrValue is not > 0m)
            return Invalid(entry, "StructuralAtrUnavailable");
        decimal atr = atrValue.Value;
        decimal stop = rawStop + (buy ? -1m : 1m) * options.StopBufferAtr * atr;
        decimal risk = buy ? entry - stop : stop - entry;
        if (risk <= 0m || risk > options.Geometry.MaximumStopDistanceAtr * atr)
            return Invalid(entry, "StructuralStopInvalid");
        if (evidence.ExecutableSpread > 0m &&
            risk < evidence.ExecutableSpread * options.Geometry.MinimumRiskToSpreadMultiple)
            return Invalid(entry, "StructuralStopDistanceBelowExecutionCostFloor");

        var obstacles = new List<(decimal Price, string Source, LiquidityPool? Pool)>();
        foreach (SwingPoint swing in evidence.Setup.Swings.Where(item => item.ConfirmedAt <= evidence.AvailableAt))
        {
            if (buy && swing.Type == SwingType.High && swing.Price > entry ||
                !buy && swing.Type == SwingType.Low && swing.Price < entry)
                obstacles.Add((swing.Price, $"Swing:{swing.PivotTime:O}", null));
        }

        LiquiditySide targetSide = buy ? LiquiditySide.BuySide : LiquiditySide.SellSide;
        foreach (LiquidityPool pool in evidence.Liquidity.Pools.Where(item =>
                     item.Side == targetSide && item.State is not (LiquidityPoolState.Consumed or LiquidityPoolState.Broken or LiquidityPoolState.Expired or LiquidityPoolState.Merged)))
        {
            decimal price = buy ? pool.LowerPrice : pool.UpperPrice;
            if (buy && price > entry || !buy && price < entry)
                obstacles.Add((price, $"LiquidityPool:{pool.PoolId:N}", pool));
        }

        SupplyDemandZoneType opposing = buy ? SupplyDemandZoneType.Supply : SupplyDemandZoneType.Demand;
        foreach (SupplyDemandZone zone in evidence.SupplyDemand.Zones.Where(item =>
                     item.Type == opposing && item.State is not (SupplyDemandZoneState.Invalidated or SupplyDemandZoneState.Mitigated or SupplyDemandZoneState.Expired or SupplyDemandZoneState.Merged)))
        {
            decimal price = buy ? Math.Min(zone.ProximalPrice, zone.DistalPrice) : Math.Max(zone.ProximalPrice, zone.DistalPrice);
            if (buy && price > entry || !buy && price < entry)
                obstacles.Add((price, $"SupplyDemandZone:{zone.ZoneId:N}", null));
        }

        decimal minimumDistance = options.Geometry.MinimumObstacleDistanceAtr * atr;
        var sortedObstacles = obstacles
            .Where(item => Math.Abs(item.Price - entry) >= minimumDistance)
            .OrderBy(item => Math.Abs(item.Price - entry))
            .ThenBy(item => item.Source, StringComparer.Ordinal)
            .ToArray();
        if (sortedObstacles.Length == 0)
            return Invalid(entry, "StructuralTargetUnavailable");
        var nearest = sortedObstacles[0];

        decimal target = nearest.Price + (buy ? -1m : 1m) * options.TargetBufferAtr * atr;
        decimal reward = buy ? target - entry : entry - target;
        if (reward <= 0m)
            return Invalid(entry, "StructuralTargetInvalid");
        decimal rewardRisk = reward / risk;
        if (rewardRisk < options.MinimumRewardRisk)
            return Invalid(entry, "StructuralRewardRiskBelowMinimum");

        decimal geometryQuality = Math.Clamp(50m + Math.Min(40m, (rewardRisk - options.MinimumRewardRisk) * 15m) -
            evidence.ExecutableSpread / atr * 10m, 0m, 100m);
        return new StructuralGeometry
        {
            IsValid = true,
            Entry = entry,
            Stop = stop,
            Target = target,
            RewardRisk = rewardRisk,
            Quality = geometryQuality,
            StopSource = stopSource,
            TargetSource = nearest.Source,
            TargetPool = nearest.Pool,
            ReasonCode = "StructuralGeometryValid"
        };
    }

    /// <summary>
    /// v2 tiered target-map path (Structural Indicator and Adaptive Target Management Plan §3-§4).
    /// Reuses <see cref="Build"/>'s exact stop-geometry/risk computation and validity gates
    /// unchanged, then replaces nearest-obstacle target selection with <see cref="TargetMapBuilder"/>.
    /// Adaptive callers use the selected <paramref name="exitPolicy"/>; the conventional fixed
    /// bracket wrapper below reuses the same target quality logic and strips management metadata.
    /// <see cref="Build"/> remains available as the original nearest-obstacle implementation.
    /// </summary>
    public StructuralGeometry BuildAdaptive(
        StructuralEvidencePacket evidence,
        PriceActionDirection direction,
        decimal rawStop,
        string stopSource,
        TradeExitPolicy exitPolicy,
        IReadOnlyCollection<string>? excludedSourceIds = null)
    {
        decimal? atrValue = ResolveGeometryAtr(evidence);
        decimal close = evidence.Trigger.LatestCandle.Prices.Close;
        bool buy = direction == PriceActionDirection.Bullish;
        decimal entry = close + (buy ? evidence.ExecutableSpread : -evidence.ExecutableSpread) / 2m;
        if (atrValue is not > 0m)
            return Invalid(entry, "StructuralAtrUnavailable");
        decimal atr = atrValue.Value;
        decimal stop = rawStop + (buy ? -1m : 1m) * options.StopBufferAtr * atr;
        decimal risk = buy ? entry - stop : stop - entry;
        if (risk <= 0m || risk > options.Geometry.MaximumStopDistanceAtr * atr)
            return Invalid(entry, "StructuralStopInvalid");
        if (evidence.ExecutableSpread > 0m &&
            risk < evidence.ExecutableSpread * options.Geometry.MinimumRiskToSpreadMultiple)
            return Invalid(entry, "StructuralStopDistanceBelowExecutionCostFloor");

        TargetMapResult map = _targetMap.Build(evidence, direction, entry, stop, exitPolicy, excludedSourceIds);
        if (!map.IsAdmissible || map.Plan is null)
            return Invalid(entry, map.ReasonCode);

        TradeTargetCandidate terminal = map.SelectedTerminal!;
        decimal geometryQuality = Math.Clamp(50m + Math.Min(40m, (map.Plan.ConservativeOpportunityR - options.MinimumRewardRisk) * 15m) -
            evidence.ExecutableSpread / atr * 10m, 0m, 100m);
        return new StructuralGeometry
        {
            IsValid = true,
            Entry = entry,
            Stop = stop,
            // Compatibility fields (plan §5.2): populated from the selected terminal/projection
            // so legacy report/replay consumers that only know about a single hard target still
            // see a sensible value, even though a managed policy never submits it as a broker order.
            Target = terminal.ExecutionPrice,
            RewardRisk = map.Plan.PlannedR,
            Quality = geometryQuality,
            StopSource = stopSource,
            TargetSource = $"{terminal.SourceKind}:{terminal.SourceId}",
            // TargetPool intentionally left null here: TargetMapBuilder returns a source-agnostic
            // TradeTargetCandidate, not the originating LiquidityPool object. A caller that needs
            // TargetLiquidityPoolId can resolve it from evidence by terminal.SourceId when wiring
            // playbooks in Phase 3.
            TargetPool = null,
            ReasonCode = "StructuralGeometryValid",
            ExitPolicy = exitPolicy,
            TargetPlan = map.Plan
        };
    }

    /// <summary>
    /// Uses the tiered structural target map for a conventional one-stop/one-target bracket.
    /// Minor Tier-C swings may act as checkpoints, but the nearest significant Tier-A/B opposing
    /// structure remains the hard target. Adaptive exit metadata is deliberately removed so this
    /// path cannot require partial-exit or runner management.
    /// </summary>
    public StructuralGeometry BuildTieredFixed(
        StructuralEvidencePacket evidence,
        PriceActionDirection direction,
        decimal rawStop,
        string stopSource,
        IReadOnlyCollection<string>? excludedSourceIds = null)
    {
        StructuralGeometry geometry = BuildAdaptive(
            evidence,
            direction,
            rawStop,
            stopSource,
            TradeExitPolicy.FixedStructuralTarget,
            excludedSourceIds);

        return geometry with
        {
            ExitPolicy = null,
            TargetPlan = null
        };
    }

    /// <summary>
    /// ATR for stop buffer / risk gates / geometry quality. Prefers setup-interval ATR because
    /// raw stops are setup structure; trigger ATR only as fallback if setup is not ready.
    /// </summary>
    private static decimal? ResolveGeometryAtr(StructuralEvidencePacket evidence) =>
        evidence.Setup.Indicators.Atr ?? evidence.Indicators.Atr;

    private static StructuralGeometry Invalid(decimal entry, string reasonCode) => new()
    {
        IsValid = false,
        Entry = entry,
        ReasonCode = reasonCode
    };
}
