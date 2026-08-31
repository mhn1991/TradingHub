using TradingClassifier.Configuration;

namespace TradingClassifier.Features;

/// <summary>One column of the feature matrix: its name and the group it belongs to.</summary>
public readonly record struct FeatureDescriptor(string Name, FeatureGroups Group);

/// <summary>
/// The ordered feature list a given <see cref="ClassifierOptions"/> produces.
/// <para>
/// The blueprint's section 31 sketches <c>FeatureRow</c> as a record with one named property per
/// feature. That cannot express section 34's requirement that the periods be configuration, nor
/// section 35's ladder that turns whole groups on and off, nor section 24's ablation - all three
/// change the feature count. So the row carries a <c>float[]</c> and this schema names its
/// columns. Every feature the section 8 list specifies is still present, under exactly the names
/// section 8 uses; they are just addressed by index rather than by property.
/// </para>
/// </summary>
public sealed class FeatureSchema
{
    private readonly Dictionary<string, int> _indexByName;

    private FeatureSchema(IReadOnlyList<FeatureDescriptor> features)
    {
        Features = features;
        _indexByName = new Dictionary<string, int>(features.Count, StringComparer.Ordinal);
        for (int index = 0; index < features.Count; index++)
            _indexByName[features[index].Name] = index;
    }

    public IReadOnlyList<FeatureDescriptor> Features { get; }
    public int Count => Features.Count;
    public IReadOnlyList<string> Names => Features.Select(feature => feature.Name).ToArray();

    public int IndexOf(string name) => _indexByName.TryGetValue(name, out int index)
        ? index
        : throw new KeyNotFoundException($"Feature '{name}' is not part of this schema.");

    public bool Contains(string name) => _indexByName.ContainsKey(name);

    /// <summary>Column indices belonging to a group - section 24 ablation drops these together.</summary>
    public IReadOnlyList<int> IndicesOf(FeatureGroups group)
    {
        List<int> indices = [];
        for (int index = 0; index < Features.Count; index++)
        {
            if (Features[index].Group == group)
                indices.Add(index);
        }
        return indices;
    }

    /// <summary>
    /// A schema restricted to the given groups, preserving column order.
    /// <para>
    /// Lets a caller build one superset dataset and project each experiment rung out of it, rather
    /// than recomputing features per rung. That matters for annotation-backed groups, where every
    /// rebuild reruns the whole ChartAnnotationEngine over the candle history — a ten-rung ladder
    /// otherwise pays that cost ten times. Projection is also strictly more correct: every rung then
    /// sees byte-identical underlying values.
    /// </para>
    /// </summary>
    public FeatureSchema Restrict(FeatureGroups groups) =>
        new([.. Features.Where(feature => groups.HasFlag(feature.Group))]);

    /// <summary>Column indices kept by <see cref="Restrict"/>, in order.</summary>
    public IReadOnlyList<int> IndicesFor(FeatureGroups groups)
    {
        List<int> indices = [];
        for (int index = 0; index < Features.Count; index++)
        {
            if (groups.HasFlag(Features[index].Group))
                indices.Add(index);
        }
        return indices;
    }

    /// <summary>
    /// Builds the schema for these options. The order here is the order
    /// <see cref="FeatureEngine"/> writes values, and the two must not drift apart - the engine
    /// asserts its own write count against <see cref="Count"/> on every row.
    /// </summary>
    /// <summary>Trend-state flags paired with their timeframe, coarsest last.</summary>
    public static IReadOnlyList<(FeatureGroups Flag, int Minutes)> TrendStateFlags { get; } =
    [
        (FeatureGroups.TrendState30m, 30),
        (FeatureGroups.TrendState1h, 60),
        (FeatureGroups.TrendState2h, 120)
    ];

    /// <summary>Short timeframe label used in column names: 30 -> "30m", 120 -> "2h".</summary>
    public static string Label(int minutes) =>
        minutes % 60 == 0 ? $"{minutes / 60}h" : $"{minutes}m";

    public static FeatureSchema Create(ClassifierOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        List<FeatureDescriptor> features = [];

        void Add(string name, FeatureGroups group)
        {
            if (options.EnabledGroups.HasFlag(group))
                features.Add(new FeatureDescriptor(name, group));
        }

        // Section 8: price returns.
        foreach (int period in options.ReturnPeriods)
            Add($"return_{period}", FeatureGroups.PriceAction);

        // Section 8: candle structure.
        Add("body_pct", FeatureGroups.PriceAction);
        Add("upper_wick_pct", FeatureGroups.PriceAction);
        Add("lower_wick_pct", FeatureGroups.PriceAction);
        Add("range_pct", FeatureGroups.PriceAction);
        Add("candle_direction", FeatureGroups.PriceAction);

        // Section 6/8: position within the recent range.
        foreach (int period in options.RangePositionPeriods)
            Add($"position_range_{period}", FeatureGroups.RangePosition);

        // Section 8: trend.
        foreach (int period in options.EmaPeriods)
            Add($"close_vs_ema{period}", FeatureGroups.Trend);
        Add("ema5_vs_ema20", FeatureGroups.Trend);
        Add("ema10_vs_ema20", FeatureGroups.Trend);
        Add("ema20_vs_ema50", FeatureGroups.Trend);
        Add("ema20_slope_1", FeatureGroups.Trend);
        Add("ema20_slope_5", FeatureGroups.Trend);
        Add("ema50_slope_5", FeatureGroups.Trend);

        // Section 8: RSI.
        foreach (int period in options.RsiPeriods)
            Add($"rsi{period}", FeatureGroups.Rsi);
        Add("rsi14_change_1", FeatureGroups.Rsi);
        Add("rsi14_change_5", FeatureGroups.Rsi);

        // Section 8: CCI.
        foreach (int period in options.CciPeriods)
            Add($"cci{period}", FeatureGroups.Cci);
        Add("cci20_change_1", FeatureGroups.Cci);
        Add("cci20_change_5", FeatureGroups.Cci);

        // ATR. Section 8 lists only atr{n}_pct; the level alone told the model how volatile the
        // market is but nothing about whether that is unusual or which way it is moving, and on
        // this data it was the single most destructive group (PROJECT_STATE.md section 3.12a).
        // Regime, change and percentile are added so the group carries volatility *context*.
        foreach (int period in options.AtrPeriods)
            Add($"atr{period}_pct", FeatureGroups.Atr);
        Add("atr_regime", FeatureGroups.Atr);
        foreach (int lag in options.AtrChangeLags)
            Add($"atr_change_{lag}", FeatureGroups.Atr);
        Add($"atr_percentile_{options.AtrPercentilePeriod}", FeatureGroups.Atr);

        // Section 8: MACD.
        Add("macd", FeatureGroups.Macd);
        Add("macd_signal", FeatureGroups.Macd);
        Add("macd_histogram", FeatureGroups.Macd);
        Add("macd_histogram_change", FeatureGroups.Macd);

        // Section 8: Bollinger.
        Add("bb_position", FeatureGroups.Bollinger);
        Add("bb_width", FeatureGroups.Bollinger);

        // Beyond the blueprint: the annotation engine's derived analysis.
        foreach (string name in AnalysisFeatures.Names)
            Add(name, FeatureGroups.Analysis);

        // Annotation-engine indicators the classifier never consumed. Order here must match the
        // Write order in FeatureEngine; the cursor check at the end of Update enforces it.
        foreach (string name in ExtendedIndicatorFeatures.AdxNames)
            Add(name, FeatureGroups.Adx);
        foreach (string name in ExtendedIndicatorFeatures.StochRsiNames)
            Add(name, FeatureGroups.StochRsi);
        foreach (string name in ExtendedIndicatorFeatures.DonchianNames)
            Add(name, FeatureGroups.Donchian);
        foreach (string name in ExtendedIndicatorFeatures.EfficiencyNames)
            Add(name, FeatureGroups.Efficiency);
        foreach (string name in SnapshotFeatures.VolumeNames)
            Add(name, FeatureGroups.Volume);
        foreach (string name in SnapshotFeatures.StructureNames)
            Add(name, FeatureGroups.Structure);
        foreach (string name in SnapshotFeatures.SupportResistanceNames)
            Add(name, FeatureGroups.SupportResistance);
        foreach (string name in SnapshotFeatures.SupplyDemandNames)
            Add(name, FeatureGroups.SupplyDemand);
        foreach (string name in SnapshotFeatures.LiquidityNames)
            Add(name, FeatureGroups.Liquidity);
        foreach (string name in SnapshotFeatures.RegimeNames)
            Add(name, FeatureGroups.Regime);
        // Tagged with the specific timeframe's flag, not the union — otherwise the ablation could
        // not drop 1h while keeping 2h, and the selector could not reject one timeframe.
        foreach ((FeatureGroups flag, int minutes) in TrendStateFlags)
        {
            if (!options.EnabledGroups.HasFlag(flag))
                continue;
            foreach (string name in TrendStateFeatures.NamesFor(Label(minutes)))
                Add(name, flag);
        }

        return new FeatureSchema(features);
    }
}
