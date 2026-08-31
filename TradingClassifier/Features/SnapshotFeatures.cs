using ChartAnnotator.Liquidity;
using ChartAnnotator.Models;
using ChartAnnotator.Regime;
using ChartAnnotator.SupplyDemand;

namespace TradingClassifier.Features;

/// <summary>
/// Feature groups sourced from the full <see cref="AnalysisSnapshot"/> rather than from indicator
/// maths: volume behaviour, market structure, support/resistance geometry, supply-demand and
/// liquidity zones, and the volatility regime.
/// <para>
/// Same bounding discipline as <see cref="ExtendedIndicatorFeatures"/>: no raw price levels, no raw
/// volumes. Zone and swing proximity is expressed as a <b>distance in ATR</b>, counts are capped,
/// and everything else is a percentile, ratio, flag or small categorical code. §3.12c found an
/// unbounded level destroyed walk-forward performance while bounded features transported intact.
/// </para>
/// </summary>
public static class SnapshotFeatures
{
    // ---- Volume (lives on IndicatorSnapshot, so no full snapshot needed) -----------------------

    public static IReadOnlyList<string> VolumeNames { get; } =
    [
        "vol_relative_to_baseline",  // ratio vs its own median
        "vol_percentile",            // 0-100
        "vol_regime",                // categorical
        "vol_is_elevated",
        "vol_is_reliable",
        "vol_is_activity_proxy"      // TickCount rather than true traded volume
    ];

    public static void WriteVolume(float[] values, ref int cursor, IndicatorSnapshot? indicators)
    {
        VolumeAnalysisSnapshot volume = indicators?.VolumeAnalysis ?? VolumeAnalysisSnapshot.Empty;
        values[cursor++] = (float)(volume.RelativeToBaseline ?? 1m);
        values[cursor++] = (float)(volume.Percentile ?? 50m);
        values[cursor++] = (int)volume.Regime;
        values[cursor++] = volume.IsElevated ? 1f : 0f;
        values[cursor++] = volume.IsReliable ? 1f : 0f;
        values[cursor++] = volume.IsActivityProxy ? 1f : 0f;
    }

    // ---- Market structure ----------------------------------------------------------------------

    public static IReadOnlyList<string> StructureNames { get; } =
    [
        "struct_direction",
        "struct_previous_direction",
        "struct_break",
        "struct_direction_changed",
        "struct_last_high_distance_atr",
        "struct_last_low_distance_atr",
        "struct_last_swing_strength"
    ];

    public static void WriteStructure(
        float[] values, ref int cursor, AnalysisSnapshot? snapshot, decimal close, decimal atr)
    {
        MarketStructureSnapshot structure = snapshot?.MarketStructure ?? MarketStructureSnapshot.Empty;
        values[cursor++] = (int)structure.Direction;
        values[cursor++] = (int)structure.PreviousDirection;
        values[cursor++] = (int)structure.Break;
        values[cursor++] = structure.DirectionChanged ? 1f : 0f;

        SwingPoint? high = null, low = null;
        if (snapshot is not null)
        {
            for (int index = snapshot.Swings.Count - 1; index >= 0; index--)
            {
                SwingPoint swing = snapshot.Swings[index];
                if (high is null && swing.Type == SwingType.High) high = swing;
                if (low is null && swing.Type == SwingType.Low) low = swing;
                if (high is not null && low is not null) break;
            }
        }

        values[cursor++] = Distance(high?.Price, close, atr);
        values[cursor++] = Distance(low?.Price, close, atr);
        values[cursor++] = high is not null || low is not null
            ? Math.Max(high?.Strength ?? 0, low?.Strength ?? 0)
            : 0f;
    }

    // ---- Support / resistance geometry -----------------------------------------------------------

    public static IReadOnlyList<string> SupportResistanceNames { get; } =
    [
        "sr_nearest_above_atr",
        "sr_nearest_below_atr",
        "sr_zone_count",
        "sr_inside_zone",
        "sr_trendline_count",
        "sr_channel_direction"
    ];

    public static void WriteSupportResistance(
        float[] values, ref int cursor, AnalysisSnapshot? snapshot, decimal close, decimal atr)
    {
        decimal? above = null, below = null;
        bool inside = false;
        int zones = 0;

        if (snapshot is not null)
        {
            zones = snapshot.PriceZones.Count;
            foreach (PriceZone zone in snapshot.PriceZones)
            {
                if (close >= zone.LowerPrice && close <= zone.UpperPrice) inside = true;
                if (zone.LowerPrice > close && (above is null || zone.LowerPrice < above)) above = zone.LowerPrice;
                if (zone.UpperPrice < close && (below is null || zone.UpperPrice > below)) below = zone.UpperPrice;
            }
        }

        values[cursor++] = Distance(above, close, atr);
        values[cursor++] = Distance(below, close, atr);
        values[cursor++] = Bounded(zones);
        values[cursor++] = inside ? 1f : 0f;
        values[cursor++] = Bounded(snapshot?.Trendlines.Count ?? 0);
        values[cursor++] = snapshot is { Channels.Count: > 0 } ? (int)snapshot.Channels[^1].Direction : 0f;
    }

    // ---- Supply / demand zones -------------------------------------------------------------------

    public static IReadOnlyList<string> SupplyDemandNames { get; } =
    [
        "sd_enabled", "sd_active_zone_count", "sd_recent_event_count"
    ];

    public static void WriteSupplyDemand(float[] values, ref int cursor, AnalysisSnapshot? snapshot)
    {
        SupplyDemandAnalysisSnapshot sd = snapshot?.SupplyDemand ?? SupplyDemandAnalysisSnapshot.Disabled;
        values[cursor++] = sd.IsEnabled ? 1f : 0f;
        values[cursor++] = Bounded(sd.ActiveZones.Count);
        values[cursor++] = Bounded(sd.RecentEvents.Count);
    }

    // ---- Liquidity pools -------------------------------------------------------------------------

    public static IReadOnlyList<string> LiquidityNames { get; } =
    [
        "liq_enabled", "liq_active_pool_count", "liq_recent_event_count"
    ];

    public static void WriteLiquidity(float[] values, ref int cursor, AnalysisSnapshot? snapshot)
    {
        LiquidityAnalysisSnapshot liquidity = snapshot?.Liquidity ?? LiquidityAnalysisSnapshot.Disabled;
        values[cursor++] = liquidity.IsEnabled ? 1f : 0f;
        values[cursor++] = Bounded(liquidity.ActivePools.Count);
        values[cursor++] = Bounded(liquidity.RecentEvents.Count);
    }

    // ---- Volatility / market regime ---------------------------------------------------------------

    public static IReadOnlyList<string> RegimeNames { get; } =
    [
        "regime", "regime_confidence", "regime_age_candles", "regime_is_tradeable",
        "pa_bias", "pa_bullish_score", "pa_bearish_score"
    ];

    public static void WriteRegime(float[] values, ref int cursor, AnalysisSnapshot? snapshot)
    {
        MarketRegimeSnapshot regime = snapshot?.MarketRegime ?? MarketRegimeSnapshot.Unknown;
        PriceActionSnapshot action = snapshot?.PriceAction ?? PriceActionSnapshot.Empty;
        values[cursor++] = (int)regime.Regime;
        values[cursor++] = (float)regime.Confidence;
        values[cursor++] = Math.Min(regime.AgeCandles, 500);   // capped: an age is unbounded
        values[cursor++] = regime.IsTradeable ? 1f : 0f;
        values[cursor++] = (int)action.Bias;
        values[cursor++] = (float)action.BullishScore;
        values[cursor++] = (float)action.BearishScore;
    }

    /// <summary>
    /// Log-compresses an unbounded count into a bounded, still-ordered value.
    /// <para>
    /// A hard <c>Math.Min(count, 50)</c> looked bounded but saturated: liquidity recent-event counts
    /// sat at the cap on every row, so the column was constant and carried no information at all.
    /// log1p keeps small counts distinguishable while never running away, which is the property
    /// §3.12c actually requires.
    /// </para>
    /// </summary>
    private static float Bounded(int count) => (float)Math.Log(1 + Math.Max(count, 0));

    /// <summary>
    /// Distance from <paramref name="close"/> to a level, in ATR. Returns a large sentinel when the
    /// level is absent so "no zone nearby" is distinguishable from "zone at zero distance" — a plain
    /// 0 would tell the model the opposite of the truth.
    /// </summary>
    private static float Distance(decimal? level, decimal close, decimal atr)
    {
        if (level is null || atr <= 0m)
            return 99f;
        return (float)Math.Min(Math.Abs(level.Value - close) / atr, 99m);
    }
}
