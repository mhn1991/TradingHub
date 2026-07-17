using Agent.Abstractions;
using Agent.Models;
using Brokers.Models;
using ChartAnnotator.Models;

namespace Agent.Strategies;

/// <summary>Reproduces the old progressive 1h -> 15m -> 5m behaviour. It uses a safety stop,
/// has no fixed profit target, and closes the position when the scoped analysis reverses.</summary>
public sealed class LegacyProgressiveAgent(ProgressiveStrategyOptions? options = null)
    : ProgressiveStrategyBase(options)
{
    public override string Name => "Legacy progressive reverse-exit";

    public override AgentExitManagementMode ExitManagementMode =>
        AgentExitManagementMode.ProtectiveStopAndStrategyExit;

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
        PriceActionSetup? paSetup = priceActionSetup ??
            entry.PriceAction.GetBestTriggeredSetup(
                buy ? PriceActionDirection.Bullish : PriceActionDirection.Bearish,
                Options.MinimumPriceActionConfidence);
        decimal stop;
        string stopSource;
        if (paSetup?.ReferenceLevel is decimal setupLevel &&
            (buy ? setupLevel < price : setupLevel > price))
        {
            stop = buy
                ? setupLevel - atr * Options.StopBufferAtr
                : setupLevel + atr * Options.StopBufferAtr;
            stopSource = $"PA setup {paSetup.Type} reference plus ATR buffer";
        }
        else
        {
            stop = buy ? price - atr * Options.FallbackStopAtr : price + atr * Options.FallbackStopAtr;
            stopSource = "Entry ATR safety stop";
        }

        if ((buy && stop >= price) || (!buy && stop <= price))
        {
            return Observe(context, "No valid structural invalidation level was available.") with
            {
                ReasonCode = "InvalidStopSide"
            };
        }

        if (Math.Abs(price - stop) <= 0m)
        {
            return Observe(context, "Entry risk collapsed to zero after stop selection.") with
            {
                ReasonCode = "ZeroRisk"
            };
        }

        decimal confidence = entry.Confidence.Total * 0.45m + confirmation.Confidence.Total * 0.30m + trend.Confidence.Total * 0.25m +
            PriceActionConfidenceAdjustment(entry, state.Side) +
            ActiveRetestConfidenceAdjustment(entry, state.Side);
        string setupNote = paSetup is null
            ? PriceActionSummary(entry, state.Side)
            : $"{paSetup.Type} ({paSetup.Confidence:F0})";
        return Trade(context, state, buy ? AgentAction.Buy : AgentAction.Sell, confidence,
            price, stop, null,
            $"Progressive higher-timeframe partial setup confirmed on the entry timeframe; " +
            $"price action: {setupNote}; exit waits for reversal.",
            stopSource, "Reverse setup exit");
    }

    protected override AgentDecision EvaluateOpenPosition(
        AgentMarketContext context, BrokerPosition position, AnalysisSnapshot trend,
        AnalysisSnapshot confirmation, AnalysisSnapshot entry)
    {
        bool longPosition = position.Side == OrderSide.Buy;
        bool reverse = longPosition
            ? DetectSide(trend, Options.MinimumTrendConfidence) == SetupSide.Sell &&
              DetectSide(confirmation, Options.MinimumConfirmationConfidence) == SetupSide.Sell
            : DetectSide(trend, Options.MinimumTrendConfidence) == SetupSide.Buy &&
              DetectSide(confirmation, Options.MinimumConfirmationConfidence) == SetupSide.Buy;
        return reverse
            ? Close(context, position, Math.Min(trend.Confidence.Total, confirmation.Confidence.Total),
                "Opposite trend and confirmation setup closed the legacy position.")
            : Observe(context, "Legacy position remains open until its safety stop or a confirmed reverse setup.");
    }
}
