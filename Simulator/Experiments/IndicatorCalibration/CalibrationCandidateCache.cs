using System.Collections.Concurrent;

namespace Simulator.Experiments.IndicatorCalibration;

/// <summary>The scored outcome of one backtest evaluation - what a candidate cache stores and what scoring/acceptance consume.</summary>
public sealed record BacktestEvaluationResult
{
    public required int TradeCount { get; init; }
    public required decimal MedianExpectancyR { get; init; }
    public required decimal MaximumDrawdownR { get; init; }
    public required decimal ProfitFactor { get; init; }
    public required bool DataQualityValid { get; init; }

    public void Validate()
    {
        if (TradeCount < 0)
            throw new ArgumentOutOfRangeException(nameof(TradeCount));
        if (MaximumDrawdownR < 0m)
            throw new ArgumentOutOfRangeException(nameof(MaximumDrawdownR));
        if (ProfitFactor < 0m)
            throw new ArgumentOutOfRangeException(nameof(ProfitFactor));
    }
}

/// <summary>
/// Content-addressed reuse of identical backtest results (blueprint §12, "Efficiency"). Keyed on
/// the complete <see cref="BacktestEvaluationIdentity"/> - never on a partial/approximate key -
/// so a cache hit is only ever returned when every execution-affecting input is identical.
/// </summary>
public interface ICalibrationCandidateCache
{
    bool TryGet(BacktestEvaluationIdentity identity, out BacktestEvaluationResult? result);
    void Set(BacktestEvaluationIdentity identity, BacktestEvaluationResult result);
    long Hits { get; }
    long Misses { get; }
}

/// <summary>
/// Process-local, thread-safe cache. Bounded concurrency + deterministic reduction is the
/// caller's responsibility (blueprint §12/§14: "Parallel execution may improve throughput but
/// must never alter candidate selection or tie-breaking") - this cache only guarantees that
/// concurrent readers/writers for the same key never race destructively.
/// </summary>
public sealed class InMemoryCalibrationCandidateCache : ICalibrationCandidateCache
{
    private readonly ConcurrentDictionary<string, BacktestEvaluationResult> _entries = new(StringComparer.Ordinal);
    private long _hits;
    private long _misses;

    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);

    public bool TryGet(BacktestEvaluationIdentity identity, out BacktestEvaluationResult? result)
    {
        ArgumentNullException.ThrowIfNull(identity);
        string key = identity.ComputeCacheKey();
        if (_entries.TryGetValue(key, out BacktestEvaluationResult? cached))
        {
            Interlocked.Increment(ref _hits);
            result = cached;
            return true;
        }

        Interlocked.Increment(ref _misses);
        result = null;
        return false;
    }

    public void Set(BacktestEvaluationIdentity identity, BacktestEvaluationResult result)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(result);
        result.Validate();
        _entries[identity.ComputeCacheKey()] = result;
    }
}
