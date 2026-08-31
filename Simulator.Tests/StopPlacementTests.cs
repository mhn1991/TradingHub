using Agent.Abstractions;
using Agent.Models;
using Agent.Strategies.TrendTactical;
using Brokers.Models;
using ChartAnnotator.Models;
using NUnit.Framework;
using TradingClassifier.Configuration;
using TradingClassifier.Features;
using TradingClassifier.Models;

namespace Simulator.Tests;

/// <summary>
/// ML-assisted stop placement. Adverse excursion is the only quantity measured in this repo with
/// usable signal (rho 0.0597, against favourable excursion's -0.0010), so the stop is the half of
/// the bracket worth predicting — but a prediction that sizes live risk needs guardrails more than
/// it needs accuracy.
/// </summary>
[TestFixture]
public sealed class StopPlacementTests
{
    private static readonly InstrumentKey Instrument = new("METAL:XAU/USD");
    private static readonly DateTimeOffset Now = new(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly BarInterval Trigger = BarInterval.Minutes(15);
    private static readonly BarInterval Trend = BarInterval.Hours(2);

    [Test]
    public void WithoutAStopModel_TheAgentKeepsItsStructuralStop()
    {
        // The failure to avoid is an agent that behaves as though it had a model it does not have:
        // the A/B against the structural stop would then be comparing a thing with itself.
        TrendTacticalAgent agent = new(
            new TrendTacticalStrategyOptions
            {
                UseMlStopPlacement = true,
                Classifier = new ClassifierOptions { EnabledGroups = FeatureGroups.Experiment1 }
            },
            new AlwaysBuyModel(),
            stopModel: null);

        Assert.That(agent.ExitManagementMode,
            Is.EqualTo(AgentExitManagementMode.ProtectiveStopAndStrategyExit));
    }

    [Test]
    public void PredictedStop_IsBoundedOnBothSides()
    {
        // Previously this test re-implemented Math.Clamp and never called the agent, so it could not
        // fail for the reason it existed — worse than no test, because it read as coverage.
        // It now drives the agent with a degenerate stop model and asserts on the stop it emits.
        foreach (decimal predicted in (decimal[])[0.0001m, 10_000m])
        {
            TrendTacticalAgent agent = new(
                new TrendTacticalStrategyOptions
                {
                    TriggerInterval = Trigger,
                    TrendInterval = Trend,
                    RequireClassifierAgreement = false,
                    UseMlStopPlacement = true,
                    MinimumStopAtrMultiple = 0.75m,
                    MaximumStopAtrMultiple = 4.0m,
                    Classifier = new ClassifierOptions { EnabledGroups = FeatureGroups.Experiment1 }
                },
                new AlwaysBuyModel(),
                new ConstantStopModel(predicted));

            AgentDecision decision = Drive(agent);
            if (decision.Action is not (AgentAction.Buy or AgentAction.Sell))
                continue;

            decimal distance = Math.Abs(decision.ReferencePrice!.Value - decision.StopLossPrice!.Value);
            // Atr is 5m in the fixture, so the permitted band is 0.75-4.0 ATR = 3.75-20.
            Assert.That(distance, Is.InRange(3.75m, 20m),
                $"prediction {predicted} produced a stop {distance} outside the configured bounds");
        }
    }

    [Test]
    public void SafetyMultipleMustPlaceTheStopBeyondTheExpectedExcursion()
    {
        // A stop placed exactly AT the expected adverse excursion is hit roughly half the time by
        // construction, so the default has to exceed 1.
        Assert.That(new TrendTacticalStrategyOptions().MlStopSafetyMultiple, Is.GreaterThan(1m));
    }

    [Test]
    public void OptionsReject_DegenerateStopBounds()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => new TrendTacticalStrategyOptions { MlStopSafetyMultiple = 0m }.Validate(),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => new TrendTacticalStrategyOptions
            {
                MinimumStopAtrMultiple = 3m, MaximumStopAtrMultiple = 1m
            }.Validate(), Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }

    [Test]
    public void StopModel_RejectsAMismatchedFeatureVector()
    {
        // Scoring a 20-column model with a 5-column vector would place a stop from misaligned
        // columns — confident nonsense, and it would size real risk.
        StubStopModel model = new(["a", "b", "c"]);
        FeatureVector wrongWidth = new()
        {
            Timestamp = Now, Close = 100m, LabelAtr = 1m, Values = [0f, 0f]
        };

        Assert.That(() => model.PredictAdverseExcursionAtr(wrongWidth), Throws.ArgumentException);
    }

    /// <summary>Drives enough 2H candles to confirm a trend, then returns the last decision.</summary>
    private static AgentDecision Drive(TrendTacticalAgent agent)
    {
        AgentDecision decision = agent.EvaluateAsync(Context(flat: true)).GetAwaiter().GetResult();
        for (int index = 1; index <= 40; index++)
        {
            decision = agent.EvaluateAsync(
                Context(flat: false, step: index, drift: index * 3m)).GetAwaiter().GetResult();
        }
        return decision;
    }

    private static AgentMarketContext Context(bool flat, int step = 0, decimal drift = 0m)
    {
        decimal close = 2_000m + (flat ? 0m : drift);
        DateTimeOffset at = Now.AddHours(2 * step);
        return new AgentMarketContext
        {
            Instrument = Instrument,
            Timestamp = at,
            Analysis = new MultiTimeframeAnalysis(Instrument, at,
                new Dictionary<BarInterval, AnalysisSnapshot>
                {
                    [Trigger] = Snapshot(Trigger, close, at),
                    [Trend] = Snapshot(Trend, close, at)
                }),
            Account = new AccountSnapshot
            {
                AccountId = "a", Currency = "USD", Balance = 100_000m, Available = 100_000m, CanTrade = true
            },
            Positions = [],
            OpenOrders = []
        };
    }

    private static AnalysisSnapshot Snapshot(BarInterval interval, decimal close, DateTimeOffset at) => new()
    {
        Instrument = Instrument,
        Interval = interval,
        AvailableAt = at,
        Version = 1,
        LatestCandle = TestCandles.Create(Instrument, at, interval, close - 1m, close + 2m, close - 2m, close),
        Indicators = new IndicatorSnapshot { Atr = 5m, Rsi = 55m, Cci = 20m },
        Swings = [],
        PriceZones = [],
        Trendlines = [],
        Channels = [],
        Confidence = new ConfidenceScore { Total = 70m, Contributions = [] }
    };

    /// <summary>Returns a fixed prediction, including degenerate ones the bounds must contain.</summary>
    private sealed class ConstantStopModel(decimal value) : IStopPlacementModel
    {
        public IReadOnlyList<string> FeatureNames { get; } = [];

        public decimal PredictAdverseExcursionAtr(FeatureVector features) => value;
    }

    private sealed class StubStopModel(IReadOnlyList<string> names) : IStopPlacementModel
    {
        public IReadOnlyList<string> FeatureNames { get; } = names;

        public decimal PredictAdverseExcursionAtr(FeatureVector features) =>
            features.Values.Length != FeatureNames.Count
                ? throw new ArgumentException("width mismatch", nameof(features))
                : 1.2m;
    }

    private sealed class AlwaysBuyModel : ITradingModel
    {
        public IReadOnlyList<string> FeatureNames { get; } = [];

        public Prediction Predict(FeatureVector features) => new()
        {
            BuyProbability = 0.9, NoTradeProbability = 0.05, SellProbability = 0.05
        };
    }
}
