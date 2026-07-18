using ChartAnnotator.Engine;
using ChartAnnotator.Liquidity;
using ChartAnnotator.SupplyDemand;

namespace ChartAnnotator.Confluence;

/// <summary>
/// Builds explicit, causal relationships between a completed liquidity sweep and a nearby
/// price-derived zone. It does not modify either subsystem's quality score.
/// </summary>
public sealed class SupplyDemandLiquidityConfluenceAnalyzer
{
    private readonly SupplyDemandLiquidityConfluenceOptions _options;
    private readonly Dictionary<Guid, SupplyDemandLiquidityConfluence> _relationships = [];
    private long _snapshotVersion;

    public SupplyDemandLiquidityConfluenceAnalyzer(
        SupplyDemandLiquidityConfluenceOptions? options = null)
    {
        _options = options ?? new SupplyDemandLiquidityConfluenceOptions();
        _options.Validate();
    }

    public SupplyDemandLiquidityConfluenceSnapshot Update(
        SupplyDemandAnalysisSnapshot supplyDemand,
        LiquidityAnalysisSnapshot liquidity,
        decimal? atr,
        DateTimeOffset availableAt)
    {
        ArgumentNullException.ThrowIfNull(supplyDemand);
        ArgumentNullException.ThrowIfNull(liquidity);
        _snapshotVersion++;

        if (!_options.Enabled)
        {
            return SupplyDemandLiquidityConfluenceSnapshot.Disabled with
            {
                SnapshotVersion = _snapshotVersion,
                AvailableAt = availableAt
            };
        }

        if (supplyDemand.IsEnabled && liquidity.IsEnabled && atr is > 0m)
            AddRelationships(supplyDemand, liquidity, atr.Value, availableAt);

        SupplyDemandLiquidityConfluence[] published = _relationships.Values
            .Where(item => item.AvailableAt <= availableAt)
            .OrderByDescending(item => item.AvailableAt)
            .ThenBy(item => item.ConfluenceId)
            .Take(_options.MaximumRetainedRelationships)
            .ToArray();

        if (_relationships.Count > _options.MaximumRetainedRelationships)
        {
            HashSet<Guid> retained = published.Select(item => item.ConfluenceId).ToHashSet();
            foreach (Guid id in _relationships.Keys.Where(id => !retained.Contains(id)).ToArray())
                _relationships.Remove(id);
        }

        return new SupplyDemandLiquidityConfluenceSnapshot
        {
            IsEnabled = true,
            SnapshotVersion = _snapshotVersion,
            AvailableAt = availableAt,
            Relationships = published
        };
    }

    private void AddRelationships(
        SupplyDemandAnalysisSnapshot supplyDemand,
        LiquidityAnalysisSnapshot liquidity,
        decimal atr,
        DateTimeOffset availableAt)
    {
        Dictionary<Guid, LiquidityPool> pools = liquidity.Pools
            .Where(item => item.AvailableAt <= availableAt)
            .ToDictionary(item => item.PoolId);

        foreach (LiquiditySweepEvent sweep in liquidity.RecentSweeps
                     .Where(item => item.AvailableAt <= availableAt)
                     .OrderBy(item => item.AvailableAt)
                     .ThenBy(item => item.SweepId))
        {
            if (!pools.TryGetValue(sweep.PoolId, out LiquidityPool? pool))
                continue;

            SupplyDemandZoneType requiredZone = pool.Side == LiquiditySide.SellSide
                ? SupplyDemandZoneType.Demand
                : SupplyDemandZoneType.Supply;
            ConfluenceDirection direction = pool.Side == LiquiditySide.SellSide
                ? ConfluenceDirection.Bullish
                : ConfluenceDirection.Bearish;

            foreach (SupplyDemandZone zone in supplyDemand.ActiveZones
                         .Where(item => item.Type == requiredZone && item.AvailableAt <= availableAt)
                         .OrderBy(item => item.ZoneId))
            {
                decimal distanceAtr = IntervalDistance(
                    Math.Min(zone.ProximalPrice, zone.DistalPrice),
                    Math.Max(zone.ProximalPrice, zone.DistalPrice),
                    pool.LowerPrice,
                    pool.UpperPrice) / atr;
                if (distanceAtr > _options.MaximumDistanceAtr)
                    continue;

                Guid relationshipId = DeterministicId.Create(
                    "supply-demand-liquidity-confluence",
                    zone.ZoneId,
                    pool.PoolId,
                    sweep.SweepId,
                    direction);
                _relationships.TryAdd(relationshipId, new SupplyDemandLiquidityConfluence
                {
                    ConfluenceId = relationshipId,
                    ZoneId = zone.ZoneId,
                    PoolId = pool.PoolId,
                    SweepId = sweep.SweepId,
                    Direction = direction,
                    ZoneWasFresh = zone.State is SupplyDemandZoneState.ConfirmedFresh or
                        SupplyDemandZoneState.Approached,
                    LiquidityWasSwept = true,
                    DisplacementConfirmed = sweep.DisplacementConfirmed,
                    StructureShiftConfirmed = sweep.StructureShiftConfirmed,
                    DistanceBetweenZoneAndPoolAtr = distanceAtr,
                    QualityScore = Math.Clamp(
                        zone.QualityScore * 0.45m +
                        pool.QualityScore * 0.30m +
                        sweep.QualityScore * 0.25m,
                        0m,
                        1m),
                    AvailableAt = Max(zone.AvailableAt, sweep.AvailableAt)
                });
            }
        }
    }

    private static decimal IntervalDistance(
        decimal leftLower,
        decimal leftUpper,
        decimal rightLower,
        decimal rightUpper) =>
        leftUpper < rightLower
            ? rightLower - leftUpper
            : rightUpper < leftLower
                ? leftLower - rightUpper
                : 0m;

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) =>
        left >= right ? left : right;
}
