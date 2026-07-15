using Agent.Abstractions;
using Agent.Models;
using Brokers.Models;
using ChartAnnotator.Models;

namespace Agent.Strategies;

/// <summary>
/// A deliberately simple reference strategy. It demonstrates how an Agent consumes
/// synchronised annotations; replace its rules with your production strategy.
/// </summary>
public sealed class RuleBasedMultiTimeframeAgent : ITradingAgent
{
    private readonly BarInterval _entryInterval;
    private readonly BarInterval _confirmationInterval;
    private readonly BarInterval _trendInterval;
    private readonly decimal _quantity;
    private readonly decimal _minimumConfidence;
    private readonly RsiBollingerSignalOptions _indicatorOptions;
    private readonly ZoneVolumeSignalOptions _zoneVolumeOptions;

    public RuleBasedMultiTimeframeAgent(
        BarInterval entryInterval,
        BarInterval confirmationInterval,
        BarInterval trendInterval,
        decimal quantity,
        decimal minimumConfidence = 60m,
        RsiBollingerSignalOptions? indicatorOptions = null,
        ZoneVolumeSignalOptions? zoneVolumeOptions = null)
    {
        if (quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity));
        }

        if (!entryInterval.IsValid ||
            !confirmationInterval.IsValid ||
            !trendInterval.IsValid)
        {
            throw new ArgumentException("Every strategy interval must be valid.");
        }

        if (entryInterval == confirmationInterval ||
            entryInterval == trendInterval ||
            confirmationInterval == trendInterval ||
            BarIntervalParser.CompareDuration(entryInterval, confirmationInterval) >= 0 ||
            BarIntervalParser.CompareDuration(confirmationInterval, trendInterval) >= 0)
        {
            throw new ArgumentException(
                "Entry, confirmation, and trend intervals must be unique and ordered from finest to coarsest.");
        }

        if (minimumConfidence is < 0m or > 100m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumConfidence),
                "Minimum confidence must be from 0 to 100.");
        }

        _entryInterval = entryInterval;
        _confirmationInterval = confirmationInterval;
        _trendInterval = trendInterval;
        _quantity = quantity;
        _minimumConfidence = minimumConfidence;
        _indicatorOptions = indicatorOptions ?? new RsiBollingerSignalOptions();
        _indicatorOptions.Validate();
        _zoneVolumeOptions = zoneVolumeOptions ?? new ZoneVolumeSignalOptions();
        _zoneVolumeOptions.Validate();
        RequiredIntervals = new HashSet<BarInterval>
        {
            entryInterval,
            confirmationInterval,
            trendInterval
        };
    }

    public string Name => "Rule-based multi-timeframe agent";
    public IReadOnlySet<BarInterval> RequiredIntervals { get; }
    public BarInterval TriggerInterval => _entryInterval;
    public AgentExitManagementMode ExitManagementMode => AgentExitManagementMode.Bracket;

    public Task<AgentDecision> EvaluateAsync(
        AgentMarketContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        AnalysisSnapshot entry = context.Analysis.Get(_entryInterval);
        AnalysisSnapshot confirmation = context.Analysis.Get(_confirmationInterval);
        AnalysisSnapshot trend = context.Analysis.Get(_trendInterval);

        if (context.Positions.Any(position =>
                position.Instrument == context.Instrument && position.Quantity > 0m))
        {
            return Task.FromResult(Observe(
                context,
                "An open position already exists; bracket orders own its exit."));
        }

        if (!IsReady(entry) || !IsReady(confirmation) || !IsReady(trend))
        {
            return Task.FromResult(Observe(context, "Indicators are still warming up."));
        }

        decimal combinedConfidence =
            entry.Confidence.Total * 0.45m +
            confirmation.Confidence.Total * 0.30m +
            trend.Confidence.Total * 0.25m;

        bool bullishTrend = IsBullish(trend);
        bool bearishTrend = IsBearish(trend);
        bool bullishEntry = IsBullish(entry) && entry.Indicators.Rsi is >= 50m and < 75m;
        bool bearishEntry = IsBearish(entry) && entry.Indicators.Rsi is <= 50m and > 25m;

        if (bullishTrend && bullishEntry && IsBullish(confirmation))
        {
            RsiBollingerSignalAssessment indicators = RsiBollingerSignalPolicy.Evaluate(
                entry,
                PriceActionDirection.Bullish,
                _indicatorOptions);
            if (indicators.IsVetoed)
            {
                return Task.FromResult(Observe(
                    context,
                    $"Bullish entry was vetoed: {indicators.Explanation}"));
            }
            ZoneVolumeSignalAssessment zoneVolume = ZoneVolumeSignalPolicy.Evaluate(
                entry,
                PriceActionDirection.Bullish,
                indicators,
                _zoneVolumeOptions);
            if (zoneVolume.IsVetoed)
            {
                return Task.FromResult(Observe(
                    context,
                    $"Bullish entry was vetoed: {zoneVolume.Explanation}"));
            }

            decimal adjustedConfidence = Math.Clamp(
                combinedConfidence +
                indicators.ConfidenceAdjustment +
                zoneVolume.ConfidenceAdjustment,
                0m,
                100m);
            if (adjustedConfidence < _minimumConfidence)
            {
                return Task.FromResult(Observe(
                    context,
                    $"Bullish confidence {adjustedConfidence:F1} is below " +
                    $"{_minimumConfidence:F1}; {indicators.Explanation} " +
                    zoneVolume.Explanation));
            }

            return Task.FromResult(CreateTrade(
                context,
                AgentAction.Buy,
                adjustedConfidence,
                entry.Indicators.Atr,
                "Trend, confirmation and entry timeframes are bullish. " +
                $"Indicator context: {indicators.Explanation} " +
                $"Structure/volume context: {zoneVolume.Explanation}"));
        }

        if (bearishTrend && bearishEntry && IsBearish(confirmation))
        {
            RsiBollingerSignalAssessment indicators = RsiBollingerSignalPolicy.Evaluate(
                entry,
                PriceActionDirection.Bearish,
                _indicatorOptions);
            if (indicators.IsVetoed)
            {
                return Task.FromResult(Observe(
                    context,
                    $"Bearish entry was vetoed: {indicators.Explanation}"));
            }
            ZoneVolumeSignalAssessment zoneVolume = ZoneVolumeSignalPolicy.Evaluate(
                entry,
                PriceActionDirection.Bearish,
                indicators,
                _zoneVolumeOptions);
            if (zoneVolume.IsVetoed)
            {
                return Task.FromResult(Observe(
                    context,
                    $"Bearish entry was vetoed: {zoneVolume.Explanation}"));
            }

            decimal adjustedConfidence = Math.Clamp(
                combinedConfidence +
                indicators.ConfidenceAdjustment +
                zoneVolume.ConfidenceAdjustment,
                0m,
                100m);
            if (adjustedConfidence < _minimumConfidence)
            {
                return Task.FromResult(Observe(
                    context,
                    $"Bearish confidence {adjustedConfidence:F1} is below " +
                    $"{_minimumConfidence:F1}; {indicators.Explanation} " +
                    zoneVolume.Explanation));
            }

            return Task.FromResult(CreateTrade(
                context,
                AgentAction.Sell,
                adjustedConfidence,
                entry.Indicators.Atr,
                "Trend, confirmation and entry timeframes are bearish. " +
                $"Indicator context: {indicators.Explanation} " +
                $"Structure/volume context: {zoneVolume.Explanation}"));
        }

        if (combinedConfidence < _minimumConfidence)
        {
            return Task.FromResult(Observe(
                context,
                $"Combined confidence {combinedConfidence:F1} is below {_minimumConfidence:F1}."));
        }

        return Task.FromResult(Observe(context, "The timeframes are not aligned."));
    }

    private AgentDecision CreateTrade(
        AgentMarketContext context,
        AgentAction action,
        decimal confidence,
        decimal? atr,
        string reason)
    {
        decimal close = context.Analysis.Get(_entryInterval).LatestCandle.Prices.Close;
        decimal distance = (atr is > 0m ? atr.Value : close * 0.002m) * 1.5m;
        bool buy = action == AgentAction.Buy;

        return new AgentDecision
        {
            Action = action,
            Instrument = context.Instrument,
            SuggestedQuantity = _quantity,
            QuantityUnit = QuantityUnit.Units,
            OrderType = StandardOrderType.Market,
            ReferencePrice = close,
            StopLossPrice = buy ? close - distance : close + distance,
            TakeProfitPrice = buy ? close + distance * 2m : close - distance * 2m,
            Confidence = confidence,
            CreatedAt = context.Timestamp,
            Reason = reason
        };
    }

    private static bool IsReady(AnalysisSnapshot snapshot) =>
        snapshot.Indicators.Atr is not null &&
        snapshot.Indicators.Rsi is not null &&
        snapshot.Indicators.BollingerMiddle is not null;

    private static bool IsBullish(AnalysisSnapshot snapshot) =>
        snapshot.LatestCandle.Prices.Close > snapshot.LatestCandle.Prices.Open &&
        snapshot.LatestCandle.Prices.Close >= snapshot.Indicators.BollingerMiddle;

    private static bool IsBearish(AnalysisSnapshot snapshot) =>
        snapshot.LatestCandle.Prices.Close < snapshot.LatestCandle.Prices.Open &&
        snapshot.LatestCandle.Prices.Close <= snapshot.Indicators.BollingerMiddle;

    private static AgentDecision Observe(AgentMarketContext context, string reason) => new()
    {
        Action = AgentAction.Observe,
        Instrument = context.Instrument,
        Confidence = 0m,
        CreatedAt = context.Timestamp,
        Reason = reason
    };
}
