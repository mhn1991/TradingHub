using System.Security.Cryptography;
using System.Text;
using Agent.Strategies.StructuralConfluence.Evidence;
using Brokers.Models;
using ChartAnnotator.Liquidity;
using ChartAnnotator.Models;
using ChartAnnotator.SupplyDemand;
using ChartAnnotator.TargetManagement;

namespace Agent.Strategies.StructuralConfluence.TargetManagement;

/// <summary>
/// Outcome of one <see cref="TargetMapBuilder.Build"/> call: the full ranked candidate list plus,
/// when admissible, the resolved <see cref="TradeTargetPlan"/> for the requested exit policy
/// (plan §3, §4.1 - policy selection itself is a Phase 3/playbook concern; this builder only
/// answers "given this policy, is the route admissible and what does the plan look like").
/// </summary>
public sealed record TargetMapResult
{
    public required bool IsAdmissible { get; init; }
    public required string ReasonCode { get; init; }
    public required IReadOnlyList<TradeTargetCandidate> Candidates { get; init; }
    public TradeTargetCandidate? SelectedTerminal { get; init; }
    public TradeTargetCandidate? SelectedCheckpoint { get; init; }
    public TradeTargetPlan? Plan { get; init; }
}

/// <summary>
/// Deterministic tiered target-map builder (Structural Indicator and Adaptive Target Management
/// Plan §3): collects causal swing/liquidity/supply-demand candidates across every configured
/// timeframe, clusters and ranks them, resolves reachability against the nearest significant
/// opposing structure, and evaluates admission for a caller-selected <see cref="TradeExitPolicy"/>.
/// Entirely additive - <see cref="StructuralGeometryBuilder"/>'s v1 nearest-obstacle path is
/// untouched; this type is only invoked from a v2 path once <see cref="AdaptiveTargetManagementOptions.Enabled"/>
/// is true (wiring lands in Phase 3).
/// </summary>
public sealed class TargetMapBuilder
{
    private readonly StructuralConfluenceStrategyOptions _root;
    private readonly AdaptiveTargetManagementOptions _options;

    public TargetMapBuilder(StructuralConfluenceStrategyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _root = options;
        _options = options.AdaptiveTargetManagement;
    }

    public TargetMapResult Build(
        StructuralEvidencePacket evidence,
        PriceActionDirection direction,
        decimal entry,
        decimal stop,
        TradeExitPolicy exitPolicy,
        IReadOnlyCollection<string>? excludedSourceIds = null)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        bool buy = direction == PriceActionDirection.Bullish;
        decimal? atrValue = evidence.Indicators.Atr ?? evidence.Setup.Indicators.Atr;
        decimal risk = buy ? entry - stop : stop - entry;
        if (atrValue is not > 0m || risk <= 0m)
            return Invalid("StructuralTargetMapAtrOrRiskUnavailable");
        decimal atr = atrValue.Value;
        var excluded = excludedSourceIds is { Count: > 0 }
            ? new HashSet<string>(excludedSourceIds, StringComparer.Ordinal)
            : null;

        List<RawCandidate> raw = CollectRawCandidates(evidence, buy, entry, atr, excluded);
        decimal minimumDistance = _root.Geometry.MinimumObstacleDistanceAtr * atr;
        raw = raw.Where(item => Math.Abs(item.RawPrice - entry) >= minimumDistance).ToList();

        IReadOnlyList<TradeTargetCandidate> ranked = ClusterAndRank(raw, entry, atr, risk, evidence.ExecutableSpread);

        TradeTargetCandidate? terminal = ranked.FirstOrDefault(item =>
            item.Tier is TradeTargetSignificanceTier.TierA or TradeTargetSignificanceTier.TierB &&
            item.DistanceAtr <= _options.MaximumTerminalDistanceAtr);
        var withRoles = AssignRoles(ranked, terminal);

        TradeTargetCandidate? checkpoint = withRoles
            .Where(item => item.Role == TradeTargetRole.Checkpoint &&
                (terminal is null || item.DistanceAtr < terminal.DistanceAtr) &&
                item.TargetR >= _options.CheckpointMinimumR)
            .OrderBy(item => item.DistanceAtr)
            .ThenBy(item => item.CandidateId, StringComparer.Ordinal)
            .FirstOrDefault();

        return Evaluate(exitPolicy, withRoles, terminal, checkpoint, entry, stop, risk, atr, evidence.AvailableAt);
    }

    private TargetMapResult Evaluate(
        TradeExitPolicy exitPolicy,
        IReadOnlyList<TradeTargetCandidate> candidates,
        TradeTargetCandidate? terminal,
        TradeTargetCandidate? checkpoint,
        decimal entry,
        decimal stop,
        decimal risk,
        decimal atr,
        DateTimeOffset decisionAt)
    {
        if (exitPolicy == TradeExitPolicy.ManagedExpansion && terminal is null)
        {
            var projection = BuildSyntheticRCandidate(entry, stop, risk, atr, _options.ManagedExpansionProjectionMinimumR,
                TradeTargetRole.Projection, "projection:r-multiple", "StructuralManagedExpansionProjection");
            var withProjection = candidates.Append(projection)
                .Take(_options.MaximumPersistedCandidates)
                .ToArray();
            return BuildPlan(exitPolicy, withProjection, projection, checkpoint, entry, stop, risk, atr, decisionAt);
        }

        if (terminal is null)
            return Invalid("StructuralTargetMapNoTerminalCandidate", candidates);

        decimal terminalR = terminal.TargetR ?? 0m;
        string? gateFailure = exitPolicy switch
        {
            TradeExitPolicy.FixedStructuralTarget when terminalR < _root.MinimumRewardRisk =>
                "StructuralRewardRiskBelowMinimum",
            TradeExitPolicy.PartialThenRunner when terminalR < _options.ManagedOpportunityMinimumR =>
                "StructuralManagedOpportunityBelowMinimum",
            TradeExitPolicy.ManagedExpansion when terminalR < _options.ManagedExpansionProjectionMinimumR =>
                "StructuralManagedExpansionProjectionBelowMinimum",
            _ => null
        };
        if (gateFailure is not null)
            return Invalid(gateFailure, candidates);

        return BuildPlan(exitPolicy, candidates, terminal, checkpoint, entry, stop, risk, atr, decisionAt);
    }

    private TargetMapResult BuildPlan(
        TradeExitPolicy exitPolicy,
        IReadOnlyList<TradeTargetCandidate> candidates,
        TradeTargetCandidate terminal,
        TradeTargetCandidate? checkpoint,
        decimal entry,
        decimal stop,
        decimal risk,
        decimal atr,
        DateTimeOffset decisionAt)
    {
        decimal terminalR = terminal.TargetR ?? 0m;
        // PartialThenRunner always partials (plan §4.4): materialize a synthetic checkpoint
        // candidate into the persisted plan when no real structural checkpoint qualified, so
        // downstream trade management (Phase 4) can read a concrete TargetR from TargetPlan
        // without depending on this project's options at management time.
        if (checkpoint is null && exitPolicy == TradeExitPolicy.PartialThenRunner)
        {
            checkpoint = BuildSyntheticRCandidate(entry, stop, risk, atr, _options.SyntheticPartialCheckpointR,
                TradeTargetRole.Checkpoint, "checkpoint:synthetic-r-multiple", "StructuralSyntheticPartialCheckpoint");
            candidates = candidates.Append(checkpoint).Take(_options.MaximumPersistedCandidates).ToArray();
        }

        bool hasCheckpoint = checkpoint is not null;
        decimal checkpointR = hasCheckpoint ? checkpoint!.TargetR ?? 0m : 0m;
        decimal partialFraction = exitPolicy == TradeExitPolicy.FixedStructuralTarget ? 0m : _options.DefaultPartialFraction;
        bool appliesPartial = exitPolicy switch
        {
            TradeExitPolicy.FixedStructuralTarget => false,
            TradeExitPolicy.PartialThenRunner => true,
            TradeExitPolicy.ManagedExpansion => hasCheckpoint,
            _ => false
        };
        decimal plannedR = exitPolicy == TradeExitPolicy.FixedStructuralTarget
            ? terminalR
            : appliesPartial
                ? partialFraction * checkpointR + (1m - partialFraction) * terminalR
                : terminalR;
        if (exitPolicy == TradeExitPolicy.PartialThenRunner && plannedR < _options.MinimumWeightedPlannedRewardR)
            return Invalid("StructuralPlannedRewardBelowMinimum", candidates);
        if (!appliesPartial)
            partialFraction = 0m;

        var plan = new TradeTargetPlan
        {
            PlanVersion = 1,
            Revision = 0,
            ExitPolicy = exitPolicy,
            OriginalEntry = entry,
            OriginalStop = stop,
            InitialRiskPrice = risk,
            SelectedCheckpointId = checkpoint?.CandidateId,
            SelectedTerminalId = terminal.CandidateId,
            Candidates = candidates,
            PartialFraction = partialFraction,
            MinimumRunnerFraction = exitPolicy == TradeExitPolicy.FixedStructuralTarget ? 1m : _options.MinimumRunnerFraction,
            PlannedR = plannedR,
            ConservativeOpportunityR = terminalR,
            CreatedAt = decisionAt,
            LastRevisedAt = decisionAt
        };
        return new TargetMapResult
        {
            IsAdmissible = true,
            ReasonCode = "AdaptiveTargetPlanReady",
            Candidates = candidates,
            SelectedTerminal = terminal,
            SelectedCheckpoint = checkpoint,
            Plan = plan
        };
    }

    private static TradeTargetCandidate BuildSyntheticRCandidate(
        decimal entry,
        decimal stop,
        decimal risk,
        decimal atr,
        decimal targetR,
        TradeTargetRole role,
        string candidateId,
        string reasonCode)
    {
        bool buy = entry > stop;
        decimal price = buy ? entry + risk * targetR : entry - risk * targetR;
        return new TradeTargetCandidate
        {
            CandidateId = candidateId,
            ClusterId = candidateId,
            SourceKind = TradeTargetSourceKind.RMultipleProjection,
            SourceId = candidateId,
            SourceInterval = BarInterval.Minutes(1),
            LifecycleState = "Synthetic",
            LowerBoundary = price,
            UpperBoundary = price,
            ExecutionPrice = price,
            Role = role,
            Tier = TradeTargetSignificanceTier.TierC,
            Quality = 0m,
            Prominence = 0m,
            Freshness = 0m,
            DistanceAtr = atr > 0m ? Math.Abs(price - entry) / atr : 0m,
            TargetR = targetR,
            AvailableAt = DateTimeOffset.MinValue,
            ReasonCodes = [reasonCode]
        };
    }

    private static TargetMapResult Invalid(string reasonCode, IReadOnlyList<TradeTargetCandidate>? candidates = null) => new()
    {
        IsAdmissible = false,
        ReasonCode = reasonCode,
        Candidates = candidates ?? []
    };

    private static IReadOnlyList<TradeTargetCandidate> AssignRoles(
        IReadOnlyList<TradeTargetCandidate> ranked,
        TradeTargetCandidate? terminal)
    {
        var result = new List<TradeTargetCandidate>(ranked.Count);
        foreach (TradeTargetCandidate candidate in ranked)
        {
            TradeTargetRole role = candidate.Tier == TradeTargetSignificanceTier.TierC
                ? TradeTargetRole.Checkpoint
                : terminal is not null && candidate.CandidateId == terminal.CandidateId
                    ? TradeTargetRole.Terminal
                    : TradeTargetRole.HardBarrier;
            result.Add(candidate with { Role = role });
        }

        return result;
    }

    private IReadOnlyList<TradeTargetCandidate> ClusterAndRank(
        IReadOnlyList<RawCandidate> raw,
        decimal entry,
        decimal atr,
        decimal risk,
        decimal spread)
    {
        if (raw.Count == 0)
            return [];

        decimal clusterDistance = _options.TargetClusterDistanceAtr * atr;
        List<RawCandidate> sorted = raw.OrderBy(item => item.ExecutionPrice).ToList();
        var clusters = new List<List<RawCandidate>>();
        List<RawCandidate> current = [sorted[0]];
        for (int i = 1; i < sorted.Count; i++)
        {
            if (Math.Abs(sorted[i].ExecutionPrice - current[^1].ExecutionPrice) <= clusterDistance)
                current.Add(sorted[i]);
            else
            {
                clusters.Add(current);
                current = [sorted[i]];
            }
        }
        clusters.Add(current);

        var candidates = new List<TradeTargetCandidate>(clusters.Count);
        foreach (List<RawCandidate> cluster in clusters)
        {
            RawCandidate representative = cluster
                .OrderByDescending(item => item.BaseTier == TradeTargetSignificanceTier.TierA ? 2 : item.BaseTier == TradeTargetSignificanceTier.TierB ? 1 : 0)
                .ThenByDescending(item => item.Quality)
                .ThenBy(item => Math.Abs(item.RawPrice - entry))
                .ThenBy(item => item.SourceId, StringComparer.Ordinal)
                .First();
            IReadOnlyList<string> constituents = cluster.Select(item => item.SourceId)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();
            TradeTargetSignificanceTier tier = constituents.Count >= 2
                ? Promote(representative.BaseTier)
                : representative.BaseTier;
            string clusterId = ClusterHash(constituents);
            decimal distanceAtr = Math.Abs(representative.RawPrice - entry) / atr;
            candidates.Add(new TradeTargetCandidate
            {
                CandidateId = clusterId,
                ClusterId = clusterId,
                SourceKind = representative.SourceKind,
                SourceId = representative.SourceId,
                SourceInterval = representative.SourceInterval,
                LifecycleState = representative.LifecycleState,
                LowerBoundary = representative.Lower,
                UpperBoundary = representative.Upper,
                ExecutionPrice = representative.ExecutionPrice,
                Role = TradeTargetRole.Checkpoint,
                Tier = tier,
                Quality = representative.Quality,
                Prominence = representative.Prominence,
                Freshness = representative.Freshness,
                PriorTouchCount = representative.PriorTouchCount,
                DistanceAtr = distanceAtr,
                TargetR = CostAdjustedTargetR(representative.ExecutionPrice, entry, risk, spread),
                ConstituentSourceIds = constituents,
                AvailableAt = representative.AvailableAt,
                ReasonCodes = constituents.Count >= 2 ? ["StructuralTargetClusterConfluence"] : []
            });
        }

        // Nearest reachable obstacle wins the tiebreak within a tier, not the coarsest source
        // timeframe - the previous "prefer coarser timeframe" tiebreaker (right after Tier, ahead
        // of quality/distance) is exactly what let a 2H context zone ~26 ATR away outrank much
        // closer setup/trigger-timeframe candidates for a 5-minute entry. MaximumTerminalDistanceAtr
        // (see Build) stops the most extreme cases from being admissible at all, but distance-first
        // ranking is what actually keeps every REACHABLE candidate consistent with the same
        // "management stays on the entry's own timeframe cascade" principle applied everywhere
        // else (StrategySimulationSession/SimulationStrategyProfile.ValidateManagementTimeframeAlignment,
        // FindStructuralCandidate's stop-trail candidates) - this is the target-side counterpart.
        return candidates
            .OrderBy(item => item.Tier)
            .ThenBy(item => item.DistanceAtr)
            .ThenByDescending(item => item.Quality)
            .ThenByDescending(item => item.Prominence)
            .ThenByDescending(item => item.Freshness)
            .ThenBy(item => item.PriorTouchCount ?? 0)
            .ThenBy(item => item.CandidateId, StringComparer.Ordinal)
            .Take(_options.MaximumPersistedCandidates)
            .ToArray();
    }

    private static decimal? CostAdjustedTargetR(decimal executionPrice, decimal entry, decimal risk, decimal spread)
    {
        // First-order price-space approximation of plan §3.7's cash-based TargetR: reward minus
        // one round of executable spread, divided by price-space risk. The full
        // InitialRiskCash/TargetRewardCash conversion needs quantity, commission, and
        // instrument-value-conversion inputs that are not yet threaded into the Agent evaluation
        // layer (applied downstream at position-sizing time today) - tracked as a documented
        // follow-up rather than a Phase 2 blocker.
        if (risk <= 0m)
            return null;
        decimal reward = Math.Max(0m, Math.Abs(executionPrice - entry) - spread);
        return reward / risk;
    }

    private static TradeTargetSignificanceTier Promote(TradeTargetSignificanceTier tier) => tier switch
    {
        TradeTargetSignificanceTier.TierC => TradeTargetSignificanceTier.TierB,
        _ => TradeTargetSignificanceTier.TierA
    };

    private static string ClusterHash(IReadOnlyList<string> sortedSourceIds)
    {
        string source = string.Join('|', sortedSourceIds);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
    }

    private List<RawCandidate> CollectRawCandidates(
        StructuralEvidencePacket evidence,
        bool buy,
        decimal entry,
        decimal atr,
        HashSet<string>? excluded)
    {
        var result = new List<RawCandidate>();
        var frames = new (AnalysisSnapshot Snapshot, SnapshotRole Role)[]
        {
            (evidence.Context, SnapshotRole.Context),
            (evidence.Setup, SnapshotRole.Setup),
            (evidence.Trigger, SnapshotRole.Trigger)
        }.Concat(evidence.AdditionalContexts.Select(item => (item, SnapshotRole.Context)));

        foreach ((AnalysisSnapshot snapshot, SnapshotRole role) in frames)
        {
            CollectSwings(snapshot, buy, entry, atr, evidence.AvailableAt, result);
            CollectLiquidityPools(snapshot, role, buy, entry, atr, evidence.AvailableAt, excluded, result);
            CollectSupplyDemandZones(snapshot, role, buy, entry, atr, evidence.AvailableAt, excluded, result);
        }

        return result;
    }

    private void CollectSwings(
        AnalysisSnapshot snapshot,
        bool buy,
        decimal entry,
        decimal atr,
        DateTimeOffset availableAt,
        List<RawCandidate> result)
    {
        foreach (SwingPoint swing in snapshot.Swings)
        {
            if (swing.ConfirmedAt > availableAt)
                continue;
            bool onProfitableSide = buy ? swing.Type == SwingType.High && swing.Price > entry
                : swing.Type == SwingType.Low && swing.Price < entry;
            if (!onProfitableSide)
                continue;

            decimal executionPrice = swing.Price + (buy ? -1m : 1m) * _root.TargetBufferAtr * atr;
            decimal quality = Math.Clamp(swing.Strength / 10m, 0m, 1m);
            result.Add(new RawCandidate(
                TradeTargetSourceKind.Swing,
                $"Swing:{snapshot.Interval}:{swing.PivotTime:O}:{swing.Type}",
                snapshot.Interval,
                "Confirmed",
                swing.Price,
                swing.Price,
                swing.Price,
                executionPrice,
                quality,
                quality,
                1m,
                null,
                swing.ConfirmedAt,
                TradeTargetSignificanceTier.TierC));
        }
    }

    private void CollectLiquidityPools(
        AnalysisSnapshot snapshot,
        SnapshotRole role,
        bool buy,
        decimal entry,
        decimal atr,
        DateTimeOffset availableAt,
        HashSet<string>? excluded,
        List<RawCandidate> result)
    {
        LiquiditySide targetSide = buy ? LiquiditySide.BuySide : LiquiditySide.SellSide;
        foreach (LiquidityPool pool in snapshot.Liquidity.Pools)
        {
            if (pool.AvailableAt > availableAt || pool.Side != targetSide)
                continue;
            if (pool.State is not (LiquidityPoolState.Active or LiquidityPoolState.Approached or LiquidityPoolState.Touched))
                continue;
            string sourceId = $"LiquidityPool:{pool.PoolId:N}";
            if (excluded is not null && excluded.Contains(sourceId))
                continue;
            decimal rawPrice = buy ? pool.LowerPrice : pool.UpperPrice;
            if (buy ? rawPrice <= entry : rawPrice >= entry)
                continue;

            decimal executionPrice = rawPrice + (buy ? -1m : 1m) * _root.TargetBufferAtr * atr;
            TradeTargetSignificanceTier tier = role != SnapshotRole.Trigger &&
                pool.QualityScore >= _options.TierAMinimumPoolQuality &&
                pool.ProminenceScore >= _options.TierAMinimumPoolProminence
                ? TradeTargetSignificanceTier.TierA
                : role == SnapshotRole.Setup && pool.QualityScore >= _options.TierBMinimumPoolQuality
                    ? TradeTargetSignificanceTier.TierB
                    : TradeTargetSignificanceTier.TierC;
            result.Add(new RawCandidate(
                TradeTargetSourceKind.LiquidityPool,
                sourceId,
                pool.Interval,
                pool.State.ToString(),
                pool.LowerPrice,
                pool.UpperPrice,
                rawPrice,
                executionPrice,
                pool.QualityScore,
                pool.ProminenceScore,
                pool.FreshnessScore,
                pool.TouchCount,
                pool.AvailableAt,
                tier));
        }
    }

    private void CollectSupplyDemandZones(
        AnalysisSnapshot snapshot,
        SnapshotRole role,
        bool buy,
        decimal entry,
        decimal atr,
        DateTimeOffset availableAt,
        HashSet<string>? excluded,
        List<RawCandidate> result)
    {
        SupplyDemandZoneType opposing = buy ? SupplyDemandZoneType.Supply : SupplyDemandZoneType.Demand;
        foreach (SupplyDemandZone zone in snapshot.SupplyDemand.Zones)
        {
            if (zone.AvailableAt > availableAt || zone.Type != opposing)
                continue;
            if (zone.State is not (SupplyDemandZoneState.ConfirmedFresh or SupplyDemandZoneState.Approached or
                SupplyDemandZoneState.Tested or SupplyDemandZoneState.PartiallyMitigated))
                continue;
            string sourceId = $"SupplyDemandZone:{zone.ZoneId:N}";
            if (excluded is not null && excluded.Contains(sourceId))
                continue;
            decimal lower = Math.Min(zone.ProximalPrice, zone.DistalPrice);
            decimal upper = Math.Max(zone.ProximalPrice, zone.DistalPrice);
            decimal rawPrice = buy ? lower : upper;
            if (buy ? rawPrice <= entry : rawPrice >= entry)
                continue;

            decimal executionPrice = rawPrice + (buy ? -1m : 1m) * _root.TargetBufferAtr * atr;
            TradeTargetSignificanceTier tier = role != SnapshotRole.Trigger &&
                zone.QualityScore >= _options.TierAMinimumZoneQuality &&
                zone.TouchCount <= _options.TierAMaximumPriorTouches
                ? TradeTargetSignificanceTier.TierA
                : role == SnapshotRole.Setup && zone.QualityScore >= _options.TierBMinimumZoneQuality
                    ? TradeTargetSignificanceTier.TierB
                    : TradeTargetSignificanceTier.TierC;
            result.Add(new RawCandidate(
                TradeTargetSourceKind.SupplyDemandZone,
                sourceId,
                zone.Interval,
                zone.State.ToString(),
                lower,
                upper,
                rawPrice,
                executionPrice,
                zone.QualityScore,
                zone.QualityScore,
                zone.FreshnessScore,
                zone.TouchCount,
                zone.AvailableAt,
                tier));
        }
    }

    private enum SnapshotRole
    {
        Context,
        Setup,
        Trigger
    }

    private sealed record RawCandidate(
        TradeTargetSourceKind SourceKind,
        string SourceId,
        BarInterval SourceInterval,
        string LifecycleState,
        decimal Lower,
        decimal Upper,
        decimal RawPrice,
        decimal ExecutionPrice,
        decimal Quality,
        decimal Prominence,
        decimal Freshness,
        int? PriorTouchCount,
        DateTimeOffset AvailableAt,
        TradeTargetSignificanceTier BaseTier);
}
