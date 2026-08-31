using ChartAnnotator.Regime;
using NUnit.Framework;
using TradingClassifier.Features;
using TradingClassifier.ML.Evaluation;
using TradingClassifier.ML.Training;
using TradingClassifier.Models;

namespace Simulator.Tests;

/// <summary>
/// ML catalogue Phase 6 (ensembles, strategy selector) and Phase 7 (drift detection, automatic
/// retraining). These are infrastructure: none of them can create an edge, and the tests are written
/// to pin the refusals as much as the happy paths.
/// </summary>
[TestFixture]
public sealed class Phase67Tests
{
    // ---- Phase 6: ensemble ----------------------------------------------------------------------

    [Test]
    public void Ensemble_AveragesMembersAndStaysNormalised()
    {
        EnsembleModel ensemble = new([Stub(0.8, 0.1, 0.1), Stub(0.2, 0.2, 0.6)]);
        Prediction prediction = ensemble.Predict(Features());

        // Members are (sell, noTrade, buy) = (0.8, 0.1, 0.1) and (0.2, 0.2, 0.6).
        // Equal weights give sell 0.50, noTrade 0.15, buy 0.35 — which already sums to 1, so the
        // renormalisation step must be a no-op here rather than a silent rescale.
        Assert.Multiple(() =>
        {
            Assert.That(prediction.SellProbability, Is.EqualTo(0.50).Within(1e-9));
            Assert.That(prediction.NoTradeProbability, Is.EqualTo(0.15).Within(1e-9));
            Assert.That(prediction.BuyProbability, Is.EqualTo(0.35).Within(1e-9));
            Assert.That(
                prediction.BuyProbability + prediction.SellProbability + prediction.NoTradeProbability,
                Is.EqualTo(1.0).Within(1e-9));
        });
    }

    // ---- Phase 7: drift -------------------------------------------------------------------------

    [Test]
    public void Psi_IsNearZeroForTheSameDistributionAndLargeForAShiftedOne()
    {
        Random random = new(3);
        double[] reference = [.. Enumerable.Range(0, 2000).Select(_ => random.NextDouble())];
        double[] same = [.. Enumerable.Range(0, 2000).Select(_ => random.NextDouble())];
        double[] shifted = [.. Enumerable.Range(0, 2000).Select(_ => random.NextDouble() + 1.0)];

        DriftReport stable = ModelDriftDetector.Compare("f", reference, same);
        DriftReport moved = ModelDriftDetector.Compare("f", reference, shifted);

        Assert.Multiple(() =>
        {
            Assert.That(stable.PopulationStabilityIndex, Is.LessThan(0.10));
            Assert.That(stable.RequiresAttention, Is.False);
            // §3.12c's killer: a test window sitting almost entirely outside the training range.
            Assert.That(moved.PopulationStabilityIndex, Is.GreaterThan(0.25));
            Assert.That(moved.RequiresAttention, Is.True);
        });
    }

    // ---- Phase 7: retraining --------------------------------------------------------------------

    [Test]
    public void Retraining_PrioritisesInputDriftOverPerformanceDecay()
    {
        // A model whose inputs have left the training distribution is not underperforming — it is
        // being asked the wrong question. The trigger must say which.
        RetrainingPolicy policy = new();
        RetrainDecision decision = policy.Evaluate(
            eventsSinceLastFit: 500,
            inputDrift: [Drift("atr14_pct", 0.60)],
            predictionDrift: null,
            referenceExpectancyR: 0.20,
            currentExpectancyR: 0.02);

        Assert.Multiple(() =>
        {
            Assert.That(decision.ShouldRetrain, Is.True);
            Assert.That(decision.Trigger, Is.EqualTo(RetrainTrigger.InputDrift));
            Assert.That(decision.Reason, Does.Contain("atr14_pct"));
        });
    }

    [Test]
    public void Retraining_DoesNotFireOnSmallSamplesOrOnAnAlreadyNegativeReference()
    {
        RetrainingPolicy policy = new();

        RetrainDecision tooEarly = policy.Evaluate(
            10, [Drift("f", 0.9)], null, 0.2, -0.5);

        // Halving a NEGATIVE expectancy is an improvement, not decay — this must not trigger.
        RetrainDecision negativeReference = policy.Evaluate(
            500, [], null, referenceExpectancyR: -0.20, currentExpectancyR: -0.40);

        Assert.Multiple(() =>
        {
            Assert.That(tooEarly.ShouldRetrain, Is.False);
            Assert.That(negativeReference.Trigger, Is.Not.EqualTo(RetrainTrigger.PerformanceDecay));
        });
    }

    // ---- Phase 6: strategy selector --------------------------------------------------------------

    [Test]
    public void Selector_RefusesThinSamplesAndLosingStrategies()
    {
        StrategySelector selector = new();
        IReadOnlyList<StrategyChoice> choices = selector.Fit(
        [
            // Enough trades, but every option loses: not trading beats the least-bad loser.
            new RegimePerformance
            {
                StrategyId = "a", Regime = MarketRegime.TrendingUp, Trades = 100, TotalR = -12
            },
            new RegimePerformance
            {
                StrategyId = "b", Regime = MarketRegime.TrendingUp, Trades = 100, TotalR = -30
            },
            // §2.18 saw windows with 1-4 trades reporting PF 17 and infinity.
            new RegimePerformance
            {
                StrategyId = "c", Regime = MarketRegime.Range, Trades = 3, TotalR = 9
            },
            new RegimePerformance
            {
                StrategyId = "d", Regime = MarketRegime.Compression, Trades = 200, TotalR = 40
            }
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(choices.Single(c => c.Regime == MarketRegime.TrendingUp).StrategyId, Is.Null);
            Assert.That(choices.Single(c => c.Regime == MarketRegime.Range).StrategyId, Is.Null);
            Assert.That(choices.Single(c => c.Regime == MarketRegime.Compression).StrategyId,
                Is.EqualTo("d"));
        });
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static DriftReport Drift(string name, double psi) => new()
    {
        Name = name, PopulationStabilityIndex = psi, ReferenceCount = 1000, CurrentCount = 1000
    };

    private static FeatureVector Features() => new()
    {
        Timestamp = DateTimeOffset.UnixEpoch, Close = 100m, LabelAtr = 1m, Values = [0f]
    };

    private static ITradingModel Stub(double sell, double noTrade, double buy) =>
        new StubModel(sell, noTrade, buy);

    private sealed class StubModel(double sell, double noTrade, double buy) : ITradingModel
    {
        public IReadOnlyList<string> FeatureNames { get; } = ["x"];

        public Prediction Predict(FeatureVector features) => new()
        {
            SellProbability = sell, NoTradeProbability = noTrade, BuyProbability = buy
        };
    }
}
