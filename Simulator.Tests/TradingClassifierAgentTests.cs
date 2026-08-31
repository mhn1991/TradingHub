using Agent.Abstractions;
using Agent.Configuration;
using Agent.Factories;
using Agent.Models;
using Agent.Strategies.TradingClassification;
using Brokers.Models;
using ChartAnnotator.Models;
using NUnit.Framework;
using TradingClassifier.Configuration;
using TradingClassifier.Dataset;
using TradingClassifier.Evaluation;
using TradingClassifier.Features;
using TradingClassifier.ML.Experiments;
using TradingClassifier.ML.Training;
using TradingClassifier.Models;
using TradingClassifier.Signals;

namespace Simulator.Tests;

/// <summary>
/// Decision-layer, evaluation and agent-integration coverage for the Trading Classification Model
/// V1 blueprint.
/// </summary>
[TestFixture]
public sealed class TradingClassifierAgentTests
{
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");
    private static readonly BarInterval Signal = BarInterval.Minutes(5);
    private static readonly DateTimeOffset Start = new(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);

    // ---- section 19 / 20 / 33: the decision layer -------------------------------------------

    [Test]
    public void SignalGenerator_RefusesTheArgmaxClassBelowThreshold()
    {
        // Section 19's worked example verbatim: BUY 0.41 / NO_TRADE 0.35 / SELL 0.24. Argmax says
        // BUY; the trading system must say NO TRADE.
        ConfidenceSignalGenerator generator = new(new ClassifierOptions());
        Prediction prediction = new() { BuyProbability = 0.41, NoTradeProbability = 0.35, SellProbability = 0.24 };

        Assert.That(prediction.MostLikely, Is.EqualTo(TradeLabel.Buy));
        Assert.That(generator.Generate(prediction).Action, Is.EqualTo(TradeLabel.NoTrade));
    }

    [Test]
    public void SignalGenerator_FiresOnceTheThresholdIsCleared()
    {
        ConfidenceSignalGenerator generator = new(new ClassifierOptions());

        Assert.That(generator.Generate(new Prediction
        {
            BuyProbability = 0.79, NoTradeProbability = 0.14, SellProbability = 0.07
        }).Action, Is.EqualTo(TradeLabel.Buy));

        Assert.That(generator.Generate(new Prediction
        {
            BuyProbability = 0.05, NoTradeProbability = 0.15, SellProbability = 0.80
        }).Action, Is.EqualTo(TradeLabel.Sell));
    }

    [Test]
    public void SignalGenerator_TakesNeitherSideWhenBothClearTheirThresholds()
    {
        ConfidenceSignalGenerator generator = new(new ClassifierOptions
        {
            BuyProbabilityThreshold = 0.3,
            SellProbabilityThreshold = 0.3
        });

        TradingSignal signal = generator.Generate(new Prediction
        {
            BuyProbability = 0.4, NoTradeProbability = 0.2, SellProbability = 0.4
        });

        Assert.That(signal.Action, Is.EqualTo(TradeLabel.NoTrade));
        Assert.That(signal.Reason, Does.Contain("ambiguous"));
    }

    // ---- section 21: metrics ------------------------------------------------------------------

    [Test]
    public void Evaluator_FlagsAModelThatOnlyEverPredictsTheMajorityClass()
    {
        // Section 21's scenario: 75% NO_TRADE, and a model that always answers NO_TRADE.
        TradeLabel[] actual =
        [
            .. Enumerable.Repeat(TradeLabel.NoTrade, 75),
            .. Enumerable.Repeat(TradeLabel.Buy, 13),
            .. Enumerable.Repeat(TradeLabel.Sell, 12)
        ];
        Prediction[] predictions = [.. Enumerable.Repeat(
            new Prediction { SellProbability = 0, NoTradeProbability = 1, BuyProbability = 0 }, 100)];

        ClassificationReport report = ClassificationEvaluator.Evaluate(actual, predictions);

        Assert.Multiple(() =>
        {
            Assert.That(report.Accuracy, Is.EqualTo(0.75).Within(1e-9));
            Assert.That(report.IsNoBetterThanMajority, Is.True);
            // The point of section 21: high accuracy, and no BUY or SELL recall whatsoever.
            Assert.That(report.For(TradeLabel.Buy).Recall, Is.Zero);
            Assert.That(report.For(TradeLabel.Sell).Recall, Is.Zero);
            Assert.That(report.MacroF1, Is.LessThan(0.3));
        });
    }

    [Test]
    public void Backtester_CostsTurnAMarginalWinnerIntoALoser()
    {
        ClassifierOptions options = new() { PredictionHorizon = 1, BuyProbabilityThreshold = 0.5 };
        LabeledFeatureRow[] rows =
        [
            Row(Start, 100m), Row(Start.AddMinutes(5), 100.5m), Row(Start.AddMinutes(10), 101m)
        ];
        Prediction[] predictions = [.. Enumerable.Repeat(
            new Prediction { SellProbability = 0.1, NoTradeProbability = 0.2, BuyProbability = 0.7 }, 3)];

        TradingReport free = ClassifierBacktester.Run(rows, predictions, options);
        TradingReport costed = ClassifierBacktester.Run(rows, predictions, options,
            new TradingCostModel { HalfSpread = 0.2m, CommissionPerTrade = 0.1m, SlippagePerSide = 0.05m });

        // Section 22: a classifier that looks good and loses money after costs is not useful.
        Assert.That(free.NetProfit, Is.GreaterThan(0m));
        Assert.That(costed.NetProfit, Is.LessThan(0m));
        Assert.That(costed.Trades, Is.EqualTo(free.Trades));
    }

    // ---- section 13/14: the ML.NET round trip -------------------------------------------------

    [Test]
    public void LightGbm_LearnsASeparableTargetAndKeepsTheClassOrder()
    {
        // A deliberately learnable series: a strong, persistent uptrend labels almost everything
        // BUY. This does not test predictive skill - it tests that the pipeline wires the label
        // key order, the score vector and the Prediction fields together correctly, which a
        // silently transposed mapping would break while still "training fine".
        ClassifierDataset dataset = new DatasetBuilder(new ClassifierOptions { PredictionHorizon = 5 })
            .Build(Trending(1200));

        using TrainedModel model = new LightGbmModelTrainer(
            new LightGbmTrainingOptions { NumberOfIterations = 40, MinimumExampleCountPerLeaf = 5 })
            .Train(dataset.Rows, dataset.Schema);

        Prediction prediction = model.Model.Predict(dataset.Rows[^1].Features);
        double total = prediction.BuyProbability + prediction.SellProbability + prediction.NoTradeProbability;

        Assert.Multiple(() =>
        {
            Assert.That(total, Is.EqualTo(1.0).Within(1e-3), "probabilities should form a distribution");
            Assert.That(model.Model.FeatureNames, Is.EqualTo(dataset.Schema.Names));
            // The final bars sit in a falling regime, so the model should not be calling BUY there.
            // A transposed score vector is exactly what would make it do so.
            Assert.That(prediction.MostLikely, Is.Not.EqualTo(TradeLabel.Buy));
        });
    }

    [Test]
    public void SavedModel_RoundTripsThroughDisk()
    {
        ClassifierDataset dataset = new DatasetBuilder(new ClassifierOptions { PredictionHorizon = 5 })
            .Build(Trending(600));
        using TrainedModel trained = new LightGbmModelTrainer(
            new LightGbmTrainingOptions { NumberOfIterations = 20, MinimumExampleCountPerLeaf = 5 })
            .Train(dataset.Rows, dataset.Schema);

        ClassifierOptions options = new() { PredictionHorizon = 5 };
        string path = Path.Combine(Path.GetTempPath(), $"tc-{Guid.NewGuid():N}.zip");
        try
        {
            trained.Save(path, options, candleIntervalMinutes: 5, instrument: "TEST",
                trainedFrom: dataset.Start, trainedTo: dataset.End, trainingRows: dataset.Rows.Count);

            var (reloaded, metadata) = TradingClassifier.ML.Inference.MlNetTradingModel.Load(path, options, 5);
            using (reloaded)
            {
                Prediction before = trained.Model.Predict(dataset.Rows[^1].Features);
                Prediction after = reloaded.Predict(dataset.Rows[^1].Features);

                Assert.Multiple(() =>
                {
                    Assert.That(after.BuyProbability, Is.EqualTo(before.BuyProbability).Within(1e-6));
                    Assert.That(after.SellProbability, Is.EqualTo(before.SellProbability).Within(1e-6));
                    Assert.That(metadata.CandleIntervalMinutes, Is.EqualTo(5));
                    Assert.That(metadata.PredictionHorizon, Is.EqualTo(5));
                });
            }

            // Loading the same model under a different timeframe must be refused.
            Assert.That(() => TradingClassifier.ML.Inference.MlNetTradingModel.Load(path, options, 15),
                Throws.InvalidOperationException.With.Message.Contains("interval"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            string sidecar = Path.ChangeExtension(path, null) + ".metadata.json";
            if (File.Exists(sidecar)) File.Delete(sidecar);
        }
    }

    // ---- the agent ----------------------------------------------------------------------------

    [Test]
    public void Agent_RejectsAModelTrainedOnADifferentFeatureSet()
    {
        TradingClassificationStrategyOptions options = new();
        // A model that only knows the experiment-1 columns cannot score the full schema.
        NoTradeModel wrong = new(FeatureSchema.Create(
            new ClassifierOptions { EnabledGroups = FeatureGroups.Experiment1 }).Names);

        Assert.That(() => new TradingClassificationAgent(options, wrong),
            Throws.ArgumentException.With.Message.Contains("must match exactly"));
    }

    [Test]
    public async Task Agent_ObservesThroughWarmUpThenAsksTheModel()
    {
        TradingClassificationStrategyOptions options = new();
        AlwaysBuyModel model = new(FeatureSchema.Create(options.Classifier).Names);
        TradingClassificationAgent agent = new(options, model);

        AgentDecision? entry = null;
        int observed = 0;
        IReadOnlyList<ClassifierCandle> candles = Trending(200);

        foreach (ClassifierCandle candle in candles)
        {
            AgentDecision decision = await agent.EvaluateAsync(ContextFor(candle));
            if (decision.Action == AgentAction.Observe)
            {
                observed++;
                continue;
            }
            entry = decision;
            break;
        }

        Assert.That(observed, Is.GreaterThan(30), "the feature engine must warm up before signalling");
        Assert.That(entry, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(entry!.Action, Is.EqualTo(AgentAction.Buy));
            // Section 32's probability becomes the decision confidence rather than a fixed number.
            Assert.That(entry.Confidence, Is.EqualTo(90m).Within(0.001m));
            Assert.That(entry.StopLossPrice!.Value, Is.LessThan(entry.ReferencePrice!.Value));
            Assert.That(entry.TakeProfitPrice!.Value, Is.GreaterThan(entry.ReferencePrice!.Value));
        });
    }

    [Test]
    public async Task Agent_WithNoTrainedModelObservesForever()
    {
        TradingClassificationStrategyOptions options = new();
        TradingClassificationAgent agent = new(
            options, new NoTradeModel(FeatureSchema.Create(options.Classifier).Names));

        foreach (ClassifierCandle candle in Trending(200))
        {
            AgentDecision decision = await agent.EvaluateAsync(ContextFor(candle));
            Assert.That(decision.Action, Is.EqualTo(AgentAction.Observe));
        }
    }

    [Test]
    public async Task Agent_IgnoresARepeatedCandleInsteadOfCorruptingItsState()
    {
        TradingClassificationStrategyOptions options = new();
        AlwaysBuyModel model = new(FeatureSchema.Create(options.Classifier).Names);
        TradingClassificationAgent agent = new(options, model);

        IReadOnlyList<ClassifierCandle> candles = Trending(120);
        foreach (ClassifierCandle candle in candles)
            await agent.EvaluateAsync(ContextFor(candle));

        // Re-evaluating the same close must not throw (FeatureEngine rejects non-increasing
        // timestamps) and must not fold the candle in a second time.
        AgentDecision repeat = await agent.EvaluateAsync(ContextFor(candles[^1]));
        Assert.That(repeat.Action, Is.AnyOf(AgentAction.Buy, AgentAction.Observe));
    }

    [Test]
    public void Catalogue_ResolvesTheAgentAndRoundTripsItsDefinition()
    {
        TradingAgentCatalog catalog = TradingAgentCatalog.CreateDefault();
        AgentDefinition definition = AgentDefinition.FromTradingClassification(new TradingClassificationStrategyOptions());

        ITradingAgent agent = catalog.Create(definition);

        Assert.Multiple(() =>
        {
            Assert.That(agent, Is.InstanceOf<TradingClassificationAgent>());
            Assert.That(agent.TriggerInterval, Is.EqualTo(Signal));
            Assert.That(agent.ExitManagementMode, Is.EqualTo(AgentExitManagementMode.Bracket));
            // Round trip through the typed definition, as every other agent kind does.
            TradingAgentDefinition typed = TradingAgentDefinition.FromAgentDefinition(definition);
            Assert.That(typed.Kind, Is.EqualTo(TradingAgentKind.TradingClassification));
            Assert.That(typed.TradingClassification, Is.Not.Null);
        });
    }

    [Test]
    public void Options_RejectAStopTargetPairBelowTheMinimumRewardRisk()
    {
        Assert.That(() => new TradingClassificationStrategyOptions
        {
            StopAtrMultiple = 2m,
            TargetAtrMultiple = 2m,
            MinimumRewardRisk = 1.5m
        }.Validate(), Throws.ArgumentException.With.Message.Contains("reward:risk"));
    }

    // ---- helpers ------------------------------------------------------------------------------

    private sealed class AlwaysBuyModel(IReadOnlyList<string> featureNames) : ITradingModel
    {
        public IReadOnlyList<string> FeatureNames { get; } = featureNames;

        public Prediction Predict(FeatureVector features) => new()
        {
            SellProbability = 0.02, NoTradeProbability = 0.08, BuyProbability = 0.90
        };
    }

    /// <summary>
    /// Alternating trend regimes: long enough runs up and down to label BUY, SELL and NO_TRADE.
    /// A single-direction ramp would label every row BUY, and a multiclass model cannot be fitted
    /// to one class - which the trainers now reject explicitly.
    /// </summary>
    private static IReadOnlyList<ClassifierCandle> Trending(int count)
    {
        List<ClassifierCandle> candles = [];
        decimal price = 100m;
        DateTimeOffset timestamp = Start;

        for (int index = 0; index < count; index++)
        {
            decimal open = price;
            // 60-bar regimes: up, down, then a flat stretch that produces NO_TRADE rows.
            int phase = (index / 60) % 3;
            decimal step = phase switch
            {
                0 => index % 7 == 0 ? -0.15m : 0.25m,
                1 => index % 7 == 0 ? 0.15m : -0.25m,
                _ => index % 2 == 0 ? 0.02m : -0.02m
            };
            decimal close = Math.Round(open + step, 4);
            candles.Add(new ClassifierCandle(
                timestamp, open, Math.Max(open, close) + 0.05m, Math.Min(open, close) - 0.05m, close));
            price = close;
            timestamp = timestamp.AddMinutes(5);
        }

        return candles;
    }

    private static LabeledFeatureRow Row(DateTimeOffset timestamp, decimal close) => new()
    {
        Features = new FeatureVector
        {
            Timestamp = timestamp, Close = close, LabelAtr = 1m, Values = [0f]
        },
        Label = TradeLabel.NoTrade,
        LabelExcursionAtr = 0m
    };

    private static AgentMarketContext ContextFor(ClassifierCandle candle) => new()
    {
        Instrument = Instrument,
        Timestamp = candle.Timestamp,
        Analysis = new MultiTimeframeAnalysis(Instrument, candle.Timestamp,
            new Dictionary<BarInterval, AnalysisSnapshot>
            {
                [Signal] = new AnalysisSnapshot
                {
                    Instrument = Instrument,
                    Interval = Signal,
                    AvailableAt = candle.Timestamp,
                    Version = 1,
                    LatestCandle = TestCandles.Create(
                        Instrument, candle.Timestamp.AddMinutes(-5), Signal,
                        candle.Open, candle.High, candle.Low, candle.Close),
                    Indicators = new IndicatorSnapshot(),
                    Swings = [],
                    PriceZones = [],
                    Trendlines = [],
                    Channels = [],
                    Confidence = new ConfidenceScore { Total = 70m, Contributions = [] }
                }
            }),
        Account = new AccountSnapshot
        {
            AccountId = "a", Currency = "USD", Balance = 100_000m, Available = 100_000m, CanTrade = true
        },
        Positions = [],
        OpenOrders = []
    };
}

/// <summary>
/// Meta-labelling: using the classifier to filter another strategy's signals. The random-subset
/// control is the part worth pinning - without it a filter that merely trades less looks like a
/// filter that chooses well.
/// </summary>
[TestFixture]
public sealed class TradingClassifierMetaFilterTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);

    private static (PrimaryTrade, Prediction) Scored(int number, TradeLabel side, decimal net, double agreeing)
    {
        Prediction prediction = side == TradeLabel.Buy
            ? new Prediction { BuyProbability = agreeing, SellProbability = 0.1, NoTradeProbability = 0.9 - agreeing }
            : new Prediction { SellProbability = agreeing, BuyProbability = 0.1, NoTradeProbability = 0.9 - agreeing };

        return (new PrimaryTrade(
            number, side, Start.AddHours(number).AddMinutes(-1), Start.AddHours(number),
            Start.AddHours(number + 1), net, net), prediction);
    }

    [Test]
    public void Agreement_KeepsOnlyTradesTheModelBacksAtTheThreshold()
    {
        (PrimaryTrade, Prediction)[] scored =
        [
            Scored(1, TradeLabel.Buy, 100m, 0.80),
            Scored(2, TradeLabel.Buy, -50m, 0.20),
            Scored(3, TradeLabel.Sell, 30m, 0.60)
        ];

        FilterOutcome outcome = MetaFilter.Apply(scored, FilterMode.Agreement, 0.50);

        Assert.That(outcome.Kept, Is.EqualTo(2));
        Assert.That(outcome.Dropped, Is.EqualTo(1));
        Assert.That(outcome.Report.NetProfit, Is.EqualTo(130m));
    }

    [Test]
    public void NotOpposed_DropsOnlyWhatTheModelContradicts()
    {
        // Opposing probability is pinned at 0.1 by the helper, so a 0.05 threshold drops
        // everything and a 0.5 threshold keeps everything - the two ends of the mode's range.
        (PrimaryTrade, Prediction)[] scored = [Scored(1, TradeLabel.Buy, 100m, 0.80), Scored(2, TradeLabel.Sell, -20m, 0.30)];

        Assert.That(MetaFilter.Apply(scored, FilterMode.NotOpposed, 0.05).Kept, Is.Zero);
        Assert.That(MetaFilter.Apply(scored, FilterMode.NotOpposed, 0.50).Kept, Is.EqualTo(2));
    }

    [Test]
    public void RandomControl_RanksAPerfectFilterAboveChanceAndAnArbitraryOneAtChance()
    {
        // 20 winners and 20 losers of equal size: the full set has profit factor 1.
        List<(PrimaryTrade, Prediction)> scored = [];
        for (int index = 0; index < 40; index++)
            scored.Add(Scored(index, TradeLabel.Buy, index < 20 ? 100m : -100m, 0.9));

        // A filter that kept only the 20 winners would post an infinite profit factor and must
        // land at the top of the random distribution.
        var (median, perfectPercentile, _) = MetaFilter.RandomControl(
            scored, keepCount: 20, actualProfitFactor: double.PositiveInfinity);

        // A filter posting exactly the random median has selected nothing, and must not look good.
        var (_, chancePercentile, _) = MetaFilter.RandomControl(
            scored, keepCount: 20, actualProfitFactor: median);

        Assert.Multiple(() =>
        {
            Assert.That(perfectPercentile, Is.GreaterThan(0.95));
            Assert.That(chancePercentile, Is.LessThan(0.6));
            Assert.That(median, Is.GreaterThan(0));
        });
    }

    [Test]
    public void RandomControl_IsDeterministicForAGivenSeed()
    {
        List<(PrimaryTrade, Prediction)> scored = [];
        for (int index = 0; index < 30; index++)
            scored.Add(Scored(index, TradeLabel.Buy, index % 3 == 0 ? 200m : -80m, 0.7));

        var first = MetaFilter.RandomControl(scored, 10, 1.5, iterations: 500, seed: 42);
        var second = MetaFilter.RandomControl(scored, 10, 1.5, iterations: 500, seed: 42);

        Assert.That(second, Is.EqualTo(first));
    }
}

/// <summary>
/// A training window legitimately contains only two classes, and the resulting two-slot score
/// vector must not be misread.
/// </summary>
[TestFixture]
public sealed class TwoClassModelTests
{
    private static readonly DateTimeOffset Start = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Rallies punctuated by near-flat pauses. Rallies out-run the ATR threshold and label BUY;
    /// pauses fall inside it and label NO_TRADE. Price never falls, so SELL is absent - which is
    /// the two-class training window this test is about. A purely monotone ramp would be rejected
    /// by TrainingPreconditions for having a single class.
    /// </summary>
    private static IReadOnlyList<ClassifierCandle> OneDirectional(int count)
    {
        List<ClassifierCandle> candles = [];
        decimal price = 100m;
        DateTimeOffset time = Start;

        for (int index = 0; index < count; index++)
        {
            bool rallying = (index / 20) % 2 == 0;
            decimal close = Math.Round(price + (rallying ? 0.50m : 0.01m), 4);
            candles.Add(new ClassifierCandle(
                time, price, Math.Max(price, close) + 0.05m, Math.Min(price, close) - 0.05m, close));
            price = close;
            time = time.AddMinutes(5);
        }

        return candles;
    }

    [Test]
    public void TwoClassModel_DoesNotMapTheAbsentClassOntoAnotherClassesScore()
    {
        ClassifierDataset dataset = new DatasetBuilder(new ClassifierOptions { PredictionHorizon = 5 })
            .Build(OneDirectional(700));

        HashSet<TradeLabel> present = [.. dataset.Rows.Select(row => row.Label)];
        Assume.That(present, Has.Count.EqualTo(2), "fixture must produce exactly two classes");
        Assume.That(present, Does.Not.Contain(TradeLabel.Sell));

        using TrainedModel model = new LightGbmModelTrainer(
            new LightGbmTrainingOptions { NumberOfIterations = 25, MinimumExampleCountPerLeaf = 5 })
            .Train(dataset.Rows, dataset.Schema);

        Prediction prediction = model.Model.Predict(dataset.Rows[^1].Features);
        double total = prediction.SellProbability + prediction.NoTradeProbability + prediction.BuyProbability;

        Assert.Multiple(() =>
        {
            // The absent class must report zero, not another class's score.
            Assert.That(prediction.SellProbability, Is.Zero,
                "SELL was absent from training and must not borrow another slot's probability");
            // And the total must remain a distribution rather than exceeding 1.
            Assert.That(total, Is.EqualTo(1.0).Within(1e-3),
                "probabilities must sum to 1; a duplicated slot would push this above 1");
        });
    }

    [Test]
    public void TwoClassModel_DoesNotEmitSignalsForTheAbsentDirection()
    {
        ClassifierDataset dataset = new DatasetBuilder(new ClassifierOptions { PredictionHorizon = 5 })
            .Build(OneDirectional(700));
        using TrainedModel model = new LightGbmModelTrainer(
            new LightGbmTrainingOptions { NumberOfIterations = 25, MinimumExampleCountPerLeaf = 5 })
            .Train(dataset.Rows, dataset.Schema);

        ConfidenceSignalGenerator generator = new(new ClassifierOptions());

        // Across the whole dataset, not one bar may produce a SELL: the model has never seen one.
        foreach (var row in dataset.Rows)
        {
            TradingSignal signal = generator.Generate(model.Model.Predict(row.Features));
            Assert.That(signal.Action, Is.Not.EqualTo(TradeLabel.Sell),
                $"a class absent from training produced a SELL at {row.Features.Timestamp:O}");
        }
    }
}

/// <summary>
/// The metadata sidecar: the only thing standing between a saved model and being scored under a
/// configuration it was never trained for.
/// </summary>
[TestFixture]
public sealed class ModelArtifactTests
{
    private static ClassifierOptions Options(int horizon = 10) => new()
    {
        PredictionHorizon = horizon,
        EnabledGroups = FeatureGroups.Experiment3
    };

    private static ClassifierModelArtifact Artifact(ClassifierOptions options, int intervalMinutes)
    {
        IReadOnlyList<string> names = FeatureSchema.Create(options).Names;
        return new ClassifierModelArtifact
        {
            SchemaVersion = ClassifierModelArtifact.CurrentSchemaVersion,
            TrainerName = "LightGBM",
            CreatedAt = DateTimeOffset.UtcNow,
            CandleIntervalMinutes = intervalMinutes,
            Instrument = "XAUUSD",
            TrainedFrom = DateTimeOffset.UnixEpoch,
            TrainedTo = DateTimeOffset.UnixEpoch.AddDays(365),
            TrainingRows = 5000,
            TrainedClasses = ["Sell", "NoTrade", "Buy"],
            PredictionHorizon = options.PredictionHorizon,
            AtrTargetMultiplier = options.AtrTargetMultiplier,
            UseMaximumExcursionLabels = options.UseMaximumExcursionLabels,
            TrainingStride = options.TrainingStride,
            EnabledGroups = options.EnabledGroups.ToString(),
            FeatureNames = names,
            BuyProbabilityThreshold = options.BuyProbabilityThreshold,
            SellProbabilityThreshold = options.SellProbabilityThreshold,
            ConfigurationHash = ClassifierModelArtifact.ComputeHash(options, intervalMinutes, names)
        };
    }

    [Test]
    public void EnsureCompatible_CatchesATimeframeMismatchThatFeatureNamesCannot()
    {
        ClassifierOptions options = Options();
        ClassifierModelArtifact artifact = Artifact(options, intervalMinutes: 15);
        IReadOnlyList<string> names = FeatureSchema.Create(options).Names;

        // Same options, same feature names - only the bar interval differs. This is the exact
        // hazard: training defaults to unresampled input, the agent defaults to 5m.
        Assert.That(() => artifact.EnsureCompatible(options, 5, names),
            Throws.InvalidOperationException.With.Message.Contains("interval 15m -> 5m"));

        artifact.EnsureCompatible(options, 15, names);
    }

    [Test]
    public void EnsureCompatible_CatchesHorizonLabelModeAndFeatureSetChanges()
    {
        ClassifierOptions options = Options();
        ClassifierModelArtifact artifact = Artifact(options, 15);

        Assert.Multiple(() =>
        {
            ClassifierOptions horizon = options with { PredictionHorizon = 20 };
            Assert.That(() => artifact.EnsureCompatible(horizon, 15, FeatureSchema.Create(horizon).Names),
                Throws.InvalidOperationException.With.Message.Contains("horizon"));

            ClassifierOptions label = options with { UseMaximumExcursionLabels = true };
            Assert.That(() => artifact.EnsureCompatible(label, 15, FeatureSchema.Create(label).Names),
                Throws.InvalidOperationException.With.Message.Contains("label mode"));

            ClassifierOptions groups = options with { EnabledGroups = FeatureGroups.Experiment5 };
            Assert.That(() => artifact.EnsureCompatible(groups, 15, FeatureSchema.Create(groups).Names),
                Throws.InvalidOperationException.With.Message.Contains("groups"));
        });
    }

    [Test]
    public void Save_AndLoad_RoundTripsThroughTheSidecar()
    {
        ClassifierOptions options = Options();
        string path = Path.Combine(Path.GetTempPath(), $"tc-{Guid.NewGuid():N}.zip");
        try
        {
            Artifact(options, 15).Save(path);
            ClassifierModelArtifact loaded = ClassifierModelArtifact.Load(path);

            Assert.Multiple(() =>
            {
                Assert.That(loaded.CandleIntervalMinutes, Is.EqualTo(15));
                Assert.That(loaded.PredictionHorizon, Is.EqualTo(options.PredictionHorizon));
                Assert.That(loaded.BuyProbabilityThreshold, Is.EqualTo(options.BuyProbabilityThreshold));
                Assert.That(loaded.TrainedClasses, Has.Count.EqualTo(3));
            });
        }
        finally
        {
            string sidecar = Path.ChangeExtension(path, null) + ".metadata.json";
            if (File.Exists(sidecar)) File.Delete(sidecar);
        }
    }

    [Test]
    public void Load_RefusesAModelWithNoSidecar()
    {
        // A bare .zip cannot be used safely: its timeframe, horizon and thresholds are unknowable.
        Assert.That(() => ClassifierModelArtifact.Load(Path.Combine(Path.GetTempPath(), "missing-model.zip")),
            Throws.TypeOf<FileNotFoundException>().With.Message.Contains("cannot be used safely"));
    }
}
