using ChartAnnotator.Models;

namespace TradingClassifier.Features;

/// <summary>
/// Extracts the derived analysis this repo's <c>ChartAnnotationEngine</c> already computes -
/// RSI/CCI zones, momentum and divergence relationships, and the Bollinger width regime - into
/// model columns.
/// <para>
/// These are worth having for a specific reason beyond "more features": almost all of them are
/// bounded, categorical or percentile-valued. PROJECT_STATE.md section 3.12c showed that the one
/// feature that destroyed walk-forward performance was an unbounded <i>level</i> (<c>atr14_pct</c>,
/// 48% of a test window outside the entire training range), while bounded features transported
/// intact. Zones, regimes, percentiles and bars-since counters are structurally the right shape.
/// </para>
/// <para>
/// Nothing here is recomputed: every value comes straight off the annotation snapshot the engine
/// already produced, which is also what the live agent receives. That satisfies the blueprint's
/// section 27 requirement of one shared implementation far better than the parallel indicator
/// maths in <see cref="FeatureEngine"/> does.
/// </para>
/// </summary>
public static class AnalysisFeatures
{
    /// <summary>Column names, in the order <see cref="Write"/> emits them.</summary>
    public static IReadOnlyList<string> Names { get; } =
    [
        // RSI
        "an_rsi_zone",
        "an_rsi_momentum_direction",
        "an_rsi_momentum_change",
        "an_rsi_divergence_bullish",
        "an_rsi_divergence_bearish",
        "an_rsi_divergence_hidden",
        "an_rsi_divergence_strength",
        "an_rsi_divergence_age",
        "an_rsi_divergence_is_new",
        // CCI
        "an_cci_zone",
        "an_cci_momentum_direction",
        "an_cci_momentum_change",
        "an_cci_crossed_up_zero",
        "an_cci_crossed_down_zero",
        "an_cci_crossed_up_from_extreme_negative",
        "an_cci_crossed_down_from_extreme_positive",
        "an_cci_bars_since_extreme_negative",
        "an_cci_bars_since_extreme_positive",
        "an_cci_divergence_bullish",
        "an_cci_divergence_bearish",
        "an_cci_divergence_strength",
        "an_cci_divergence_age",
        // Bollinger
        "an_bb_percent_b",
        "an_bb_bandwidth_percent",
        "an_bb_bandwidth_change_percent",
        "an_bb_width_percentile",
        "an_bb_width_direction",
        "an_bb_width_regime",
        "an_bb_is_squeeze",
        "an_bb_is_expansion",
        "an_bb_squeeze_released"
    ];

    public static int Count => Names.Count;

    /// <summary>
    /// Writes the analysis columns for one snapshot. A missing or still-warming-up sub-snapshot
    /// yields 0 rather than an exception: the annotation engine legitimately reports Empty during
    /// warm-up, and the feature engine's own readiness gate already withholds those rows.
    /// </summary>
    public static void Write(float[] values, ref int cursor, IndicatorSnapshot indicators)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(indicators);

        RsiAnalysisSnapshot rsi = indicators.RsiAnalysis;
        RsiRelationshipSnapshot? rsiRelationship = rsi.LatestRelationship;

        // Enums are encoded ordinally where the underlying scale is genuinely ordered
        // (Oversold -> Overbought, Contracting -> Expanding). Divergence TYPE is not ordered, so it
        // is split into indicator columns instead - an ordinal encoding there would tell the trees
        // that "hidden bearish" sits numerically between two unrelated states.
        values[cursor++] = (float)rsi.Zone;
        values[cursor++] = Direction(rsi.MomentumDirection);
        values[cursor++] = (float)(rsi.MomentumChange ?? 0m);
        values[cursor++] = Flag(rsiRelationship?.Type is RsiRelationshipType.RegularBullishDivergence
            or RsiRelationshipType.HiddenBullishDivergence or RsiRelationshipType.BullishConvergence);
        values[cursor++] = Flag(rsiRelationship?.Type is RsiRelationshipType.RegularBearishDivergence
            or RsiRelationshipType.HiddenBearishDivergence or RsiRelationshipType.BearishConvergence);
        values[cursor++] = Flag(rsiRelationship?.Type is RsiRelationshipType.HiddenBullishDivergence
            or RsiRelationshipType.HiddenBearishDivergence);
        values[cursor++] = (float)(rsiRelationship?.Strength ?? 0m);
        values[cursor++] = rsiRelationship?.AgeCandles ?? -1f;
        values[cursor++] = Flag(rsi.IsNewRelationship);

        CciAnalysisSnapshot cci = indicators.CciAnalysis;
        CciRelationshipSnapshot? cciRelationship = cci.LatestRelationship;

        values[cursor++] = (float)cci.Zone;
        values[cursor++] = Direction(cci.MomentumDirection);
        values[cursor++] = (float)(cci.MomentumChange ?? 0m);
        values[cursor++] = Flag(cci.CrossedUpZero);
        values[cursor++] = Flag(cci.CrossedDownZero);
        values[cursor++] = Flag(cci.CrossedUpFromExtremeNegative);
        values[cursor++] = Flag(cci.CrossedDownFromExtremePositive);
        values[cursor++] = cci.BarsSinceExtremeNegative;
        values[cursor++] = cci.BarsSinceExtremePositive;
        values[cursor++] = Flag(cciRelationship?.Type is CciRelationshipType.RegularBullishDivergence
            or CciRelationshipType.HiddenBullishDivergence or CciRelationshipType.BullishConvergence);
        values[cursor++] = Flag(cciRelationship?.Type is CciRelationshipType.RegularBearishDivergence
            or CciRelationshipType.HiddenBearishDivergence or CciRelationshipType.BearishConvergence);
        values[cursor++] = (float)(cciRelationship?.Strength ?? 0m);
        values[cursor++] = cciRelationship?.AgeCandles ?? -1f;

        BollingerAnalysisSnapshot bollinger = indicators.BollingerAnalysis;

        values[cursor++] = (float)(bollinger.PercentB ?? 0m);
        values[cursor++] = (float)(bollinger.BandwidthPercent ?? 0m);
        values[cursor++] = (float)(bollinger.BandwidthChangePercent ?? 0m);
        values[cursor++] = (float)(bollinger.WidthPercentile ?? 0m);
        values[cursor++] = (float)bollinger.WidthDirection;
        values[cursor++] = (float)bollinger.WidthRegime;
        values[cursor++] = Flag(bollinger.IsSqueeze);
        values[cursor++] = Flag(bollinger.IsExpansion);
        values[cursor++] = Flag(bollinger.SqueezeReleased);
    }

    /// <summary>Signed encoding so "falling" and "rising" sit either side of neutral.</summary>
    private static float Direction(MomentumDirection direction) => direction switch
    {
        MomentumDirection.Falling => -1f,
        MomentumDirection.Rising => 1f,
        MomentumDirection.Stable => 0f,
        _ => 0f
    };

    private static float Flag(bool value) => value ? 1f : 0f;
}
