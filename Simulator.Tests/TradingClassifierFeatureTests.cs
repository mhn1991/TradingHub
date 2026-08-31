using NUnit.Framework;
using TradingClassifier.Configuration;
using TradingClassifier.Dataset;
using TradingClassifier.Features;
using TradingClassifier.Indicators;
using TradingClassifier.Labels;
using TradingClassifierRunner;

namespace Simulator.Tests;

/// <summary>
/// Feature-side coverage for the Trading Classification Model V1 blueprint: the indicator maths,
/// the section 25 look-ahead guarantee, and the section 26 warm-up/horizon trimming.
/// </summary>
[TestFixture]
public sealed class TradingClassifierFeatureTests
{
    private static IReadOnlyList<ClassifierCandle> Series(int count, int seed = 3)
    {
        Random random = new(seed);
        List<ClassifierCandle> candles = [];
        decimal price = 100m;
        DateTimeOffset timestamp = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

        for (int index = 0; index < count; index++)
        {
            decimal open = price;
            decimal close = Math.Round(open + (decimal)((random.NextDouble() - 0.5) * 1.5), 4);
            decimal high = Math.Max(open, close) + (decimal)(random.NextDouble() * 0.4);
            decimal low = Math.Min(open, close) - (decimal)(random.NextDouble() * 0.4);
            candles.Add(new ClassifierCandle(timestamp, open, Math.Round(high, 4), Math.Round(low, 4), close));
            price = close;
            timestamp = timestamp.AddMinutes(5);
        }

        return candles;
    }

    [Test]
    public void Ema_SeedsWithSimpleAverageThenSmooths()
    {
        EmaState ema = new(3);
        foreach (decimal close in (decimal[])[2m, 4m, 6m])
            ema.Update(close);

        // Seeded with the SMA of the first three closes.
        Assert.That(ema.IsReady, Is.True);
        Assert.That(ema.Current, Is.EqualTo(4m));

        // Then multiplier 2/(3+1) = 0.5: 4 + (10 - 4) * 0.5 = 7.
        ema.Update(10m);
        Assert.That(ema.Current, Is.EqualTo(7m));
    }

    [Test]
    public void Rsi_IsOneHundredWhenEveryChangeIsAGain()
    {
        RsiState rsi = new(3);
        foreach (decimal close in (decimal[])[1m, 2m, 3m, 4m, 5m])
            rsi.Update(close);

        Assert.That(rsi.IsReady, Is.True);
        Assert.That(rsi.Current, Is.EqualTo(100m));
    }

    [Test]
    public void Atr_SeedsWithTheAverageTrueRange()
    {
        AtrState atr = new(2);
        atr.Update(10m, 8m, 9m);    // no previous close: true range = 2
        atr.Update(11m, 9m, 10m);   // max(2, |11-9|, |9-9|) = 2
        Assert.That(atr.IsReady, Is.True);
        Assert.That(atr.Current, Is.EqualTo(2m));
    }

    [Test]
    public void Cci_IsZeroOnAPerfectlyFlatWindow()
    {
        CciState cci = new(3);
        for (int index = 0; index < 3; index++)
            cci.Update(10m, 10m, 10m);

        // Zero mean deviation would divide by zero; the guard reports no deviation from the mean.
        Assert.That(cci.IsReady, Is.True);
        Assert.That(cci.Current, Is.EqualTo(0m));
    }

    [Test]
    public void LaggedValue_ReturnsTheValueFromNUpdatesAgo()
    {
        LaggedValue lagged = new(2);
        lagged.Update(1m);
        lagged.Update(2m);
        Assert.That(lagged.IsReady, Is.False);
        lagged.Update(3m);
        Assert.That(lagged.IsReady, Is.True);
        Assert.That(lagged.Previous, Is.EqualTo(1m));
        lagged.Update(4m);
        Assert.That(lagged.Previous, Is.EqualTo(2m));
    }

    [Test]
    public void RollingPercentile_RanksTheNewestValueInItsWindow()
    {
        RollingPercentileState percentile = new(4);
        foreach (decimal value in (decimal[])[1m, 2m, 3m, 4m])
            percentile.Update(value);

        // 4 is the highest of {1,2,3,4}: all four are at or below it.
        Assert.That(percentile.IsReady, Is.True);
        Assert.That(percentile.Current, Is.EqualTo(1m));

        // Window is now {2,3,4,0}; only 0 is at or below 0.
        percentile.Update(0m);
        Assert.That(percentile.Current, Is.EqualTo(0.25m));
    }

    [Test]
    public void AtrGroup_CarriesRegimeChangeAndPercentileNotJustLevel()
    {
        FeatureSchema schema = FeatureSchema.Create(new ClassifierOptions());

        Assert.Multiple(() =>
        {
            foreach (string expected in (string[])
                ["atr_regime", "atr_change_1", "atr_change_5", "atr_percentile_100"])
            {
                Assert.That(schema.Contains(expected), Is.True, $"missing feature {expected}");
            }
            // The raw level columns are gone by default: they do not survive a volatility regime
            // shift (PROJECT_STATE.md section 3.12c) and atr_percentile replaces them.
            Assert.That(schema.Contains("atr14_pct"), Is.False);
            Assert.That(schema.Contains("atr20_pct"), Is.False);
            Assert.That(schema.IndicesOf(FeatureGroups.Atr), Has.Count.EqualTo(4));
        });
    }

    [Test]
    public void AtrRegime_IsAboveOneWhenShortTermVolatilityExpands()
    {
        // A long quiet stretch then a violent one: ATR14 must run hot against its ATR50 baseline.
        ClassifierOptions options = new() { EnabledGroups = FeatureGroups.Atr | FeatureGroups.PriceAction };
        FeatureEngine engine = new(options);
        FeatureSchema schema = engine.Schema;

        FeatureVector? last = null;
        DateTimeOffset timestamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        decimal price = 100m;

        for (int index = 0; index < 260; index++)
        {
            // Calm for 200 bars, then a 60-bar expansion an order of magnitude wider.
            decimal range = index < 200 ? 0.10m : 1.00m;
            decimal close = price + (index % 2 == 0 ? range / 4 : -range / 4);
            FeatureVector? row = engine.Update(new ClassifierCandle(
                timestamp, price, Math.Max(price, close) + range, Math.Min(price, close) - range, close));
            if (row is not null)
                last = row;
            price = close;
            timestamp = timestamp.AddMinutes(5);
        }

        Assert.That(last, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(last!.Values[schema.IndexOf("atr_regime")], Is.GreaterThan(1f),
                "ATR14 should exceed its ATR50 baseline inside an expansion");
            Assert.That(last.Values[schema.IndexOf("atr_percentile_100")], Is.GreaterThan(0.5f),
                "current ATR should rank high in its own recent window");
        });
    }

    [Test]
    public void AtrChange_IsZeroWhenVolatilityIsFlatAndPositiveWhenItRises()
    {
        ClassifierOptions options = new() { EnabledGroups = FeatureGroups.Atr | FeatureGroups.PriceAction };
        FeatureSchema schema = FeatureSchema.Create(options);

        float ChangeAfter(bool expand)
        {
            FeatureEngine engine = new(options);
            FeatureVector? last = null;
            DateTimeOffset timestamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

            for (int index = 0; index < 260; index++)
            {
                decimal range = expand && index >= 255 ? 5m : 0.5m;
                FeatureVector? row = engine.Update(new ClassifierCandle(
                    timestamp, 100m, 100m + range, 100m - range, 100m));
                if (row is not null)
                    last = row;
                timestamp = timestamp.AddMinutes(5);
            }

            return last!.Values[schema.IndexOf("atr_change_5")];
        }

        Assert.That(ChangeAfter(expand: false), Is.EqualTo(0f).Within(1e-4f));
        Assert.That(ChangeAfter(expand: true), Is.GreaterThan(0.1f));
    }

    [Test]
    public void Options_RejectAnAtrBasePeriodMissingFromAtrPeriods()
    {
        // Levels can still be opted back in, which must restore the two extra columns.
        ClassifierOptions withLevels = new() { AtrPeriods = [14, 20] };
        withLevels.Validate();
        Assert.That(FeatureSchema.Create(withLevels).IndicesOf(FeatureGroups.Atr), Has.Count.EqualTo(6));

        Assert.That(() => new ClassifierOptions { AtrBasePeriod = 50, AtrRegimeSlowPeriod = 50 }.Validate(),
            Throws.ArgumentException.With.Message.Contains("must exceed AtrBasePeriod"));
    }

    [Test]
    public void Schema_MatchesTheSectionEightFeatureList()
    {
        FeatureSchema schema = FeatureSchema.Create(new ClassifierOptions());

        // Section 8's list comes to 42. The ATR group now contributes 4 columns instead of 2:
        // regime + two changes + percentile, with the two non-transportable atr{n}_pct levels
        // removed (see ClassifierOptions.AtrPeriods). 42 - 2 + 4 = 44.
        Assert.That(schema.Count, Is.EqualTo(44));
        Assert.Multiple(() =>
        {
            foreach (string expected in (string[])
                [
                    "return_1", "return_20", "body_pct", "upper_wick_pct", "lower_wick_pct",
                    "range_pct", "candle_direction", "position_range_20", "close_vs_ema20",
                    "ema5_vs_ema20", "ema20_vs_ema50", "ema20_slope_5", "ema50_slope_5",
                    "rsi7", "rsi14", "rsi21", "rsi14_change_1", "rsi14_change_5",
                    "cci14", "cci20", "cci50", "cci20_change_5",
                    "atr_regime", "atr_change_5", "atr_percentile_100",
                    "macd", "macd_signal", "macd_histogram", "macd_histogram_change",
                    "bb_position", "bb_width"
                ])
            {
                Assert.That(schema.Contains(expected), Is.True, $"missing feature {expected}");
            }
        });
    }

    [Test]
    public void ExperimentLadder_AddsFeaturesMonotonically()
    {
        int Count(FeatureGroups groups) =>
            FeatureSchema.Create(new ClassifierOptions { EnabledGroups = groups }).Count;

        // Section 35 must be a ladder: each rung strictly adds to the one below it.
        Assert.That(Count(FeatureGroups.Experiment1), Is.LessThan(Count(FeatureGroups.Experiment2)));
        Assert.That(Count(FeatureGroups.Experiment2), Is.LessThan(Count(FeatureGroups.Experiment3)));
        Assert.That(Count(FeatureGroups.Experiment3), Is.LessThan(Count(FeatureGroups.Experiment4)));
        Assert.That(Count(FeatureGroups.Experiment4), Is.LessThan(Count(FeatureGroups.Experiment5)));
        Assert.That(Count(FeatureGroups.Experiment5), Is.EqualTo(Count(FeatureGroups.All)));
    }

    /// <summary>
    /// Section 25, stated as a property: a feature row for candle t must be byte-identical whether
    /// or not the series continues past t. If any indicator could see forward, the longer series
    /// would produce a different row.
    /// </summary>
    [Test]
    public void Features_DoNotChangeWhenLaterCandlesExist()
    {
        IReadOnlyList<ClassifierCandle> full = Series(400);
        IReadOnlyList<ClassifierCandle> prefix = full.Take(300).ToArray();

        FeatureVector[] fromFull = Run(full).Take(prefix.Count).ToArray();
        FeatureVector[] fromPrefix = Run(prefix).ToArray();

        Assert.That(fromPrefix, Is.Not.Empty);
        Assert.That(fromFull.Length, Is.GreaterThanOrEqualTo(fromPrefix.Length));

        for (int index = 0; index < fromPrefix.Length; index++)
        {
            Assert.That(fromFull[index].Timestamp, Is.EqualTo(fromPrefix[index].Timestamp));
            Assert.That(fromFull[index].Values, Is.EqualTo(fromPrefix[index].Values),
                $"row {index} at {fromPrefix[index].Timestamp:O} differs once later candles exist");
        }

        static List<FeatureVector> Run(IReadOnlyList<ClassifierCandle> candles)
        {
            FeatureEngine engine = new(new ClassifierOptions());
            List<FeatureVector> rows = [];
            foreach (ClassifierCandle candle in candles)
            {
                if (engine.Update(candle) is FeatureVector vector)
                    rows.Add(vector);
            }
            return rows;
        }
    }

    [Test]
    public void FeatureEngine_RejectsOutOfOrderCandles()
    {
        FeatureEngine engine = new(new ClassifierOptions());
        IReadOnlyList<ClassifierCandle> candles = Series(5);
        foreach (ClassifierCandle candle in candles)
            engine.Update(candle);

        Assert.That(() => engine.Update(candles[0]),
            Throws.ArgumentException.With.Message.Contains("strictly increasing"));
    }

    [Test]
    public void FeatureEngine_EmitsNothingUntilEveryIndicatorIsWarm()
    {
        FeatureEngine engine = new(new ClassifierOptions());
        IReadOnlyList<ClassifierCandle> candles = Series(200);
        int firstRow = -1;

        for (int index = 0; index < candles.Count; index++)
        {
            if (engine.Update(candles[index]) is not null)
            {
                firstRow = index;
                break;
            }
        }

        // The slowest chain is MACD: EMA26 then a 9-period signal EMA on top, so nothing can be
        // emitted before roughly 34 candles.
        Assert.That(firstRow, Is.GreaterThanOrEqualTo(33));
        Assert.That(firstRow, Is.LessThan(candles.Count));
    }

    [Test]
    public void Label_UsesTheAtrScaledThresholdOnFutureClose()
    {
        ClassifierOptions options = new() { PredictionHorizon = 2, AtrTargetMultiplier = 0.75m };
        AtrThresholdLabelGenerator generator = new(options);

        ClassifierCandle[] candles =
        [
            new(Time(0), 100m, 100m, 100m, 100m),
            new(Time(1), 100m, 100m, 100m, 100m),
            new(Time(2), 100m, 100m, 100m, 110m)
        ];

        // ATR 10 => threshold 7.5. A +10 close clears it.
        var buy = generator.Label(candles, 0, atr: 10m);
        Assert.That(buy!.Value.Label, Is.EqualTo(TradeLabel.Buy));

        // ATR 20 => threshold 15. The same +10 close does not.
        var flat = generator.Label(candles, 0, atr: 20m);
        Assert.That(flat!.Value.Label, Is.EqualTo(TradeLabel.NoTrade));
    }

    [Test]
    public void Label_IsNullWhenTheHorizonRunsPastTheData()
    {
        AtrThresholdLabelGenerator generator = new(new ClassifierOptions { PredictionHorizon = 5 });
        ClassifierCandle[] candles = [.. Series(3)];
        Assert.That(generator.Label(candles, 0, 1m), Is.Null);
    }

    [Test]
    public void MaximumExcursionLabel_TreatsATwoSidedWindowAsNoTrade()
    {
        ClassifierOptions options = new()
        {
            PredictionHorizon = 2,
            AtrTargetMultiplier = 0.5m,
            UseMaximumExcursionLabels = true
        };
        AtrThresholdLabelGenerator generator = new(options);

        // Threshold 5 with ATR 10. The window runs 20 up AND 20 down: which came first is not
        // knowable from candle extremes, so section 12's target must decline to guess.
        ClassifierCandle[] whipsaw =
        [
            new(Time(0), 100m, 100m, 100m, 100m),
            new(Time(1), 100m, 120m, 100m, 110m),
            new(Time(2), 110m, 110m, 80m, 90m)
        ];
        Assert.That(generator.Label(whipsaw, 0, 10m)!.Value.Label, Is.EqualTo(TradeLabel.NoTrade));

        // One-sided: only the upside clears.
        ClassifierCandle[] clean =
        [
            new(Time(0), 100m, 100m, 100m, 100m),
            new(Time(1), 100m, 120m, 99m, 110m),
            new(Time(2), 110m, 115m, 99m, 112m)
        ];
        Assert.That(generator.Label(clean, 0, 10m)!.Value.Label, Is.EqualTo(TradeLabel.Buy));
    }

    [Test]
    public void DatasetBuilder_DropsWarmUpAndUnlabelledTailRows()
    {
        ClassifierOptions options = new() { PredictionHorizon = 10 };
        ClassifierDataset dataset = new DatasetBuilder(options).Build(Series(500));

        Assert.That(dataset.Rows, Is.Not.Empty);
        Assert.That(dataset.Rows.Count, Is.LessThan(500));
        Assert.Multiple(() =>
        {
            foreach (var row in dataset.Rows)
                Assert.That(row.Features.Values, Has.Length.EqualTo(dataset.Schema.Count));
        });

        // Every row must have had a candle a full horizon ahead of it.
        Assert.That(dataset.Rows[^1].Features.Timestamp,
            Is.LessThanOrEqualTo(Series(500)[^1].Timestamp.AddMinutes(-5 * options.PredictionHorizon)));
    }

    [Test]
    public void Embargo_RemovesTheHorizonSpanningRowsFromTrain()
    {
        ClassifierOptions options = new() { PredictionHorizon = 10 };
        ClassifierDataset dataset = new DatasetBuilder(options).Build(Series(2000));

        DatasetSplit none = DatasetSplitter.Chronological(dataset.Rows, 0.6, 0.2, embargoRows: 0);
        DatasetSplit embargoed = DatasetSplitter.Chronological(dataset.Rows, 0.6, 0.2, embargoRows: 10);

        Assert.That(embargoed.Train.Count, Is.EqualTo(none.Train.Count - 10));
        Assert.That(embargoed.Validation.Count, Is.EqualTo(none.Validation.Count - 10));
        // The test slice is never embargoed: it is what gets reported on.
        Assert.That(embargoed.Test.Count, Is.EqualTo(none.Test.Count));
        // The gap is what matters: train must now end strictly before validation begins.
        Assert.That(embargoed.Train[^1].Features.Timestamp,
            Is.LessThan(embargoed.Validation[0].Features.Timestamp));
    }

    [Test]
    public void WalkForward_ProducesNonOverlappingTestPeriods()
    {
        ClassifierOptions options = new();
        ClassifierDataset dataset = new DatasetBuilder(options).Build(Series(20000));

        IReadOnlyList<DatasetSplit> windows = DatasetSplitter.WalkForward(
            dataset.Rows, TimeSpan.FromDays(10), TimeSpan.FromDays(3), TimeSpan.FromDays(3),
            embargoRows: options.PredictionHorizon);

        Assert.That(windows.Count, Is.GreaterThan(1));
        for (int index = 1; index < windows.Count; index++)
        {
            // Section 16 rolls the frame forward by one test span, so consecutive test periods tile
            // rather than overlap - otherwise the pooled result double-counts the same candles.
            Assert.That(windows[index].Test[0].Features.Timestamp,
                Is.GreaterThan(windows[index - 1].Test[^1].Features.Timestamp));
            Assert.That(windows[index].Train[0].Features.Timestamp,
                Is.GreaterThan(windows[index - 1].Train[0].Features.Timestamp));
        }
    }

    private static DateTimeOffset Time(int index) =>
        new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(5 * index);
}

/// <summary>
/// Timeframe aggregation. The close-time bucketing is the part that matters: getting it wrong by
/// one bucket hands every feature a window of hindsight, and nothing downstream would notice.
/// </summary>
[TestFixture]
public sealed class TradingClassifierResampleTests
{
    private static ClassifierCandle Minute(int minute, decimal open, decimal high, decimal low, decimal close) =>
        new(new DateTimeOffset(2026, 1, 5, 3, 0, 0, TimeSpan.Zero).AddMinutes(minute), open, high, low, close);

    [Test]
    public void Resample_FoldsFiveOneMinuteBarsIntoOneFiveMinuteBar()
    {
        // Close times 03:11..03:15 all belong to the 5m bar that CLOSES at 03:15.
        ClassifierCandle[] source =
        [
            Minute(11, 10m, 12m, 9m, 11m),
            Minute(12, 11m, 15m, 10m, 14m),
            Minute(13, 14m, 14m, 8m, 9m),
            Minute(14, 9m, 11m, 9m, 10m),
            Minute(15, 10m, 13m, 7m, 12m)
        ];

        IReadOnlyList<ClassifierCandle> folded = CandleSources.Resample(source, 5);

        Assert.That(folded, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(folded[0].Timestamp.Minute, Is.EqualTo(15), "stamped with the bar's close time");
            Assert.That(folded[0].Open, Is.EqualTo(10m), "first open");
            Assert.That(folded[0].High, Is.EqualTo(15m), "highest high");
            Assert.That(folded[0].Low, Is.EqualTo(7m), "lowest low");
            Assert.That(folded[0].Close, Is.EqualTo(12m), "last close");
        });
    }

    [Test]
    public void Resample_DropsAnIncompleteTrailingBucket()
    {
        // 03:16 and 03:17 open the next 5m bar but do not close it; that bar's high and low are
        // still forming, so emitting it would put an unknowable candle at the end of every run.
        ClassifierCandle[] source =
        [
            Minute(11, 10m, 12m, 9m, 11m), Minute(12, 11m, 12m, 9m, 11m), Minute(13, 11m, 12m, 9m, 11m),
            Minute(14, 11m, 12m, 9m, 11m), Minute(15, 11m, 12m, 9m, 11m),
            Minute(16, 11m, 20m, 9m, 19m), Minute(17, 19m, 21m, 18m, 20m)
        ];

        IReadOnlyList<ClassifierCandle> folded = CandleSources.Resample(source, 5);

        Assert.That(folded, Has.Count.EqualTo(1));
        Assert.That(folded[0].High, Is.EqualTo(12m), "the incomplete bucket's 21 high must not leak in");
    }

    [Test]
    public void Resample_ProducesStrictlyIncreasingTimestampsTheFeatureEngineAccepts()
    {
        List<ClassifierCandle> source = [];
        decimal price = 100m;
        for (int minute = 1; minute <= 600; minute++)
        {
            decimal close = price + (minute % 3 == 0 ? 0.3m : -0.1m);
            source.Add(Minute(minute, price, Math.Max(price, close) + 0.2m, Math.Min(price, close) - 0.2m, close));
            price = close;
        }

        foreach (int minutes in (int[])[5, 15, 60])
        {
            IReadOnlyList<ClassifierCandle> folded = CandleSources.Resample(source, minutes);
            Assert.That(folded, Is.Not.Empty, $"{minutes}m produced no candles");

            for (int index = 1; index < folded.Count; index++)
            {
                Assert.That(folded[index].Timestamp, Is.GreaterThan(folded[index - 1].Timestamp));
                Assert.That(folded[index].Timestamp - folded[index - 1].Timestamp,
                    Is.EqualTo(TimeSpan.FromMinutes(minutes)), $"{minutes}m bars must be evenly spaced");
            }

            // Aggregation must never invent or lose range.
            Assert.That(folded.Max(c => c.High), Is.EqualTo(source.Max(c => c.High)));
            Assert.That(folded.Min(c => c.Low), Is.EqualTo(source.Min(c => c.Low)));
        }
    }
}

/// <summary>
/// The Analysis feature group: columns sourced from the annotation engine rather than recomputed.
/// </summary>
[TestFixture]
public sealed class TradingClassifierAnalysisFeatureTests
{
    [Test]
    public void Schema_AddsTheAnalysisColumnsOnlyWhenTheGroupIsEnabled()
    {
        FeatureSchema without = FeatureSchema.Create(new ClassifierOptions { EnabledGroups = FeatureGroups.Experiment5 });
        FeatureSchema with = FeatureSchema.Create(new ClassifierOptions { EnabledGroups = FeatureGroups.Experiment6 });

        Assert.Multiple(() =>
        {
            Assert.That(without.Contains("an_rsi_divergence_bullish"), Is.False);
            Assert.That(with.Contains("an_rsi_divergence_bullish"), Is.True);
            Assert.That(with.Contains("an_bb_is_squeeze"), Is.True);
            Assert.That(with.Contains("an_cci_bars_since_extreme_negative"), Is.True);
            Assert.That(with.Count - without.Count, Is.EqualTo(AnalysisFeatures.Count));
            // Every analysis column is attributed to the group, so ablation can drop them together.
            Assert.That(with.IndicesOf(FeatureGroups.Analysis), Has.Count.EqualTo(AnalysisFeatures.Count));
        });
    }

    [Test]
    public void FeatureEngine_RefusesTheAnalysisGroupWithoutASnapshot()
    {
        // Silently emitting zeros would train a model on columns that are always zero and then
        // score it live against real values - the failure would surface only in production.
        FeatureEngine engine = new(new ClassifierOptions { EnabledGroups = FeatureGroups.Experiment6 });

        Assert.That(() => engine.Update(new ClassifierCandle(
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), 100m, 101m, 99m, 100m)),
            Throws.TypeOf<ArgumentNullException>().With.Message.Contains("AnnotationDatasetBuilder"));
    }

    [Test]
    public void AnalysisFeatures_WriteExactlyItsDeclaredColumnCount()
    {
        float[] values = new float[AnalysisFeatures.Count + 4];
        int cursor = 0;

        // An entirely empty snapshot is what the annotation engine reports during warm-up; it must
        // produce zeros/sentinels rather than throwing.
        AnalysisFeatures.Write(values, ref cursor, new ChartAnnotator.Models.IndicatorSnapshot());

        Assert.That(cursor, Is.EqualTo(AnalysisFeatures.Count));
        Assert.That(values.Take(cursor).All(float.IsFinite), Is.True);
    }

    [Test]
    public void DivergenceTypeIsSplitIntoIndicatorsNotOrdinallyEncoded()
    {
        // Direction and hidden-ness are separate axes. An ordinal encoding of the seven-member
        // relationship enum would place "HiddenBearish" numerically between unrelated states and
        // invite the trees to split on a meaningless ordering.
        Assert.Multiple(() =>
        {
            Assert.That(AnalysisFeatures.Names, Does.Contain("an_rsi_divergence_bullish"));
            Assert.That(AnalysisFeatures.Names, Does.Contain("an_rsi_divergence_bearish"));
            Assert.That(AnalysisFeatures.Names, Does.Contain("an_rsi_divergence_hidden"));
            Assert.That(AnalysisFeatures.Names, Does.Not.Contain("an_rsi_divergence_type"));
        });
    }
}

/// <summary>
/// The two methodology fixes: overlapping labels in training, and a threshold tuner that could
/// select a configuration trading almost every bar.
/// </summary>
[TestFixture]
public sealed class TradingClassifierMethodologyTests
{
    private static IReadOnlyList<LabeledFeatureRow> Rows(int count)
    {
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return [.. Enumerable.Range(0, count).Select(index => new LabeledFeatureRow
        {
            Features = new FeatureVector
            {
                Timestamp = start.AddMinutes(15 * index),
                Close = 100m + index,
                LabelAtr = 1m,
                Values = [index]
            },
            Label = (TradeLabel)(index % 3),
            LabelExcursionAtr = 0m
        })];
    }

    [Test]
    public void EffectiveSampleSize_IsFarBelowTheRowCountWhenLabelsOverlap()
    {
        IReadOnlyList<LabeledFeatureRow> rows = Rows(1000);

        double effective = SampleUniqueness.EffectiveSampleSize(rows, horizon: 10);

        // 1000 rows whose labels each span 10 bars carry roughly 100 independent observations.
        Assert.That(effective, Is.LessThan(rows.Count / 5.0),
            "overlapping labels must not be counted as independent samples");
        Assert.That(effective, Is.GreaterThan(rows.Count / 20.0));
    }

    [Test]
    public void Uniqueness_IsUniformOnAContiguousDatasetWhichIsWhyStrideIsTheFix()
    {
        double[] uniqueness = SampleUniqueness.Compute(Rows(500), horizon: 10);

        // Every interior row scores the same, so weighting by uniqueness cancels out entirely -
        // this test exists to stop anyone "fixing" the overlap with weights alone.
        double[] interior = [.. uniqueness.Skip(50).Take(400)];
        Assert.That(interior.Max() - interior.Min(), Is.LessThan(0.01),
            "uniqueness is uniform here, so it cannot differentiate samples");
    }

    [Test]
    public void Stride_ProducesNonOverlappingLabels()
    {
        IReadOnlyList<LabeledFeatureRow> rows = Rows(1000);

        IReadOnlyList<LabeledFeatureRow> strided = SampleUniqueness.Stride(rows, stride: 10);

        Assert.That(strided, Has.Count.EqualTo(100));
        Assert.Multiple(() =>
        {
            // Consecutive kept rows are 10 bars apart, so with horizon 10 their label windows abut
            // rather than overlap.
            for (int index = 1; index < strided.Count; index++)
            {
                TimeSpan gap = strided[index].Features.Timestamp - strided[index - 1].Features.Timestamp;
                Assert.That(gap, Is.EqualTo(TimeSpan.FromMinutes(150)));
            }
            // Effective sample size should now be close to the row count.
            Assert.That(SampleUniqueness.EffectiveSampleSize(strided, 1),
                Is.EqualTo(strided.Count).Within(1));
        });
        Assert.That(SampleUniqueness.Stride(rows, 1), Has.Count.EqualTo(1000), "stride 1 is a no-op");
    }

    [Test]
    public void Options_RejectANonPositiveTrainingStride()
    {
        Assert.That(() => new ClassifierOptions { TrainingStride = 0 }.Validate(),
            Throws.TypeOf<ArgumentOutOfRangeException>());
        new ClassifierOptions { TrainingStride = 10 }.Validate();
    }
}
