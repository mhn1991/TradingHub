using Agent.Strategies.Alfonso.Zones;

namespace Agent.Strategies.Alfonso;

/// <summary>
/// A bounded execution-timeframe history. Pivots require two closed bars on either side;
/// the most extreme confirmed pivot in the lookback is used, not the latest minor pivot.
/// </summary>
internal sealed class AlfonsoStructuralStop(int lookback)
{
    private readonly List<AlfonsoBar> _bars = [];

    public void Apply(AlfonsoBar bar)
    {
        if (_bars.Count > 0 && bar.OpenTime <= _bars[^1].OpenTime)
            return;
        _bars.Add(bar);
        // Two additional bars allow confirmation at the oldest eligible anchor.
        if (_bars.Count > lookback + 2)
            _bars.RemoveAt(0);
    }

    public (decimal Stop, AlfonsoBar? Anchor) Resolve(bool buy, decimal distal, decimal padding)
    {
        AlfonsoBar? anchor = null;
        for (int i = Math.Max(2, _bars.Count - lookback); i < _bars.Count - 2; i++)
        {
            AlfonsoBar bar = _bars[i];
            bool pivot = true;
            for (int offset = -2; offset <= 2; offset++)
            {
                if (offset == 0)
                    continue;
                AlfonsoBar other = _bars[i + offset];
                if (buy ? bar.Low >= other.Low : bar.High <= other.High)
                {
                    pivot = false;
                    break;
                }
            }
            // Equal-price pivots prefer the most recent anchor for the explanation.
            if (pivot && (anchor is null || (buy ? bar.Low <= anchor.Value.Low : bar.High >= anchor.Value.High)))
                anchor = bar;
        }

        // Never move protection inside the original zone. No pivot means an explicit fallback.
        decimal boundary = anchor is null ? distal
            : buy ? Math.Min(distal, anchor.Value.Low) : Math.Max(distal, anchor.Value.High);
        return (buy ? boundary - padding : boundary + padding, anchor);
    }
}
