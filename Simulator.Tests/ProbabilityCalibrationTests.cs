using NUnit.Framework;
using TradingClassifier.Evaluation;

namespace Simulator.Tests;

/// <summary>
/// Calibration is the one Phase-3 item this repo reported but never fitted: expected calibration
/// error was printed, and nothing corrected the probabilities it was complaining about.
/// </summary>
[TestFixture]
public sealed class ProbabilityCalibrationTests
{
    [Test]
    public void Platt_CorrectsASystematicallyOverconfidentModel()
    {
        // A model whose scores are pushed toward the extremes: it says 0.95 when the truth is 0.75.
        // Overconfidence is the failure mode that matters, because it is invisible in accuracy and
        // fatal to any "take if p > x" rule.
        Random random = new(11);
        List<double> scores = [];
        List<bool> outcomes = [];
        for (int index = 0; index < 600; index++)
        {
            double truth = random.NextDouble();
            bool outcome = random.NextDouble() < truth;
            double overconfident = truth < 0.5 ? truth * 0.4 : 1 - ((1 - truth) * 0.4);
            scores.Add(overconfident);
            outcomes.Add(outcome);
        }

        PlattCalibrator calibrator = PlattCalibrator.Fit(scores, outcomes);
        double before = Error(scores, outcomes, raw => raw);
        double after = Error(scores, outcomes, calibrator.Calibrate);

        Assert.Multiple(() =>
        {
            Assert.That(after, Is.LessThan(before),
                $"Calibration made it worse: {before:F4} -> {after:F4}");
            Assert.That(calibrator.Calibrate(0.5), Is.InRange(0.0, 1.0));
        });
    }

    [Test]
    public void Platt_ReturnsIdentity_WhenAFoldHasOnlyOneClass()
    {
        // A thin walk-forward fold routinely holds a single outcome class. Fitting there would encode
        // the base rate as certainty, so the calibrator must decline instead.
        PlattCalibrator calibrator = PlattCalibrator.Fit(
            [0.1, 0.4, 0.7, 0.9], [true, true, true, true]);

        Assert.Multiple(() =>
        {
            Assert.That(calibrator.A, Is.EqualTo(PlattCalibrator.Identity.A));
            Assert.That(calibrator.B, Is.EqualTo(PlattCalibrator.Identity.B));
        });
    }

    [Test]
    public void Platt_SurvivesPerfectlySeparableData()
    {
        // Without Platt's target smoothing the coefficients run to infinity here and every
        // subsequent probability collapses to exactly 0 or 1.
        List<double> scores = [.. Enumerable.Range(0, 100).Select(index => index / 100.0)];
        List<bool> outcomes = [.. scores.Select(score => score > 0.5)];

        PlattCalibrator calibrator = PlattCalibrator.Fit(scores, outcomes);

        Assert.Multiple(() =>
        {
            Assert.That(double.IsFinite(calibrator.A), Is.True);
            Assert.That(double.IsFinite(calibrator.B), Is.True);
            Assert.That(calibrator.Calibrate(0.9), Is.InRange(0.0, 1.0));
            Assert.That(calibrator.Calibrate(0.1), Is.InRange(0.0, 1.0));
        });
    }

    [Test]
    public void Isotonic_IsMonotoneAndDeclinesOnSmallSamples()
    {
        Random random = new(7);
        List<double> scores = [];
        List<bool> outcomes = [];
        for (int index = 0; index < 500; index++)
        {
            double score = random.NextDouble();
            scores.Add(score);
            outcomes.Add(random.NextDouble() < score);
        }

        IsotonicCalibrator? fitted = IsotonicCalibrator.Fit(scores, outcomes, minimumSamples: 200);
        IsotonicCalibrator? refused = IsotonicCalibrator.Fit([0.2, 0.8], [false, true], minimumSamples: 200);

        Assert.Multiple(() =>
        {
            Assert.That(refused, Is.Null, "Isotonic must decline a sample it cannot support.");
            Assert.That(fitted, Is.Not.Null);

            double previous = -1;
            for (double point = 0; point <= 1.0; point += 0.05)
            {
                double value = fitted!.Calibrate(point);
                Assert.That(value, Is.GreaterThanOrEqualTo(previous), "Isotonic output must be monotone.");
                Assert.That(value, Is.InRange(0.0, 1.0));
                previous = value;
            }
        });
    }

    /// <summary>Brier score — mean squared error of the probability against the outcome.</summary>
    private static double Error(
        IReadOnlyList<double> scores, IReadOnlyList<bool> outcomes, Func<double, double> map)
    {
        double total = 0;
        for (int index = 0; index < scores.Count; index++)
        {
            double predicted = map(scores[index]);
            double actual = outcomes[index] ? 1.0 : 0.0;
            total += (predicted - actual) * (predicted - actual);
        }
        return total / scores.Count;
    }
}
