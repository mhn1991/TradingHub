using TrendStatistics.Detection;

namespace TradingClassifier.Features;

/// <summary>
/// Cross-timeframe features: the higher-timeframe <see cref="TrendState"/> joined onto each
/// lower-timeframe row. This is the ML catalogue's Phase 4 item and the composition feature for the
/// trend-gated agent.
/// <para>
/// It is the one genuinely untested idea in this repo's ML surface. Every negative result so far —
/// the standalone classifier (§3.12g), the meta-filter gate (§3.15) and the direct-R regression
/// (§3.18) — used a <b>single-timeframe</b> model. The higher-timeframe state that produced the only
/// validated edge here (§2.18, 2H PF 1.694 walk-forward) has never been shown to a classifier.
/// </para>
/// <para>
/// CAUSALITY: these columns are only meaningful if the state was produced by replaying the detector
/// incrementally over <i>closed</i> higher-timeframe candles. Generating completed trend segments
/// over the full history and backfilling their direction, endpoints or percentiles into earlier rows
/// would leak the outcome into the features — the failure V2 §4.3 and §6.2 both warn about. The
/// builder owns that guarantee; this class only formats what it is handed.
/// </para>
/// <para>
/// Bounded, like every other group: categorical codes, flags, log-compressed durations and
/// ATR-normalised distances. Never a raw price or an unbounded bar count (§3.12c).
/// </para>
/// </summary>
public static class TrendStateFeatures
{
    private static readonly string[] Suffixes =
    [
        "direction",              // +1 bullish, -1 bearish, 0 none
        "phase",                  // Neutral/Candidate/Confirmed/Mature/Exhaustion
        "is_confirmed",
        "is_late",                // Mature or Exhaustion — blueprint §43's "be more selective"
        "move_pct",
        "duration_bars",          // log1p
        "duration_hours",         // log1p
        "from_start_atr",         // how far price has travelled from the structural start
        "from_extreme_atr",       // give-back from the favourable extreme
        "volatility_at_confirmation"
    ];

    /// <summary>Columns per timeframe, so several trend timeframes can coexist in one vector.</summary>
    public static IReadOnlyList<string> NamesFor(string label) =>
        [.. Suffixes.Select(suffix => $"ht{label}_{suffix}")];

    /// <summary>Column count emitted per timeframe.</summary>
    public static int WidthPerTimeframe => Suffixes.Length;

    public static void Write(float[] values, ref int cursor, TrendState? state, decimal close, decimal atr)
    {
        TrendState trend = state ?? TrendState.Neutral;

        values[cursor++] = trend.Direction switch
        {
            TrendDirection.Bullish => 1f,
            TrendDirection.Bearish => -1f,
            _ => 0f
        };
        values[cursor++] = (int)trend.Phase;
        values[cursor++] = trend.Phase == TrendPhase.Confirmed ? 1f : 0f;
        values[cursor++] = trend.Phase is TrendPhase.Mature or TrendPhase.Exhaustion ? 1f : 0f;

        // A percentage move is already scale-free, but clamp it so one runaway trend cannot dominate
        // the split points.
        values[cursor++] = (float)Math.Clamp(trend.CurrentMovePct, -100m, 100m);

        values[cursor++] = (float)Math.Log(1 + Math.Max(trend.DurationBars, 0));
        values[cursor++] = (float)Math.Log(1 + Math.Max((double)trend.DurationHours, 0));

        values[cursor++] = Distance(trend.StructuralStartPrice, close, atr);
        values[cursor++] = Distance(trend.FavorableExtremePrice, close, atr);
        values[cursor++] = (float)Math.Clamp(trend.VolatilityPctAtConfirmation, -100m, 100m);
    }

    /// <summary>
    /// Signed distance in ATR, clamped. Returns 0 when the reference price is absent — meaning
    /// "no trend anchor", which for a signed displacement is the honest neutral value.
    /// </summary>
    private static float Distance(decimal? reference, decimal close, decimal atr)
    {
        if (reference is null || atr <= 0m)
            return 0f;
        return (float)Math.Clamp((close - reference.Value) / atr, -50m, 50m);
    }
}
