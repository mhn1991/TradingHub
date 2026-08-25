using Agent.Models;
using Brokers.Models;
using ChartAnnotator.Models;
using ChartAnnotator.Regime;

namespace Agent.Strategies.StructuralConfluence.Evidence;

public sealed class StructuralEvidencePacketFactory
{
    public bool TryCreate(
        AgentMarketContext context,
        StructuralConfluenceStrategyOptions options,
        out StructuralEvidencePacket? packet,
        out string reasonCode)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        if (!TryGetReady(context, options.ContextInterval, out AnalysisSnapshot higher) ||
            !TryGetReady(context, options.SetupInterval, out AnalysisSnapshot setup) ||
            !TryGetReady(context, options.TriggerInterval, out AnalysisSnapshot trigger))
        {
            packet = null;
            reasonCode = "StructuralAnalysisNotReady";
            return false;
        }

        var additional = new List<AnalysisSnapshot>(options.AdditionalContextIntervals.Count);
        foreach (BarInterval interval in options.AdditionalContextIntervals)
        {
            if (!TryGetReady(context, interval, out AnalysisSnapshot snapshot))
            {
                packet = null;
                reasonCode = "StructuralAnalysisNotReady";
                return false;
            }

            additional.Add(snapshot);
        }

        DateTimeOffset availableAt = context.Timestamp;
        var zones = setup.SupplyDemand.Zones
            .Where(item => item.AvailableAt <= availableAt)
            .OrderByDescending(item => item.AvailableAt).ThenBy(item => item.ZoneId).ToArray();
        var zoneEvents = setup.SupplyDemand.RecentEvents
            .Where(item => item.AvailableAt <= availableAt)
            .OrderByDescending(item => item.AvailableAt).ThenBy(item => item.EventId).ToArray();
        var pools = setup.Liquidity.Pools
            .Where(item => item.AvailableAt <= availableAt)
            .OrderByDescending(item => item.AvailableAt).ThenBy(item => item.PoolId).ToArray();
        var liquidityEvents = setup.Liquidity.RecentEvents
            .Where(item => item.AvailableAt <= availableAt)
            .OrderByDescending(item => item.AvailableAt).ThenBy(item => item.EventId).ToArray();
        var sweeps = setup.Liquidity.RecentSweeps
            .Where(item => item.AvailableAt <= availableAt)
            .OrderByDescending(item => item.AvailableAt).ThenBy(item => item.SweepId).ToArray();
        var triggerEvents = trigger.PriceAction.Events
            .Where(item => item.ConfirmedAt <= availableAt)
            .OrderByDescending(item => item.ConfirmedAt).ThenBy(item => item.EventId, StringComparer.Ordinal).ToArray();
        var triggerSetups = trigger.PriceAction.Setups
            .Where(item => item.ArmedAt <= availableAt && (!item.TriggeredAt.HasValue || item.TriggeredAt <= availableAt))
            .OrderByDescending(item => item.TriggeredAt ?? item.ArmedAt)
            .ThenBy(item => item.SetupId, StringComparer.Ordinal).ToArray();

        packet = new StructuralEvidencePacket
        {
            Instrument = context.Instrument,
            AvailableAt = availableAt,
            ExecutableSpread = Math.Max(0m, context.ExecutableSpread ?? 0m),
            RoundTripCostEstimate = Math.Max(0m, context.RoundTripCostEstimate ?? context.ExecutableSpread ?? 0m),
            Context = higher,
            Setup = setup,
            Trigger = trigger,
            AdditionalContexts = additional.AsReadOnly(),
            ContextEvidence = BuildContextEvidence(higher, additional),
            SupplyDemand = new SupplyDemandPacket(Array.AsReadOnly(zones), Array.AsReadOnly(zoneEvents)),
            Liquidity = new LiquidityPacket(Array.AsReadOnly(pools), Array.AsReadOnly(liquidityEvents), Array.AsReadOnly(sweeps)),
            TriggerEvidence = new TriggerPacket(Array.AsReadOnly(triggerEvents), Array.AsReadOnly(triggerSetups)),
            Indicators = new IndicatorConfirmationPacket(
                trigger.Indicators.Atr,
                trigger.Indicators.Cci,
                trigger.Indicators.CciAnalysis,
                trigger.Indicators.Rsi,
                trigger.Indicators.RsiAnalysis,
                trigger.Indicators.StochRsi,
                trigger.Indicators.BollingerAnalysis,
                trigger.Indicators.AdxAnalysis,
                trigger.Indicators.EfficiencyRatio)
        };
        reasonCode = "StructuralEvidenceReady";
        return true;
    }

    private static bool TryGetReady(
        AgentMarketContext context,
        BarInterval interval,
        out AnalysisSnapshot snapshot)
    {
        if (!context.Analysis.TryGet(interval, out snapshot!) ||
            snapshot.Instrument != context.Instrument ||
            snapshot.AvailableAt > context.Timestamp ||
            (snapshot.LatestCandle.CloseTime ?? snapshot.LatestCandle.OpenTime) > context.Timestamp)
        {
            snapshot = null!;
            return false;
        }

        return true;
    }

    private static StructuralContextEvidence BuildContextEvidence(
        AnalysisSnapshot context,
        IReadOnlyList<AnalysisSnapshot> additional)
    {
        IEnumerable<AnalysisSnapshot> snapshots = new[] { context }.Concat(additional);
        decimal bullish = 0m;
        decimal bearish = 0m;
        int available = 0;
        foreach (AnalysisSnapshot snapshot in snapshots)
        {
            decimal confidence = Math.Clamp(snapshot.MarketRegime.Confidence, 0m, 100m);
            switch (snapshot.MarketRegime.Regime)
            {
                case MarketRegime.TrendingUp:
                case MarketRegime.BreakoutExpansionUp:
                    bullish += confidence;
                    available++;
                    break;
                case MarketRegime.TrendingDown:
                case MarketRegime.BreakoutExpansionDown:
                    bearish += confidence;
                    available++;
                    break;
                default:
                    if (snapshot.MarketStructure.Direction == MarketStructureDirection.Rising)
                    {
                        bullish += Math.Clamp(snapshot.MarketStructure.Strength, 0m, 100m);
                        available++;
                    }
                    else if (snapshot.MarketStructure.Direction == MarketStructureDirection.Falling)
                    {
                        bearish += Math.Clamp(snapshot.MarketStructure.Strength, 0m, 100m);
                        available++;
                    }
                    break;
            }
        }

        if (available == 0)
            return new StructuralContextEvidence(EvidenceAlignment.Unavailable, EvidenceAlignment.Unavailable, 0m, 0m);

        decimal bullQuality = Math.Clamp(bullish / available, 0m, 100m);
        decimal bearQuality = Math.Clamp(bearish / available, 0m, 100m);
        return new StructuralContextEvidence(
            bullQuality > bearQuality ? EvidenceAlignment.Aligned : bearQuality > bullQuality ? EvidenceAlignment.Conflicting : EvidenceAlignment.Neutral,
            bearQuality > bullQuality ? EvidenceAlignment.Aligned : bullQuality > bearQuality ? EvidenceAlignment.Conflicting : EvidenceAlignment.Neutral,
            bullQuality,
            bearQuality);
    }
}
