using ChartAnnotator.Models;

namespace Agent.Strategies.Alfonso;

internal static class AlfonsoBuyExhaustion
{
    // Missing values cannot satisfy a threshold. One ready oscillator is enough for OR.
    internal static bool Matches(decimal high, IndicatorSnapshot indicators) =>
        indicators.BollingerUpper is decimal upper && high >= upper &&
        (indicators.Rsi is > 70m || indicators.Cci is >= 100m);

    internal static string? Reason(AnalysisSnapshot snapshot, DateTimeOffset at)
    {
        if (snapshot.Interval != Brokers.Models.BarInterval.Minutes(5) || snapshot.AvailableAt > at ||
            snapshot.LatestCandle.OpenTime.AddMinutes(5) != at ||
            !Matches(snapshot.LatestCandle.Prices.High, snapshot.Indicators))
            return null;
        return FormattableString.Invariant($"5m buy exhaustion: candle={snapshot.LatestCandle.OpenTime:O}; high={snapshot.LatestCandle.Prices.High}; BBUpper={snapshot.Indicators.BollingerUpper}; RSI={snapshot.Indicators.Rsi}; CCI={snapshot.Indicators.Cci}.");
    }
}
