using Agent.Strategies.Alfonso.Trend;
using Agent.Strategies.Alfonso.Zones;

namespace Agent.Strategies.Alfonso;

/// <summary>Strict 2/2 pivots, available only after both right-hand 5m bars close.</summary>
internal sealed class AlfonsoFiveMinuteStructure
{
    private readonly List<AlfonsoBar> _bars = [];
    public AlfonsoBar? Low { get; private set; }
    public AlfonsoBar? High { get; private set; }
    public AlfonsoBar? Latest => _bars.Count == 0 ? null : _bars[^1];

    public bool Apply(AlfonsoBar bar, DateTimeOffset at)
    {
        if (bar.OpenTime.AddMinutes(5) != at ||
            (Latest is { } last && bar.OpenTime <= last.OpenTime)) return false;
        _bars.Add(bar);
        if (_bars.Count > 5) _bars.RemoveAt(0);
        if (_bars.Count == 5)
        {
            var pivot = _bars[2];
            if (_bars.Where((_, i) => i != 2).All(x => x.Low > pivot.Low)) Low = pivot;
            if (_bars.Where((_, i) => i != 2).All(x => x.High < pivot.High)) High = pivot;
        }
        return true;
    }

    public string? PendingInvalidation(bool buy, AlfonsoTrend? trend)
    {
        var expected = buy ? AlfonsoTrend.Uptrend : AlfonsoTrend.Downtrend;
        if (trend != expected) return $"5m trend {trend} no longer agrees with {(buy ? "buy" : "sell")}.";
        var anchor = buy ? Low : High;
        if (Latest is { } bar && anchor is { } swing &&
            (buy ? bar.Close < swing.Low : bar.Close > swing.High))
            return $"5m close {bar.Close} broke confirmed {(buy ? "low" : "high")} " +
                $"{(buy ? swing.Low : swing.High)} from {swing.OpenTime:O}.";
        return null;
    }
}
