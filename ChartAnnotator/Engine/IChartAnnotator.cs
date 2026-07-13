using Brokers.Models;
using ChartAnnotator.Models;

namespace ChartAnnotator.Engine;

public interface IChartAnnotator
{
    ValueTask<AnalysisSnapshot> ProcessAsync(
        CandleClosedEvent candleEvent,
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
