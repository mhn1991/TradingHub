using Brokers.Models;
using ChartAnnotator.Models;

namespace ChartAnnotator.Engine;

/// <summary>
/// Resolved execution/data-quality signals for the current frame, sourced from the
/// broker/execution layer (not derivable by the generic annotator on its own). Passing
/// this lets <see cref="ChartAnnotator.Regime.MarketRegimeClassifier"/> see the same
/// spread/data-quality picture as <c>TradingConditionFilter</c>, instead of always
/// defaulting to "spread unknown, data quality OK".
/// </summary>
public sealed record AnalysisRuntimeContext
{
    public decimal? ExecutableSpread { get; init; }
    public bool DataQualityOk { get; init; } = true;
    public IReadOnlyList<string> DataQualityIssueCodes { get; init; } = [];
}

public interface IChartAnnotator
{
    ValueTask<AnalysisSnapshot> ProcessAsync(
        CandleClosedEvent candleEvent,
        AnalysisRuntimeContext? runtimeContext,
        CancellationToken cancellationToken = default);

    AnalysisSnapshot? GetLatest(
        InstrumentKey instrument,
        BarInterval interval);

    IReadOnlyList<Candle> GetCandles(
        InstrumentKey instrument,
        BarInterval interval);

    IReadOnlyList<IndicatorPoint> GetIndicatorHistory(
        InstrumentKey instrument,
        BarInterval interval);
}


public interface ICalibratableChartAnnotator
{
    void FreezeCalibration(DateTimeOffset frozenAt);
}
