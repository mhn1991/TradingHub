using Agent.Abstractions;
using Agent.Models;
using Brokers.Models;
using ChartAnnotator.Models;

namespace Agent.Strategies;

/// <summary>Progressive multi-timeframe entry with structure-derived invalidation,
/// structural target selection, and a pre-entry minimum reward/risk check.</summary>
public sealed class ImprovedProgressiveAgent(ProgressiveStrategyOptions? options = null)
    : ProgressiveStrategyBase(options)
{
    public override string Name => "Improved progressive structural plan";

    public override AgentExitManagementMode ExitManagementMode =>
        AgentExitManagementMode.Bracket;

    protected override AgentDecision CreateEntryDecision(
        AgentMarketContext context, ScopeState state, AnalysisSnapshot trend,
        AnalysisSnapshot confirmation, AnalysisSnapshot entry)
    {
        decimal price = entry.LatestCandle.Prices.Close;
        decimal atr = entry.Indicators.Atr ?? price * 0.002m;
        bool buy = state.Side == SetupSide.Buy;

        (decimal stop, string stopSource) = SelectStop(buy, price, atr, entry, confirmation);
        (decimal target, string targetSource) = SelectTarget(buy, price, atr, entry, confirmation, trend);
        decimal risk = Math.Abs(price - stop);
        decimal reward = Math.Abs(target - price);
        decimal rr = risk == 0m ? 0m : reward / risk;
        if (risk <= 0m || (buy && stop >= price) || (!buy && stop <= price))
            return Observe(context, "No valid structural invalidation level was available.");
        if ((buy && target <= price) || (!buy && target >= price) || rr < Options.MinimumRewardRisk)
            return Observe(context, $"The nearest valid target offers only {rr:F2}R; minimum is {Options.MinimumRewardRisk:F2}R.");

        decimal confidence = entry.Confidence.Total * 0.40m + confirmation.Confidence.Total * 0.35m + trend.Confidence.Total * 0.25m +
            PriceActionConfidenceAdjustment(entry, state.Side);
        return Trade(context, state, buy ? AgentAction.Buy : AgentAction.Sell, confidence,
            price, stop, target,
            $"Progressive setup confirmed with a structural stop and {rr:F2}R target; " +
            $"price action: {PriceActionSummary(entry, state.Side)}.",
            stopSource, targetSource);
    }

    protected override AgentDecision EvaluateOpenPosition(
        AgentMarketContext context, BrokerPosition position, AnalysisSnapshot trend,
        AnalysisSnapshot confirmation, AnalysisSnapshot entry)
    {
        bool longPosition = position.Side == OrderSide.Buy;
        bool invalidated = longPosition
            ? HasOpposingBreak(SetupSide.Buy, confirmation) || Opposes(SetupSide.Buy, trend)
            : HasOpposingBreak(SetupSide.Sell, confirmation) || Opposes(SetupSide.Sell, trend);
        return invalidated
            ? Close(context, position, Math.Max(confirmation.Confidence.Total, trend.Confidence.Total),
                "The structural trade thesis was invalidated before the bracket exit was reached.")
            : Observe(context, "Improved position is managed by its bracket orders and structural invalidation.");
    }

    private (decimal Price, string Source) SelectStop(
        bool buy, decimal price, decimal atr, AnalysisSnapshot entry, AnalysisSnapshot confirmation)
    {
        IEnumerable<SwingPoint> swings = entry.Swings.Concat(confirmation.Swings);
        decimal? structural = buy
            ? swings.Where(s => s.Type == SwingType.Low && s.Price < price).OrderByDescending(s => s.PivotTime).Select(s => (decimal?)s.Price).FirstOrDefault()
            : swings.Where(s => s.Type == SwingType.High && s.Price > price).OrderByDescending(s => s.PivotTime).Select(s => (decimal?)s.Price).FirstOrDefault();
        if (structural is not null)
            return (buy ? structural.Value - atr * Options.StopBufferAtr : structural.Value + atr * Options.StopBufferAtr,
                "Recent confirmed swing plus ATR buffer");

        PriceChannel? channel = entry.Channels.OrderByDescending(c => c.Confidence).FirstOrDefault();
        if (channel is not null)
            return (buy ? channel.LowerLine.PriceAt(entry.AvailableAt) - atr * Options.StopBufferAtr
                        : channel.UpperLine.PriceAt(entry.AvailableAt) + atr * Options.StopBufferAtr,
                "Active channel boundary plus ATR buffer");

        return (buy ? price - atr * Options.FallbackStopAtr : price + atr * Options.FallbackStopAtr,
            "ATR fallback");
    }

    private (decimal Price, string Source) SelectTarget(
        bool buy, decimal price, decimal atr, params AnalysisSnapshot[] snapshots)
    {
        var candidates = new List<(decimal Price, int Priority, string Source)>();
        foreach (AnalysisSnapshot snapshot in snapshots)
        {
            candidates.AddRange(snapshot.PriceZones
                .Where(z => buy ? z.CentrePrice > price : z.CentrePrice < price)
                .Select(z => (z.CentrePrice, 0, $"{snapshot.Interval} price zone")));
            candidates.AddRange(snapshot.Swings
                .Where(s => buy ? s.Type == SwingType.High && s.Price > price : s.Type == SwingType.Low && s.Price < price)
                .Select(s => (s.Price, 1, $"{snapshot.Interval} confirmed swing")));
            foreach (PriceChannel channel in snapshot.Channels)
            {
                decimal boundary = buy ? channel.UpperLine.PriceAt(snapshot.AvailableAt) : channel.LowerLine.PriceAt(snapshot.AvailableAt);
                if (buy ? boundary > price : boundary < price)
                    candidates.Add((boundary, 2, $"{snapshot.Interval} channel boundary"));
            }
        }

        if (candidates.Count == 0)
        {
            return (buy ? price + atr * Options.FallbackTargetAtr : price - atr * Options.FallbackTargetAtr,
                "ATR fallback");
        }

        (decimal Price, int Priority, string Source) selected = candidates
            .OrderBy(c => Math.Abs(c.Price - price))
            .ThenBy(c => c.Priority)
            .First();
        return (selected.Price, selected.Source);
    }
}
