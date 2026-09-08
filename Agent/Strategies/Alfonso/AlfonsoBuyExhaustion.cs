using ChartAnnotator.Models;

namespace Agent.Strategies.Alfonso;

internal sealed class AlfonsoBuyExhaustion
{
    private DateTimeOffset? _lastClose;
    private string? _currentReason;
    private string? _previousReason;

    // Keep each candle's own threshold result, never mixing OHLC/indicators across bars.
    // Only consecutive closed bars qualify; gaps must not carry an old warning forward.
    internal string? Evaluate(AnalysisSnapshot snapshot, DateTimeOffset at)
    {
        if (!IsCurrentClosed(snapshot, at) || (_lastClose is { } last && at < last))
            return null;
        if (_lastClose != at)
        {
            _previousReason = _lastClose == at.AddMinutes(-5) ? _currentReason : null;
            _currentReason = Reason(snapshot, at);
            _lastClose = at;
        }
        return _currentReason ?? _previousReason;
    }

    private static bool IsCurrentClosed(AnalysisSnapshot snapshot, DateTimeOffset at) =>
        snapshot.Interval == Brokers.Models.BarInterval.Minutes(5) && snapshot.AvailableAt <= at &&
        snapshot.LatestCandle.OpenTime.AddMinutes(5) == at;

    // Missing values cannot satisfy a threshold. One ready oscillator is enough for OR.
    internal static bool Matches(decimal high, IndicatorSnapshot indicators) =>
        indicators.BollingerUpper is decimal upper && high >= upper &&
        (indicators.Rsi is > 70m || indicators.Cci is >= 100m);

    internal static string? Reason(AnalysisSnapshot snapshot, DateTimeOffset at)
    {
        if (!IsCurrentClosed(snapshot, at) ||
            !Matches(snapshot.LatestCandle.Prices.High, snapshot.Indicators))
            return null;
        return FormattableString.Invariant($"5m buy exhaustion: candle={snapshot.LatestCandle.OpenTime:O}; high={snapshot.LatestCandle.Prices.High}; BBUpper={snapshot.Indicators.BollingerUpper}; RSI={snapshot.Indicators.Rsi}; CCI={snapshot.Indicators.Cci}.");
    }
}
