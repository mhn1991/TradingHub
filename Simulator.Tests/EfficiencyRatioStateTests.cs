using ChartAnnotator.Indicators;

namespace Simulator.Tests;

[TestFixture]
public sealed class EfficiencyRatioStateTests
{
    [Test]
    public void FlatPrices_ReturnsZero()
    {
        var state = new EfficiencyRatioState(period: 5);

        for (int index = 0; index <= 5; index++)
        {
            state.Update(100m);
        }

        Assert.Multiple(() =>
        {
            Assert.That(state.IsReady, Is.True);
            Assert.That(state.Current, Is.EqualTo(0m));
        });
    }

    [Test]
    public void MonotonicMovement_ReturnsOne()
    {
        var state = new EfficiencyRatioState(period: 5);
        decimal[] closes = [100m, 101m, 102m, 103m, 104m, 105m];

        decimal last = 0m;
        foreach (decimal close in closes)
        {
            last = state.Update(close);
        }

        Assert.Multiple(() =>
        {
            Assert.That(state.IsReady, Is.True);
            Assert.That(last, Is.EqualTo(1m));
        });
    }

    [Test]
    public void AlternatingMovement_ProducesLowerEfficiencyRatioThanMonotonic()
    {
        var alternating = new EfficiencyRatioState(period: 4);
        decimal[] alternatingCloses = [100m, 101m, 100m, 101m, 100m];
        decimal alternatingResult = 0m;
        foreach (decimal close in alternatingCloses)
        {
            alternatingResult = alternating.Update(close);
        }

        var monotonic = new EfficiencyRatioState(period: 4);
        decimal[] monotonicCloses = [100m, 101m, 102m, 103m, 104m];
        decimal monotonicResult = 0m;
        foreach (decimal close in monotonicCloses)
        {
            monotonicResult = monotonic.Update(close);
        }

        Assert.That(alternatingResult, Is.LessThan(monotonicResult));
    }

    [Test]
    public void BoundedState_MatchesTrustedBatchImplementation()
    {
        const int period = 5;
        decimal[] closes =
        [
            100m, 102m, 101m, 105m, 103m, 108m, 107m, 110m, 106m, 112m,
            111m, 115m, 114m, 118m, 116m
        ];

        var state = new EfficiencyRatioState(period);

        for (int index = 0; index < closes.Length; index++)
        {
            decimal actual = state.Update(closes[index]);

            if (index < period)
            {
                Assert.That(state.IsReady, Is.False, $"Should not be ready at index {index}.");
                continue;
            }

            decimal directionalMovement = Math.Abs(closes[index] - closes[index - period]);
            decimal pathMovement = 0m;
            for (int step = index - period + 1; step <= index; step++)
            {
                pathMovement += Math.Abs(closes[step] - closes[step - 1]);
            }

            decimal expected = pathMovement == 0m
                ? 0m
                : Math.Clamp(directionalMovement / pathMovement, 0m, 1m);

            Assert.That(state.IsReady, Is.True, $"Should be ready at index {index}.");
            Assert.That(actual, Is.EqualTo(expected), $"Mismatch at index {index}.");
        }
    }

    [Test]
    public void BeforeWarmupCompletion_IsNotReady()
    {
        var state = new EfficiencyRatioState(period: 5);
        decimal[] closes = [100m, 101m, 99m, 103m, 98m];

        foreach (decimal close in closes)
        {
            decimal current = state.Update(close);
            Assert.Multiple(() =>
            {
                Assert.That(state.IsReady, Is.False);
                Assert.That(current, Is.EqualTo(0m));
            });
        }
    }

    [Test]
    public void Deterministic_ExactDecimalBehaviourAcrossIndependentInstances()
    {
        decimal[] closes =
        [
            100.123m, 101.456m, 99.789m, 103.012m, 98.345m, 106.678m, 104.901m, 109.234m
        ];

        var first = new EfficiencyRatioState(period: 3);
        var second = new EfficiencyRatioState(period: 3);

        foreach (decimal close in closes)
        {
            decimal firstResult = first.Update(close);
            decimal secondResult = second.Update(close);
            Assert.That(firstResult, Is.EqualTo(secondResult));
        }

        Assert.That(first.Current, Is.EqualTo(second.Current));
    }

    [Test]
    public void Constructor_RejectsInvalidPeriod()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EfficiencyRatioState(period: 1));
    }
}
