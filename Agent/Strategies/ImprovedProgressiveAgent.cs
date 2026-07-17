using Agent.Abstractions;
using Agent.Models;
using Brokers.Models;
using ChartAnnotator.Models;

namespace Agent.Strategies;

/// <summary>
/// Progressive multi-timeframe entry with structure-derived invalidation,
/// RR-aware structural stop/target selection, and PA-setup-aware protection.
/// </summary>
public sealed class ImprovedProgressiveAgent(ProgressiveStrategyOptions? options = null)
    : ProgressiveStrategyBase(options)
{
    /// <summary>Minimum stop distance in ATR so micro-swings do not invent fake R:R.</summary>
    private const decimal MinimumStopAtr = 0.35m;

    /// <summary>Prefer not to place initial stops beyond this ATR distance.</summary>
    private const decimal MaximumStopAtr = 2.75m;

    public override string Name => "Improved progressive structural plan";

    public override AgentExitManagementMode ExitManagementMode =>
        AgentExitManagementMode.Bracket;

    protected override AgentDecision CreateEntryDecision(
        AgentMarketContext context,
        ScopeState state,
        AnalysisSnapshot trend,
        AnalysisSnapshot confirmation,
        AnalysisSnapshot entry,
        PriceActionSetup? priceActionSetup)
    {
        decimal price = entry.LatestCandle.Prices.Close;
        decimal atr = entry.Indicators.Atr is > 0m
            ? entry.Indicators.Atr.Value
            : Math.Max(price * 0.002m, 0.00000001m);
        bool buy = state.Side == SetupSide.Buy;

        (decimal stop, string stopSource) = SelectStop(
            buy,
            price,
            atr,
            entry,
            confirmation,
            trend,
            priceActionSetup);

        if ((buy && stop >= price) || (!buy && stop <= price))
        {
            return Observe(context, "No valid structural invalidation level was available.") with
            {
                ReasonCode = "InvalidStopSide"
            };
        }

        decimal risk = Math.Abs(price - stop);
        if (risk <= 0m)
        {
            return Observe(context, "Entry risk collapsed to zero after stop selection.") with
            {
                ReasonCode = "ZeroRisk"
            };
        }

        (decimal? selectedTarget, string targetSource) = SelectTarget(
            buy,
            price,
            atr,
            risk,
            entry,
            confirmation,
            trend);

        if (selectedTarget is not decimal target)
        {
            return Observe(context, targetSource) with
            {
                ReasonCode = "RewardBlockedByStructure"
            };
        }

        decimal reward = Math.Abs(target - price);
        decimal rr = reward / risk;
        if ((buy && target <= price) || (!buy && target >= price))
        {
            return Observe(context, "No valid target existed on the trade side of price.") with
            {
                ReasonCode = "InvalidTargetSide"
            };
        }

        if (rr + 0.0000001m < Options.MinimumRewardRisk)
        {
            return Observe(
                context,
                $"The selected structural target offers only {rr:F2}R, below the " +
                $"required {Options.MinimumRewardRisk:F2}R.") with
            {
                ReasonCode = "InsufficientStructuralReward"
            };
        }

        decimal confidence =
            entry.Confidence.Total * 0.40m +
            confirmation.Confidence.Total * 0.35m +
            trend.Confidence.Total * 0.25m +
            PriceActionConfidenceAdjustment(entry, state.Side) +
            ActiveRetestConfidenceAdjustment(entry, state.Side);
        if (priceActionSetup is not null)
            confidence += Math.Min(8m, priceActionSetup.Confidence * 0.08m);

        string paNote = priceActionSetup is not null
            ? $"{priceActionSetup.Type} ({priceActionSetup.Confidence:F0})"
            : PriceActionSummary(entry, state.Side);

        return Trade(
            context,
            state,
            buy ? AgentAction.Buy : AgentAction.Sell,
            confidence,
            price,
            stop,
            target,
            $"Progressive setup confirmed with a structural stop and {rr:F2}R target; " +
            $"price action: {paNote}.",
            stopSource,
            targetSource);
    }

    protected override AgentDecision EvaluateOpenPosition(
        AgentMarketContext context,
        BrokerPosition position,
        AnalysisSnapshot trend,
        AnalysisSnapshot confirmation,
        AnalysisSnapshot entry)
    {
        bool longPosition = position.Side == OrderSide.Buy;
        bool invalidated = longPosition
            ? HasOpposingBreak(SetupSide.Buy, confirmation) || Opposes(SetupSide.Buy, trend)
            : HasOpposingBreak(SetupSide.Sell, confirmation) || Opposes(SetupSide.Sell, trend);
        return invalidated
            ? Close(
                context,
                position,
                Math.Max(confirmation.Confidence.Total, trend.Confidence.Total),
                "The structural trade thesis was invalidated before the bracket exit was reached.")
            : Observe(
                context,
                "Improved position is managed by its bracket orders and structural invalidation.");
    }

    private (decimal Price, string Source) SelectStop(
        bool buy,
        decimal price,
        decimal atr,
        AnalysisSnapshot entry,
        AnalysisSnapshot confirmation,
        AnalysisSnapshot trend,
        PriceActionSetup? priceActionSetup)
    {
        var candidates = new List<(decimal Price, int Priority, string Source)>();

        // 1) PA composite reference (triggered this bar, or still-armed setup).
        foreach (PriceActionSetup setup in EnumerateSetupLevels(entry, buy, priceActionSetup))
        {
            if (setup.ReferenceLevel is not decimal level)
                continue;
            if (buy ? level >= price : level <= price)
                continue;
            decimal buffered = buy
                ? level - atr * Options.StopBufferAtr
                : level + atr * Options.StopBufferAtr;
            candidates.Add((buffered, 0, $"PA setup {setup.Type} reference plus ATR buffer"));
        }

        // 2) Recent swing/zone invalidation on entry / confirmation / trend.
        foreach (AnalysisSnapshot snapshot in new[] { entry, confirmation, trend })
        {
            IEnumerable<SwingPoint> swings = snapshot.Swings
                .Where(s => buy
                    ? s.Type == SwingType.Low && s.Price < price
                    : s.Type == SwingType.High && s.Price > price)
                .OrderByDescending(s => s.PivotTime)
                .Take(6);

            foreach (SwingPoint swing in swings)
            {
                decimal buffered = buy
                    ? swing.Price - atr * Options.StopBufferAtr
                    : swing.Price + atr * Options.StopBufferAtr;
                candidates.Add((
                    buffered,
                    snapshot.Interval == entry.Interval ? 1 : 3,
                    $"{BarIntervalParser.Format(snapshot.Interval)} swing plus ATR buffer"));
            }


            foreach (PriceZone zone in snapshot.PriceZones.Where(zone =>
                         zone.Strength >= Options.MinimumZoneStrength &&
                         (buy
                             ? (zone.Type is PriceZoneType.Support or PriceZoneType.Mixed) &&
                               zone.LowerPrice < price
                             : (zone.Type is PriceZoneType.Resistance or PriceZoneType.Mixed) &&
                               zone.UpperPrice > price)))
            {
                decimal level = buy ? zone.LowerPrice : zone.UpperPrice;
                decimal buffered = buy
                    ? level - atr * Options.StopBufferAtr
                    : level + atr * Options.StopBufferAtr;
                candidates.Add((
                    buffered,
                    snapshot.Interval == entry.Interval ? 2 : 3,
                    $"{BarIntervalParser.Format(snapshot.Interval)} {zone.Type.ToString().ToLowerInvariant()} " +
                    $"zone ({zone.Strength:F0}) plus ATR buffer"));
            }
        }

        // 3) ATR fallback always available.
        // (Channel boundaries were deliberately removed as a stop candidate source -
        // RANSAC trendline/channel detection is not considered reliable enough to
        // anchor invalidation levels.)
        decimal atrFallback = buy
            ? price - atr * Options.FallbackStopAtr
            : price + atr * Options.FallbackStopAtr;
        candidates.Add((atrFallback, 5, "ATR fallback"));

        // Keep only correct-side stops and clamp extreme distances into a tradable band.
        var normalized = new List<(decimal Price, int Priority, string Source, decimal RiskAtr)>();
        foreach ((decimal stop, int priority, string source) in candidates)
        {
            if (buy ? stop >= price : stop <= price)
                continue;

            decimal risk = Math.Abs(price - stop);
            decimal riskAtr = risk / atr;
            decimal adjusted = stop;
            string adjustedSource = source;

            if (riskAtr < MinimumStopAtr)
            {
                adjusted = buy
                    ? price - atr * MinimumStopAtr
                    : price + atr * MinimumStopAtr;
                adjustedSource = $"{source} (widened to {MinimumStopAtr:F2} ATR min)";
                riskAtr = MinimumStopAtr;
            }
            else if (riskAtr > MaximumStopAtr && priority < 5)
            {
                // Prefer a tighter stop over an oversized structural stop.
                continue;
            }

            normalized.Add((adjusted, priority, adjustedSource, riskAtr));
        }

        if (normalized.Count == 0)
            return (atrFallback, "ATR fallback");

        // Prefer higher-quality structure, then a moderate risk size (~1 ATR).
        (decimal Price, int Priority, string Source, decimal RiskAtr) best = normalized
            .OrderBy(item => item.Priority)
            .ThenBy(item => Math.Abs(item.RiskAtr - 1.0m))
            .ThenBy(item => item.RiskAtr)
            .First();

        return (best.Price, best.Source);
    }

    private (decimal? Price, string Source) SelectTarget(
        bool buy,
        decimal price,
        decimal atr,
        decimal risk,
        params AnalysisSnapshot[] snapshots)
    {
        decimal minReward = risk * Options.MinimumRewardRisk;
        decimal minimumSwingDistance = atr * Options.MinimumSwingTargetDistanceAtr;
        decimal targetBuffer = atr * Options.TargetBufferAtr;
        var candidates = new List<(decimal Price, int Priority, string Source, decimal Reward)>();

        foreach (AnalysisSnapshot snapshot in snapshots)
        {
            foreach (PriceZone zone in snapshot.PriceZones.Where(zone =>
                         zone.Strength >= Options.MinimumZoneStrength &&
                         (buy
                             ? zone.Type is PriceZoneType.Resistance or PriceZoneType.Mixed
                             : zone.Type is PriceZoneType.Support or PriceZoneType.Mixed)))
            {
                decimal barrier = buy ? zone.LowerPrice : zone.UpperPrice;
                if (buy ? barrier <= price : barrier >= price)
                    continue;
                decimal target = buy ? barrier - targetBuffer : barrier + targetBuffer;
                decimal reward = buy
                    ? Math.Max(0m, target - price)
                    : Math.Max(0m, price - target);
                candidates.Add((
                    target,
                    0,
                    $"{BarIntervalParser.Format(snapshot.Interval)} {zone.Type.ToString().ToLowerInvariant()} " +
                    $"zone ({zone.Strength:F0}), target buffered before the barrier",
                    reward));
            }

            foreach (SwingPoint swing in snapshot.Swings.Where(swing =>
                         swing.Strength >= 2 && swing.ConfirmedAt <= snapshot.AvailableAt))
            {
                bool usable = buy
                    ? swing.Type == SwingType.High && swing.Price > price
                    : swing.Type == SwingType.Low && swing.Price < price;
                if (!usable)
                    continue;
                decimal target = buy
                    ? swing.Price - targetBuffer
                    : swing.Price + targetBuffer;
                if (buy ? target <= price : target >= price)
                    continue;
                decimal reward = Math.Abs(target - price);
                if (reward < minimumSwingDistance)
                    continue;
                candidates.Add((
                    target,
                    1,
                    $"{BarIntervalParser.Format(snapshot.Interval)} confirmed swing, " +
                    "target buffered before the barrier",
                    reward));
            }
        }
        // (Channel boundaries were deliberately removed as a target candidate source -
        // RANSAC trendline/channel detection is not considered reliable enough to
        // anchor a profit objective.)

        // The nearest credible opposing structure is the available reward. Skipping it
        // to select a farther level manufactures R:R through a barrier the trade must
        // first overcome.
        (decimal Price, int Priority, string Source, decimal Reward)? nearest = candidates
            .OrderBy(item => item.Reward)
            .ThenBy(item => item.Priority)
            .Select(item => ((decimal Price, int Priority, string Source, decimal Reward)?)item)
            .FirstOrDefault();

        if (nearest is not null)
        {
            decimal availableR = nearest.Value.Reward / risk;
            if (nearest.Value.Reward + 0.0000001m < minReward)
            {
                return (
                    null,
                    $"Nearest opposing structure ({nearest.Value.Source}) offers only " +
                    $"{availableR:F2}R; required minimum is {Options.MinimumRewardRisk:F2}R.");
            }

            return (nearest.Value.Price, nearest.Value.Source);
        }

        // No credible mapped obstacle: use the more conservative of the ATR objective
        // and minimum-R objective, while clearly labelling it as a projection.
        decimal projectedReward = Math.Max(atr * Options.FallbackTargetAtr, minReward);
        decimal projected = buy ? price + projectedReward : price - projectedReward;
        string source = projectedReward <= atr * Options.FallbackTargetAtr + 0.0000001m
            ? $"{Options.FallbackTargetAtr:F2} ATR projection (no mapped obstacle)"
            : $"Minimum {Options.MinimumRewardRisk:F2}R projection (no mapped obstacle)";
        return (projected, source);
    }

    private static IEnumerable<PriceActionSetup> EnumerateSetupLevels(
        AnalysisSnapshot entry,
        bool buy,
        PriceActionSetup? preferred)
    {
        if (preferred is not null)
            yield return preferred;

        PriceActionDirection direction = buy
            ? PriceActionDirection.Bullish
            : PriceActionDirection.Bearish;

        foreach (PriceActionSetup setup in entry.PriceAction.Setups
                     .Where(item =>
                         item.Direction == direction &&
                         item.Phase is PriceActionSetupPhase.Triggered or PriceActionSetupPhase.Armed)
                     .OrderByDescending(item => item.Phase == PriceActionSetupPhase.Triggered)
                     .ThenByDescending(item => item.Confidence))
        {
            if (preferred is not null && setup.SetupId == preferred.SetupId)
                continue;
            yield return setup;
        }
    }
}
