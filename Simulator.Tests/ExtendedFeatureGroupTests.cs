using Brokers.Models;
using ChartAnnotator.Models;
using NUnit.Framework;
using TradingClassifier.Configuration;
using TradingClassifier.Features;

namespace Simulator.Tests;

/// <summary>
/// The schema declares column names and the engine writes values; they are two lists that must stay
/// in the same order. FeatureEngine.Update throws when the cursor and schema disagree, but only for
/// the combination actually exercised — so every new group is checked here individually and
/// together.
/// </summary>
[TestFixture]
public sealed class ExtendedFeatureGroupTests
{
    private static readonly FeatureGroups[] NewGroups =
    [
        FeatureGroups.Adx, FeatureGroups.StochRsi, FeatureGroups.Donchian, FeatureGroups.Efficiency,
        FeatureGroups.Volume, FeatureGroups.Structure, FeatureGroups.SupportResistance,
        FeatureGroups.SupplyDemand, FeatureGroups.Liquidity, FeatureGroups.Regime,
        FeatureGroups.TrendState30m, FeatureGroups.TrendState1h, FeatureGroups.TrendState2h
    ];

    [Test]
    public void EveryNewGroup_DeclaresColumnsAndIsIndependentlySelectable()
    {
        Assert.Multiple(() =>
        {
            foreach (FeatureGroups group in NewGroups)
            {
                FeatureSchema schema = FeatureSchema.Create(new ClassifierOptions { EnabledGroups = group });
                Assert.That(schema.Count, Is.GreaterThan(0), $"{group} declares no columns.");
                Assert.That(schema.Names, Is.Unique, $"{group} has duplicate column names.");
            }
        });
    }

    [Test]
    public void NewGroups_RequireTheAnnotationSnapshot()
    {
        // Without this the DatasetBuilder path would emit zeros for every column and the ablation
        // would price the group at exactly nothing — a silent false negative.
        Assert.Multiple(() =>
        {
            foreach (FeatureGroups group in NewGroups)
            {
                // TrendState is the one exception, and deliberately so: it is fed higher-timeframe
                // state by the caller, not read off the annotation snapshot, so it sits on a
                // separate axis (RequiresTrendState) and must NOT force the expensive path.
                if (FeatureEngine.RequiresTrendState(group))
                {
                    Assert.That(FeatureEngine.RequiresTrendState(group), Is.True, $"{group}");
                    continue;
                }
                Assert.That(FeatureEngine.RequiresAnnotation(group), Is.True, $"{group}");
            }

            Assert.That(FeatureEngine.RequiresAnnotation(FeatureGroups.Experiment3), Is.False,
                "OHLC/EMA/RSI/CCI must keep the cheap non-annotation path.");
            Assert.That(FeatureEngine.RequiresTrendState(FeatureGroups.Experiment3), Is.False);
        });
    }

    [Test]
    public void SchemaAndWriter_AgreeForEveryGroupAndForTheFullSet()
    {
        FeatureGroups[] cases = [.. NewGroups, FeatureGroups.ExtendedIndicators,
            FeatureGroups.Experiment6 | FeatureGroups.ExtendedIndicators];

        Assert.Multiple(() =>
        {
            foreach (FeatureGroups groups in cases)
            {
                ClassifierOptions options = new() { EnabledGroups = groups };
                FeatureEngine engine = new(options);
                FeatureVector? vector = null;

                // Warm the engine past its longest lookback, feeding a snapshot every bar.
                for (int index = 0; index < 300; index++)
                {
                    decimal close = 100m + (index % 7) * 0.5m;
                    ClassifierCandle bar = new(
                        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(index),
                        close, close + 1m, close - 1m, close);

                    // Snapshot-backed groups must go through the full-snapshot overload; the
                    // indicator-only one cannot supply swings, zones, pools or regime.
                    vector = FeatureEngine.RequiresTrendState(groups)
                        ? engine.Update(bar, FullSnapshot(), TrendStatistics.Detection.TrendState.Neutral)
                        : FeatureEngine.RequiresFullSnapshot(groups)
                            ? engine.Update(bar, FullSnapshot())
                            : engine.Update(bar, Snapshot());
                }

                Assert.That(vector, Is.Not.Null, $"{groups} never produced a vector.");
                Assert.That(vector!.Values, Has.Length.EqualTo(engine.Schema.Count),
                    $"{groups}: writer and schema disagree.");
                Assert.That(vector.Values, Has.None.Matches<float>(float.IsNaN), $"{groups} emitted NaN.");
                Assert.That(vector.Values, Has.None.Matches<float>(float.IsInfinity), $"{groups} emitted Inf.");
            }
        });
    }

    [Test]
    public void EveryLadderRung_AddsColumnsOverThePrevious()
    {
        // A rung that silently contributes zero columns is invisible in the results table except as
        // a suspiciously identical row. That is exactly what happened when the ladder projected from
        // FeatureGroups.All (which stops at Experiment5) instead of Everything: rungs 6-10 all
        // reported 44 features and identical PnL.
        (string Name, FeatureGroups Groups)[] ladder =
        [
            ("5 + MACD/BB", FeatureGroups.Experiment5),
            ("6 + Analysis", FeatureGroups.Experiment6),
            ("7 + ADX", FeatureGroups.Experiment6 | FeatureGroups.Adx),
            ("8 + StochRSI", FeatureGroups.Experiment6 | FeatureGroups.Adx | FeatureGroups.StochRsi),
            ("9 + Donchian", FeatureGroups.Experiment6 | FeatureGroups.Adx | FeatureGroups.StochRsi |
                FeatureGroups.Donchian),
            ("11 + Volume", FeatureGroups.Experiment6 | FeatureGroups.ExtendedIndicators),
            ("12 + Structure", FeatureGroups.Experiment6 | FeatureGroups.ExtendedIndicators |
                FeatureGroups.Structure),
            ("16 + Regime", FeatureGroups.Everything)
        ];

        Assert.Multiple(() =>
        {
            int previous = 0;
            string previousName = "(none)";
            foreach ((string name, FeatureGroups groups) in ladder)
            {
                int count = FeatureSchema.Create(new ClassifierOptions { EnabledGroups = groups }).Count;
                Assert.That(count, Is.GreaterThan(previous),
                    $"Rung '{name}' adds no columns over '{previousName}'.");
                previous = count;
                previousName = name;
            }
        });
    }

    [Test]
    public void Everything_ContainsEveryOtherGroup()
    {
        FeatureGroups[] all =
        [
            FeatureGroups.PriceAction, FeatureGroups.RangePosition, FeatureGroups.Trend,
            FeatureGroups.Rsi, FeatureGroups.Cci, FeatureGroups.Atr, FeatureGroups.Macd,
            FeatureGroups.Bollinger, FeatureGroups.Analysis, FeatureGroups.Adx,
            FeatureGroups.StochRsi, FeatureGroups.Donchian, FeatureGroups.Efficiency,
            FeatureGroups.Volume, FeatureGroups.Structure, FeatureGroups.SupportResistance,
            FeatureGroups.SupplyDemand, FeatureGroups.Liquidity, FeatureGroups.Regime,
            FeatureGroups.TrendState30m, FeatureGroups.TrendState1h, FeatureGroups.TrendState2h
        ];

        Assert.Multiple(() =>
        {
            foreach (FeatureGroups group in all)
            {
                Assert.That(FeatureGroups.Everything.HasFlag(group), Is.True,
                    $"{group} is missing from Everything, so the ladder superset would drop it.");
            }
        });
    }

    [Test]
    public void TrendStateGroup_IsNeutralUntilTheHigherTimeframeSuppliesState()
    {
        // The join is only meaningful if a row that has seen no CLOSED higher-timeframe candle gets
        // the neutral state rather than a peek at one. V2 §4.3 and §6.2 both call out backfilling
        // completed-trend direction into earlier rows as the leak to avoid, so "no state yet" must
        // be representable and must be what an un-fed engine produces.
        ClassifierOptions options = new()
        {
            EnabledGroups = FeatureGroups.Experiment1 | FeatureGroups.TrendState2h
        };
        FeatureSchema schema = FeatureSchema.Create(options);
        FeatureEngine engine = new(options);

        FeatureVector? vector = null;
        for (int index = 0; index < 300; index++)
        {
            decimal close = 100m + (index % 7) * 0.5m;
            vector = engine.Update(
                new ClassifierCandle(
                    new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(index),
                    close, close + 1m, close - 1m, close),
                FullSnapshot(),
                trendState: null);
        }

        // Columns are per-timeframe now (default [120] -> "2h"), so a multi-timeframe cascade
        // can coexist in one vector.
        int direction = schema.IndexOf("ht2h_direction");
        int phase = schema.IndexOf("ht2h_phase");

        Assert.Multiple(() =>
        {
            Assert.That(vector, Is.Not.Null);
            Assert.That(vector!.Values[direction], Is.Zero, "No trend state must read as no direction.");
            Assert.That(vector.Values[phase], Is.Zero, "No trend state must read as the Neutral phase.");
            Assert.That(FeatureEngine.RequiresTrendState(FeatureGroups.TrendState2h), Is.True);
            Assert.That(FeatureEngine.RequiresTrendState(FeatureGroups.Experiment3), Is.False);
        });
    }

    [Test]
    public void TrendStateGroup_ReflectsASuppliedState()
    {
        ClassifierOptions options = new()
        {
            EnabledGroups = FeatureGroups.Experiment1 | FeatureGroups.TrendState2h
        };
        FeatureSchema schema = FeatureSchema.Create(options);
        FeatureEngine engine = new(options);

        TrendStatistics.Detection.TrendState bullish = new()
        {
            Phase = TrendStatistics.Detection.TrendPhase.Confirmed,
            Direction = TrendStatistics.Detection.TrendDirection.Bullish,
            CurrentMovePct = 4.2m,
            DurationBars = 9
        };

        FeatureVector? vector = null;
        for (int index = 0; index < 300; index++)
        {
            decimal close = 100m + (index % 7) * 0.5m;
            vector = engine.Update(
                new ClassifierCandle(
                    new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(index),
                    close, close + 1m, close - 1m, close),
                FullSnapshot(),
                bullish);
        }

        Assert.Multiple(() =>
        {
            Assert.That(vector!.Values[schema.IndexOf("ht2h_direction")], Is.EqualTo(1f));
            Assert.That(vector.Values[schema.IndexOf("ht2h_is_confirmed")], Is.EqualTo(1f));
            Assert.That(vector.Values[schema.IndexOf("ht2h_is_late")], Is.Zero);
            Assert.That(vector.Values[schema.IndexOf("ht2h_move_pct")], Is.EqualTo(4.2f).Within(1e-4));
        });
    }

    private static AnalysisSnapshot FullSnapshot() => new()
    {
        Instrument = new InstrumentKey("METAL:XAU/USD"),
        Interval = BarInterval.Minutes(15),
        AvailableAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        Version = 1,
        LatestCandle = TestCandles.Create(
            new InstrumentKey("METAL:XAU/USD"),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            BarInterval.Minutes(15), 100m, 101m, 99m, 100m),
        Indicators = Snapshot(),
        Swings = [],
        PriceZones = [],
        Trendlines = [],
        Channels = [],
        Confidence = new ConfidenceScore { Total = 70m, Contributions = [] }
    };

    private static IndicatorSnapshot Snapshot() => new()
    {
        Atr = 1m,
        Rsi = 55m,
        Cci = 40m,
        BollingerUpper = 105m,
        BollingerMiddle = 100m,
        BollingerLower = 95m,
        EfficiencyRatio = 0.4m,
        StochRsi = new StochRsiSnapshot { Fast = 60m, Slow = 55m },
        Donchian = new DonchianSnapshot
        {
            Upper = 106m, Lower = 94m, Middle = 100m, Width = 12m, WidthAtr = 12m
        },
        AdxAnalysis = new AdxAnalysisSnapshot { Adx = 25m, PlusDi = 22m, MinusDi = 18m },
        VolumeAnalysis = new VolumeAnalysisSnapshot
        {
            Value = 400m, RelativeToBaseline = 1.1m, Percentile = 55m, SampleCount = 40
        },
        EfficiencyAnalysis = new EfficiencyAnalysisSnapshot { Percentile = 0.5m, SampleCount = 40 }
    };
}
