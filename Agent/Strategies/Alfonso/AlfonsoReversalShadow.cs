using Agent.Strategies.Alfonso.Zones;
using System.Text.Json.Serialization;

namespace Agent.Strategies.Alfonso;

/// <summary>Research only. Never produces orders. All levels are frozen when a sweep begins.</summary>
internal sealed class AlfonsoReversalShadow(Action<AlfonsoReversalObservation> observe)
{
    private readonly List<AlfonsoBar> _bars = [];
    private AlfonsoBar? _low;
    private AlfonsoBar? _high;
    private readonly Setup?[] _setups = new Setup?[2];
    private readonly List<Outcome> _outcomes = [];
    private int _index;

    public void Apply(AlfonsoBar bar, DateTimeOffset at)
    {
        // Snapshot previously confirmed pivots, not pivots confirmed by this same bar.
        var low = _low;
        var high = _high;
        AlfonsoBar? previous = _bars.Count == 0 ? null : _bars[^1];
        if (bar.OpenTime.AddMinutes(5) != at || (_bars.Count > 0 && bar.OpenTime <= _bars[^1].OpenTime)) return;
        _bars.Add(bar);
        if (_bars.Count > 5) _bars.RemoveAt(0);
        if (_bars.Count == 5)
        {
            var pivot = _bars[2];
            if (_bars.Where((_, i) => i != 2).All(x => x.Low > pivot.Low)) _low = pivot;
            if (_bars.Where((_, i) => i != 2).All(x => x.High < pivot.High)) _high = pivot;
        }
        _index++;
        foreach (var outcome in _outcomes.ToArray())
        {
            var s = outcome.Setup;
            decimal sign = s.Buy ? 1 : -1;
            decimal favorable = s.Buy ? bar.High : bar.Low;
            decimal adverse = s.Buy ? bar.Low : bar.High;
            outcome.Mfe = Math.Max(outcome.Mfe, sign * (favorable - outcome.Entry) / outcome.Risk);
            outcome.Mae = Math.Max(outcome.Mae, -sign * (adverse - outcome.Entry) / outcome.Risk);
            if (outcome.Result is null)
            {
                bool stop = s.Buy ? bar.Low <= s.Extreme : bar.High >= s.Extreme;
                bool target = sign * (favorable - outcome.Entry) >= outcome.Risk;
                if (stop && target) outcome.Result = "AmbiguousSameBar";
                else if (stop) outcome.Result = "StopFirst";
                else if (target) outcome.Result = "OneRFirst";
            }
            if (_index - outcome.Index >= 12)
            {
                Emit(s, at, "Outcome", outcome.Entry, outcome.Result ?? "NeitherWithin12Bars",
                    outcome.Mfe, outcome.Mae);
                _outcomes.Remove(outcome);
            }
        }

        for (int side = 0; side < 2; side++)
        {
            bool buy = side == 0;
            var setup = _setups[side];
            if (setup is not null)
            {
                if (_index - setup.Index > 12 || (buy ? bar.Low < setup.Extreme : bar.High > setup.Extreme))
                {
                    Emit(setup, at, _index - setup.Index > 12 ? "Expired" : "Invalidated");
                    _setups[side] = null;
                    continue;
                }
                if (setup.Stage == 0 && (buy ? bar.Close > setup.Level : bar.Close < setup.Level))
                {
                    setup.Stage = 1;
                    Emit(setup, at, "Reclaimed");
                }
                else if (setup.Stage == 1 && (buy ? bar.Close > setup.BreakLevel : bar.Close < setup.BreakLevel))
                {
                    setup.Stage = 2;
                    Emit(setup, at, "StructureBreak");
                }
                else if (setup.Stage == 2 && (buy ? bar.Close <= setup.BreakLevel : bar.Close >= setup.BreakLevel))
                {
                    Emit(setup, at, "RetestFailed");
                    _setups[side] = null;
                }
                else if (setup.Stage == 2 && (buy
                    ? bar.Low <= setup.BreakLevel && bar.Close > setup.BreakLevel
                    : bar.High >= setup.BreakLevel && bar.Close < setup.BreakLevel))
                {
                    Emit(setup, at, "Confirmed", bar.Close);
                    _outcomes.Add(new Outcome(setup, _index, bar.Close, Math.Abs(bar.Close - setup.Extreme)));
                    _setups[side] = null;
                }
                continue;
            }
            var anchor = buy ? low : high;
            var opposite = buy ? high : low;
            // Bound the reference age to four hours. This is a generic shadow hypothesis,
            // not a claim that every liquidity sweep is a tradeable reversal.
            if (anchor is not { } a || opposite is not { } b ||
                at - a.OpenTime > TimeSpan.FromHours(4) || at - b.OpenTime > TimeSpan.FromHours(4)) continue;
            decimal level = buy ? a.Low : a.High;
            decimal breakLevel = buy ? b.High : b.Low;
            if (previous is not { } prior || !(buy
                ? prior.Low >= level && bar.Low < level && breakLevel > level && bar.Close < breakLevel
                : prior.High <= level && bar.High > level && breakLevel < level && bar.Close > breakLevel)) continue;
            setup = new Setup(buy, _index, at, level, breakLevel, buy ? bar.Low : bar.High,
                a.OpenTime, b.OpenTime);
            _setups[side] = setup;
            Emit(setup, at, "Sweep");
            if (buy ? bar.Close > level : bar.Close < level)
            {
                setup.Stage = 1;
                Emit(setup, at, "Reclaimed");
            }
        }
    }

    private void Emit(Setup s, DateTimeOffset at, string stage, decimal? entry = null,
        string? result = null, decimal? mfe = null, decimal? mae = null) => observe(new(
            at, s.At, s.Buy ? "Buy" : "Sell", stage, s.Level, s.BreakLevel, s.Extreme,
            s.AnchorAt, s.BreakAnchorAt, entry, result, mfe, mae));

    private sealed class Setup(bool buy, int index, DateTimeOffset at, decimal level, decimal breakLevel,
        decimal extreme, DateTimeOffset anchorAt, DateTimeOffset breakAnchorAt)
    {
        public bool Buy { get; } = buy;
        public int Index { get; } = index;
        public DateTimeOffset At { get; } = at;
        public decimal Level { get; } = level;
        public decimal BreakLevel { get; } = breakLevel;
        public decimal Extreme { get; } = extreme;
        public DateTimeOffset AnchorAt { get; } = anchorAt;
        public DateTimeOffset BreakAnchorAt { get; } = breakAnchorAt;
        public int Stage { get; set; }
    }

    private sealed class Outcome(Setup setup, int index, decimal entry, decimal risk)
    {
        public Setup Setup { get; } = setup;
        public int Index { get; } = index;
        public decimal Entry { get; } = entry;
        public decimal Risk { get; } = risk;
        public decimal Mfe { get; set; }
        public decimal Mae { get; set; }
        public string? Result { get; set; }
    }
}

internal sealed record AlfonsoReversalObservation(DateTimeOffset At, DateTimeOffset SweepAt,
    string Side, string Stage, decimal SweptLevel, decimal BreakLevel, decimal StopReference,
    DateTimeOffset SwingAt, DateTimeOffset BreakSwingAt, decimal? HypotheticalEntry = null,
    string? Result = null, decimal? MfeR = null, decimal? MaeR = null);

internal sealed record AlfonsoReversalShadowRow(string Instrument, string LowerTrend,
    string? ConfirmationTrend, AlfonsoReversalObservation Observation);

[JsonSerializable(typeof(AlfonsoReversalShadowRow))]
internal partial class AlfonsoReversalShadowJsonContext : JsonSerializerContext;
