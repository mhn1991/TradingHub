using Agent.Strategies.StructuralConfluence.Evidence;
using Agent.Strategies.StructuralConfluence.Playbooks;
using ChartAnnotator.Liquidity;
using ChartAnnotator.Models;
using ChartAnnotator.SupplyDemand;

namespace Agent.Strategies.StructuralConfluence;

public sealed class StructuralGeometryBuilder(StructuralConfluenceStrategyOptions options)
{
    public StructuralGeometry Build(
        StructuralEvidencePacket evidence,
        PriceActionDirection direction,
        decimal rawStop,
        string stopSource)
    {
        decimal? atrValue = evidence.Indicators.Atr ?? evidence.Setup.Indicators.Atr;
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

    private static StructuralGeometry Invalid(decimal entry, string reasonCode) => new()
    {
        IsValid = false,
        Entry = entry,
        ReasonCode = reasonCode
    };
}
