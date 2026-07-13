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
        AgentMarketContext context, ScopeState state, AnalysisSnapshot trend,
        AnalysisSnapshot confirmation, AnalysisSnapshot entry)
    {
        decimal price = entry.LatestCandle.Prices.Close;
        decimal atr = entry.Indicators.Atr ?? price * 0.002m;
        bool buy = state.Side == SetupSide.Buy;
        decimal stop = buy ? price - atr * Options.FallbackStopAtr : price + atr * Options.FallbackStopAtr;
        decimal confidence = entry.Confidence.Total * 0.45m + confirmation.Confidence.Total * 0.30m + trend.Confidence.Total * 0.25m;
        return Trade(context, state, buy ? AgentAction.Buy : AgentAction.Sell, confidence,
            price, stop, null,
            "Progressive higher-timeframe partial setup confirmed on the entry timeframe; exit waits for reversal.",
            "Entry ATR safety stop", "Reverse setup exit");
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
