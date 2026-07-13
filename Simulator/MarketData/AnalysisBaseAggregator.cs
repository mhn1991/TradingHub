using Brokers.Models;
using ChartAnnotator.MarketData;
using ChartAnnotator.Models;

namespace Simulator.MarketData;

/// <summary>
/// Aggregates finer execution candles into completed analysis-base candles (typically 1m).
/// Does not fabricate missing execution members.
/// </summary>
public sealed class AnalysisBaseAggregator
{
    private readonly InstrumentKey _instrument;
    private readonly BarInterval _executionInterval;
    private readonly BarInterval _analysisBaseInterval;
    private readonly BaseCandleGapPolicy _gapPolicy;
    private readonly MultiTimeframeAggregator _inner;

    public AnalysisBaseAggregator(
        InstrumentKey instrument,
        BarInterval executionInterval,
        BarInterval analysisBaseInterval,
        BaseCandleGapPolicy gapPolicy = BaseCandleGapPolicy.ResetIncompleteBuckets,
        int capacity = 2_000)
    {
        if (instrument.IsEmpty)
            throw new ArgumentException("Instrument is required.", nameof(instrument));
        if (!executionInterval.IsValid || !analysisBaseInterval.IsValid)
            throw new ArgumentException("Intervals must be valid.");
        if (BarIntervalParser.CompareDuration(executionInterval, analysisBaseInterval) > 0)
            throw new ArgumentException("Execution interval must be <= analysis base.");

        _instrument = instrument;
        _executionInterval = executionInterval;
        _analysisBaseInterval = analysisBaseInterval;
        _gapPolicy = gapPolicy;

        // When execution == analysis base, multi-TF can still include the base once;
        // for execution-only aggregation we only target the analysis base.
        _inner = new MultiTimeframeAggregator(
            instrument,
            [analysisBaseInterval],
            capacity,
            gapPolicy);
    }

    public BarInterval ExecutionInterval => _executionInterval;
    public BarInterval AnalysisBaseInterval => _analysisBaseInterval;

    /// <summary>
    /// Feeds one completed execution candle. Returns zero or one completed analysis-base candle.
    /// </summary>
    public IReadOnlyList<Candle> ApplyExecutionCandle(Candle executionCandle)
    {
        ArgumentNullException.ThrowIfNull(executionCandle);
        if (executionCandle.Instrument != _instrument)
            throw new ArgumentException("Instrument mismatch.", nameof(executionCandle));
        if (executionCandle.Interval != _executionInterval)
        {
            throw new ArgumentException(
                $"Expected execution interval {_executionInterval}, got {executionCandle.Interval}.",
                nameof(executionCandle));
        }

        // Fast path: execution candle is already the analysis base.
        if (_executionInterval == _analysisBaseInterval)
            return [executionCandle];

        IReadOnlyList<CandleClosedEvent> closed = _inner.Apply(executionCandle);
        return closed
            .Where(e => e.Interval == _analysisBaseInterval)
            .Select(e => e.Candle)
            .ToArray();
    }
}
