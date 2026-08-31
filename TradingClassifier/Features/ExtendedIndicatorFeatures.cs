using ChartAnnotator.Models;

namespace TradingClassifier.Features;

/// <summary>
/// Indicator analyses the annotation engine already computes but that the classifier never
/// consumed: ADX, StochRSI, Donchian and the Kaufman efficiency ratio.
/// <para>
/// Every column here is deliberately <b>bounded</b> - a ratio, a percentile, a 0/1 flag or a small
/// categorical code. <see cref="AnalysisFeatures"/> records why: PROJECT_STATE.md §3.12c found that
/// the single feature which destroyed walk-forward performance was an unbounded <i>level</i>
/// (<c>atr14_pct</c>, with 48% of a test window outside the entire training range), while bounded
/// features transported intact. Raw prices and raw widths are therefore never emitted; they are
/// divided by ATR or the close first.
/// </para>
/// <para>
/// Each group is emitted independently so the §3.12-style ladder and ablation can measure its
/// marginal value in isolation. Nothing here is recomputed - all of it comes straight off the
/// snapshot the live agent also receives.
/// </para>
/// </summary>
public static class ExtendedIndicatorFeatures
{
    public static IReadOnlyList<string> AdxNames { get; } =
    [
        "adx",                      // 0-100, already bounded
        "adx_plus_di",
        "adx_minus_di",
        "adx_di_spread",            // +DI - -DI, the directional edge
        "adx_strength_direction",   // MomentumDirection code
        "adx_directional_bias",     // PriceActionDirection code
        "adx_is_strengthening"
    ];

    public static IReadOnlyList<string> StochRsiNames { get; } =
    [
        "stochrsi_fast",            // 0-100
        "stochrsi_slow",
        "stochrsi_spread"
    ];

    public static IReadOnlyList<string> DonchianNames { get; } =
    [
        "donchian_position",        // where close sits in the channel, 0-1
        "donchian_width_atr",       // width normalised by ATR, not raw
        "donchian_broke_upper",
        "donchian_broke_lower"
    ];

    public static IReadOnlyList<string> EfficiencyNames { get; } =
    [
        "efficiency_ratio",         // 0-1 by construction
        "efficiency_percentile",    // 0-1
        "efficiency_direction",
        "efficiency_state"
    ];

    public static void WriteAdx(float[] values, ref int cursor, IndicatorSnapshot? snapshot)
    {
        AdxAnalysisSnapshot adx = snapshot?.AdxAnalysis ?? AdxAnalysisSnapshot.Empty;
        decimal plus = adx.PlusDi ?? 0m;
        decimal minus = adx.MinusDi ?? 0m;
        values[cursor++] = (float)(adx.Adx ?? 0m);
        values[cursor++] = (float)plus;
        values[cursor++] = (float)minus;
        values[cursor++] = (float)(plus - minus);
        values[cursor++] = (int)adx.StrengthDirection;
        values[cursor++] = (int)adx.DirectionalBias;
        values[cursor++] = adx.IsTrendStrengthening ? 1f : 0f;
    }

    public static void WriteStochRsi(float[] values, ref int cursor, IndicatorSnapshot? snapshot)
    {
        StochRsiSnapshot stoch = snapshot?.StochRsi ?? StochRsiSnapshot.Empty;
        decimal fast = stoch.Fast ?? 0m;
        decimal slow = stoch.Slow ?? 0m;
        values[cursor++] = (float)fast;
        values[cursor++] = (float)slow;
        values[cursor++] = (float)(fast - slow);
    }

    public static void WriteDonchian(
        float[] values, ref int cursor, IndicatorSnapshot? snapshot, decimal close)
    {
        DonchianSnapshot donchian = snapshot?.Donchian ?? DonchianSnapshot.Empty;

        // Position inside the channel rather than the raw band prices: a level would not transport
        // across a train/test boundary on a trending instrument.
        float position = 0f;
        if (donchian.Upper is decimal upper && donchian.Lower is decimal lower && upper > lower)
            position = (float)((close - lower) / (upper - lower));

        values[cursor++] = position;
        values[cursor++] = (float)(donchian.WidthAtr ?? 0m);
        values[cursor++] = donchian.ClosedAbovePreviousUpper ? 1f : 0f;
        values[cursor++] = donchian.ClosedBelowPreviousLower ? 1f : 0f;
    }

    public static void WriteEfficiency(float[] values, ref int cursor, IndicatorSnapshot? snapshot)
    {
        EfficiencyAnalysisSnapshot efficiency = snapshot?.EfficiencyAnalysis ?? EfficiencyAnalysisSnapshot.Empty;
        values[cursor++] = (float)(snapshot?.EfficiencyRatio ?? 0m);
        values[cursor++] = (float)(efficiency.Percentile ?? 0m);
        values[cursor++] = (int)efficiency.Direction;
        values[cursor++] = (int)efficiency.State;
    }
}
