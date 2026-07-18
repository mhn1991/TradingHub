using Agent.Models;
using ChartAnnotator.Confluence;
using ChartAnnotator.Liquidity;
using ChartAnnotator.Models;
using ChartAnnotator.SupplyDemand;

namespace Agent.Strategies;

public enum StructuralEvidenceMode
{
    Disabled,
    RecordOnly,
    SoftConfidence,
    SoftRiskReduction,
    SoftConfidenceAndRisk
}

/// <summary>Agent interpretation thresholds. Detector settings remain in ChartAnnotator.</summary>
public sealed record StructuralEvidenceOptions
{
    public decimal MaximumProximityAtr { get; init; } = 1.0m;
    public decimal MinimumQuality { get; init; } = 0.35m;
    public decimal AlignmentConfidenceAdjustment { get; init; } = 2m;
    public decimal OppositionConfidenceAdjustment { get; init; } = -3m;
    public decimal MinimumRiskMultiplier { get; init; } = 0.75m;
    public decimal MaximumConflictRiskReduction { get; init; } = 0.25m;

    public void Validate()
    {
        if (MaximumProximityAtr < 0m ||
            MinimumQuality is < 0m or > 1m ||
            AlignmentConfidenceAdjustment is < -100m or > 100m ||
            OppositionConfidenceAdjustment is < -100m or > 100m ||
            MinimumRiskMultiplier is < 0m or > 1m ||
            MaximumConflictRiskReduction is < 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(StructuralEvidenceOptions));
        }
    }
}

public sealed record SupplyDemandDecisionEvidence
{
    public required decimal ConfidenceAdjustment { get; init; }
    public required decimal RiskMultiplier { get; init; }
    public required IReadOnlyList<string> ReasonCodes { get; init; }
    public required bool DirectionAligned { get; init; }
    public required bool DirectionOpposed { get; init; }
    public SupplyDemandZone? SelectedZone { get; init; }
    public SupplyDemandZone? EntryZone { get; init; }
    public SupplyDemandZone? OpposingZone { get; init; }
    public decimal? SelectedDistanceAtr { get; init; }
}

public sealed record LiquidityDecisionEvidence
{
    public required decimal ConfidenceAdjustment { get; init; }
    public required decimal RiskMultiplier { get; init; }
    public required IReadOnlyList<string> ReasonCodes { get; init; }
    public required bool DirectionAligned { get; init; }
    public required bool DirectionOpposed { get; init; }
    public LiquidityPool? SelectedPool { get; init; }
    public LiquidityPool? TargetPool { get; init; }
    public LiquiditySweepEvent? SelectedSweep { get; init; }
    public SupplyDemandLiquidityConfluence? Confluence { get; init; }
    public decimal? SelectedDistanceAtr { get; init; }
}

public static class StructuralEvidenceEvaluator
{
    public static SupplyDemandDecisionEvidence EvaluateSupplyDemand(
        AnalysisSnapshot snapshot,
        bool buy,
        bool enabled,
        StructuralEvidenceMode mode,
        StructuralEvidenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (!enabled || mode == StructuralEvidenceMode.Disabled)
            return NeutralSupplyDemand("SupplyDemandEvidenceDisabled");
        if (!snapshot.SupplyDemand.IsEnabled || snapshot.Indicators.Atr is not > 0m)
            return NeutralSupplyDemand("SupplyDemandEvidenceNotReady");

        decimal price = snapshot.LatestCandle.Prices.Close;
        decimal atr = snapshot.Indicators.Atr.Value;
        SupplyDemandZoneType alignedType = buy ? SupplyDemandZoneType.Demand : SupplyDemandZoneType.Supply;
        SupplyDemandZone? aligned = NearestZone(snapshot.SupplyDemand.ActiveZones, alignedType, price);
        SupplyDemandZone? opposed = NearestZone(snapshot.SupplyDemand.ActiveZones,
            buy ? SupplyDemandZoneType.Supply : SupplyDemandZoneType.Demand, price);
        decimal? alignedDistance = aligned is null ? null : Distance(price, aligned) / atr;
        decimal? opposedDistance = opposed is null ? null : Distance(price, opposed) / atr;
        bool alignedUsable = aligned is not null && aligned.QualityScore >= options.MinimumQuality &&
            alignedDistance <= options.MaximumProximityAtr;
        bool opposedUsable = opposed is not null && opposed.QualityScore >= options.MinimumQuality &&
            opposedDistance <= options.MaximumProximityAtr;
        bool conflict = opposedUsable && (!alignedUsable || opposedDistance <= alignedDistance);
        bool supports = alignedUsable && !conflict;
        SupplyDemandZone? selected = conflict ? opposed : supports ? aligned : aligned ?? opposed;
        decimal? selectedDistance = selected is null ? null : Distance(price, selected) / atr;

        decimal confidence = AdjustsConfidence(mode)
            ? conflict
                ? options.OppositionConfidenceAdjustment
                : supports
                    ? options.AlignmentConfidenceAdjustment
                    : 0m
            : 0m;
        decimal risk = 1m;
        if (AdjustsRisk(mode) && conflict && opposed is not null)
        {
            risk = Math.Clamp(
                1m - options.MaximumConflictRiskReduction * opposed.QualityScore,
                options.MinimumRiskMultiplier,
                1m);
        }

        var reasons = new List<string>();
        if (aligned is not null)
            reasons.Add($"SupplyDemandAlignedZone:{aligned.Type}:{aligned.State}");
        if (opposed is not null)
            reasons.Add($"SupplyDemandOpposingZone:{opposed.Type}:{opposed.State}");
        reasons.Add(supports
            ? "SupplyDemandAligned"
            : conflict
                ? "SupplyDemandOpposed"
                : "SupplyDemandNeutral");

        return new SupplyDemandDecisionEvidence
        {
            ConfidenceAdjustment = confidence,
            RiskMultiplier = risk,
            ReasonCodes = reasons,
            DirectionAligned = supports,
            DirectionOpposed = conflict,
            SelectedZone = selected,
            EntryZone = aligned,
            OpposingZone = opposed,
            SelectedDistanceAtr = selectedDistance
        };
    }

    public static LiquidityDecisionEvidence EvaluateLiquidity(
        AnalysisSnapshot snapshot,
        bool buy,
        bool enabled,
        bool confluenceEnabled,
        StructuralEvidenceMode mode,
        StructuralEvidenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (!enabled || mode == StructuralEvidenceMode.Disabled)
            return NeutralLiquidity("LiquidityEvidenceDisabled");
        if (!snapshot.Liquidity.IsEnabled || snapshot.Indicators.Atr is not > 0m)
            return NeutralLiquidity("LiquidityEvidenceNotReady");

        decimal price = snapshot.LatestCandle.Prices.Close;
        decimal atr = snapshot.Indicators.Atr.Value;
        Dictionary<Guid, LiquidityPool> pools = snapshot.Liquidity.Pools.ToDictionary(item => item.PoolId);
        LiquiditySweepEvent? sweep = snapshot.Liquidity.RecentSweeps
            .Where(item => item.AvailableAt <= snapshot.AvailableAt && pools.ContainsKey(item.PoolId))
            .OrderByDescending(item => item.AvailableAt)
            .ThenBy(item => item.SweepId)
            .FirstOrDefault();
        LiquidityPool? sweepPool = sweep is null ? null : pools[sweep.PoolId];
        bool sweepBullish = sweepPool?.Side == LiquiditySide.SellSide;
        bool sweepBearish = sweepPool?.Side == LiquiditySide.BuySide;
        bool aligned = sweep is not null && (buy ? sweepBullish : sweepBearish);
        bool opposed = sweep is not null && (buy ? sweepBearish : sweepBullish);

        SupplyDemandLiquidityConfluence? confluence = confluenceEnabled
            ? snapshot.SupplyDemandLiquidityConfluence.Relationships
                .Where(item => item.AvailableAt <= snapshot.AvailableAt)
                .OrderByDescending(item => item.AvailableAt)
                .ThenBy(item => item.ConfluenceId)
                .FirstOrDefault()
            : null;
        if (confluence is not null)
        {
            bool confluenceAligned = buy
                ? confluence.Direction == ConfluenceDirection.Bullish
                : confluence.Direction == ConfluenceDirection.Bearish;
            aligned = confluenceAligned;
            opposed = !confluenceAligned;
            if (confluence.PoolId is Guid poolId && pools.TryGetValue(poolId, out LiquidityPool? related))
                sweepPool = related;
            if (confluence.SweepId is Guid sweepId)
                sweep = snapshot.Liquidity.RecentSweeps.FirstOrDefault(item => item.SweepId == sweepId) ?? sweep;
        }

        LiquidityPool? targetPool = snapshot.Liquidity.ActivePools
            .Where(item => buy
                ? item.Side == LiquiditySide.BuySide && item.ReferencePrice > price
                : item.Side == LiquiditySide.SellSide && item.ReferencePrice < price)
            .OrderBy(item => Math.Abs(item.ReferencePrice - price))
            .ThenBy(item => item.PoolId)
            .FirstOrDefault();
        LiquidityPool? selected = sweepPool ?? targetPool;
        decimal? distanceAtr = selected is null ? null : Distance(price, selected) / atr;
        bool trusted = selected is not null && selected.QualityScore >= options.MinimumQuality;
        if (!trusted)
        {
            aligned = false;
            opposed = false;
        }

        decimal confidence = AdjustsConfidence(mode)
            ? opposed
                ? options.OppositionConfidenceAdjustment
                : aligned
                    ? options.AlignmentConfidenceAdjustment
                    : 0m
            : 0m;
        decimal risk = 1m;
        if (AdjustsRisk(mode) && opposed && selected is not null)
        {
            risk = Math.Clamp(
                1m - options.MaximumConflictRiskReduction * selected.QualityScore,
                options.MinimumRiskMultiplier,
                1m);
        }

        var reasons = new List<string>();
        if (selected is not null)
            reasons.Add($"LiquidityPool:{selected.Type}:{selected.State}:{selected.Side}");
        if (sweep is not null)
            reasons.Add(sweep.ClosedBackInside ? "LiquiditySweepClosedBackInside" : "LiquiditySweepUncertain");
        if (confluence is not null)
            reasons.Add($"SupplyDemandLiquidityConfluence:{confluence.Direction}");
        reasons.Add(aligned ? "LiquidityAligned" : opposed ? "LiquidityOpposed" : "LiquidityNeutral");

        return new LiquidityDecisionEvidence
        {
            ConfidenceAdjustment = confidence,
            RiskMultiplier = risk,
            ReasonCodes = reasons,
            DirectionAligned = aligned,
            DirectionOpposed = opposed,
            SelectedPool = selected,
            TargetPool = targetPool,
            SelectedSweep = sweep,
            Confluence = confluence,
            SelectedDistanceAtr = distanceAtr
        };
    }

    private static SupplyDemandZone? NearestZone(
        IEnumerable<SupplyDemandZone> zones,
        SupplyDemandZoneType type,
        decimal price) => zones
        .Where(item => item.Type == type)
        .OrderBy(item => Distance(price, item))
        .ThenByDescending(item => item.QualityScore)
        .ThenBy(item => item.ZoneId)
        .FirstOrDefault();

    private static decimal Distance(decimal price, SupplyDemandZone zone) =>
        Distance(price, Math.Min(zone.ProximalPrice, zone.DistalPrice),
            Math.Max(zone.ProximalPrice, zone.DistalPrice));

    private static decimal Distance(decimal price, LiquidityPool pool) =>
        Distance(price, pool.LowerPrice, pool.UpperPrice);

    private static decimal Distance(decimal price, decimal lower, decimal upper) =>
        price < lower ? lower - price : price > upper ? price - upper : 0m;

    private static bool AdjustsConfidence(StructuralEvidenceMode mode) => mode is
        StructuralEvidenceMode.SoftConfidence or StructuralEvidenceMode.SoftConfidenceAndRisk;

    private static bool AdjustsRisk(StructuralEvidenceMode mode) => mode is
        StructuralEvidenceMode.SoftRiskReduction or StructuralEvidenceMode.SoftConfidenceAndRisk;

    private static SupplyDemandDecisionEvidence NeutralSupplyDemand(string reason) => new()
    {
        ConfidenceAdjustment = 0m,
        RiskMultiplier = 1m,
        ReasonCodes = [reason],
        DirectionAligned = false,
        DirectionOpposed = false
    };

    private static LiquidityDecisionEvidence NeutralLiquidity(string reason) => new()
    {
        ConfidenceAdjustment = 0m,
        RiskMultiplier = 1m,
        ReasonCodes = [reason],
        DirectionAligned = false,
        DirectionOpposed = false
    };
}

internal static class StructuralTradeGeometry
{
    public static AgentDecision Apply(
        AgentDecision decision,
        AnalysisSnapshot snapshot,
        ProgressiveStrategyOptions options,
        SupplyDemandDecisionEvidence supplyDemand,
        LiquidityDecisionEvidence liquidity)
    {
        if (decision.ReferencePrice is not decimal reference || snapshot.Indicators.Atr is not > 0m)
            return decision;
        bool buy = decision.Action == AgentAction.Buy;
        decimal atr = snapshot.Indicators.Atr.Value;
        decimal? stop = decision.StopLossPrice;
        decimal? target = decision.TakeProfitPrice;
        string? stopSource = decision.StopSource;
        string? targetSource = decision.TargetSource;

        if (options.SupplyDemandEnabled && options.SupplyDemandStructuralStopsEnabled &&
            supplyDemand.EntryZone is { } entryZone)
        {
            decimal candidate = buy
                ? Math.Min(entryZone.ProximalPrice, entryZone.DistalPrice) - atr * options.StopBufferAtr
                : Math.Max(entryZone.ProximalPrice, entryZone.DistalPrice) + atr * options.StopBufferAtr;
            if (buy ? candidate < reference : candidate > reference)
            {
                stop = candidate;
                stopSource = $"SupplyDemandZone:{entryZone.ZoneId}";
            }
        }

        var targets = new List<(decimal Price, string Source)>();
        if (options.LiquidityEnabled && options.LiquidityTargetsEnabled && liquidity.TargetPool is { } pool)
        {
            decimal candidate = buy
                ? pool.LowerPrice - atr * options.TargetBufferAtr
                : pool.UpperPrice + atr * options.TargetBufferAtr;
            if (buy ? candidate > reference : candidate < reference)
                targets.Add((candidate, $"LiquidityPool:{pool.PoolId}"));
        }
        if (options.SupplyDemandEnabled && options.SupplyDemandTargetsEnabled &&
            supplyDemand.OpposingZone is { } opposing)
        {
            decimal candidate = buy
                ? Math.Min(opposing.ProximalPrice, opposing.DistalPrice) - atr * options.TargetBufferAtr
                : Math.Max(opposing.ProximalPrice, opposing.DistalPrice) + atr * options.TargetBufferAtr;
            if (buy ? candidate > reference : candidate < reference)
                targets.Add((candidate, $"SupplyDemandZone:{opposing.ZoneId}"));
        }
        if (targets.Count > 0)
        {
            (target, targetSource) = targets
                .OrderBy(item => Math.Abs(item.Price - reference))
                .ThenBy(item => item.Source, StringComparer.Ordinal)
                .First();
        }

        if (options.LiquidityEnabled && options.LiquidityStopAvoidanceEnabled && stop is decimal proposedStop)
        {
            LiquidityPool? nearby = snapshot.Liquidity.ActivePools
                .Where(item => proposedStop >= item.LowerPrice - atr * 0.05m &&
                    proposedStop <= item.UpperPrice + atr * 0.05m)
                .OrderBy(item => Math.Abs(item.ReferencePrice - proposedStop))
                .ThenBy(item => item.PoolId)
                .FirstOrDefault();
            if (nearby is not null)
            {
                stop = buy
                    ? nearby.LowerPrice - atr * options.StopBufferAtr
                    : nearby.UpperPrice + atr * options.StopBufferAtr;
                stopSource = $"LiquidityAvoidance:{nearby.PoolId}";
            }
        }

        decimal? rewardRisk = stop is decimal finalStop && target is decimal finalTarget && finalStop != reference
            ? Math.Abs(finalTarget - reference) / Math.Abs(reference - finalStop)
            : null;
        return decision with
        {
            StopLossPrice = stop,
            TakeProfitPrice = target,
            StopSource = stopSource,
            TargetSource = targetSource,
            ExpectedRewardRisk = rewardRisk
        };
    }
}
