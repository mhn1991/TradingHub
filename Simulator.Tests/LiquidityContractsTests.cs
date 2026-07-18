using ChartAnnotator.Engine;
using ChartAnnotator.Liquidity;

namespace Simulator.Tests;

[TestFixture]
public sealed class LiquidityContractsTests
{
    [Test]
    public void Hash_IdenticalProfiles_ProducesSameHash()
    {
        var a = new LiquidityCalculationProfile { Enabled = true, EqualLevelToleranceAtr = 0.15m };
        var b = new LiquidityCalculationProfile { Enabled = true, EqualLevelToleranceAtr = 0.15m };

        Assert.That(
            LiquidityCalculationProfileHasher.ComputeHash(a),
            Is.EqualTo(LiquidityCalculationProfileHasher.ComputeHash(b)));
    }

    [Test]
    public void Hash_DifferingScoringWeight_ProducesDifferentHash()
    {
        var baseline = new LiquidityCalculationProfile();
        var changed = baseline with
        {
            ScoringWeights = baseline.ScoringWeights with { Prominence = baseline.ScoringWeights.Prominence + 0.01m }
        };

        Assert.That(
            LiquidityCalculationProfileHasher.ComputeHash(baseline),
            Is.Not.EqualTo(LiquidityCalculationProfileHasher.ComputeHash(changed)));
    }

    [Test]
    public void Hash_DifferingRuleSetVersion_ProducesDifferentHash()
    {
        var baseline = new LiquidityCalculationProfile();
        var bumped = baseline with { RuleSetVersion = baseline.RuleSetVersion + 1 };

        Assert.That(
            LiquidityCalculationProfileHasher.ComputeHash(baseline),
            Is.Not.EqualTo(LiquidityCalculationProfileHasher.ComputeHash(bumped)));
    }

    [Test]
    public void Validate_DefaultProfile_DoesNotThrow() =>
        Assert.DoesNotThrow(() => new LiquidityCalculationProfile().Validate());

    [Test]
    public void Validate_MaximumSourcePointsBelowMinimum_Throws()
    {
        var profile = new LiquidityCalculationProfile { MinimumSourcePoints = 4, MaximumSourcePoints = 2 };
        Assert.Throws<ArgumentOutOfRangeException>(() => profile.Validate());
    }

    [Test]
    public void Validate_UnknownTimeZone_Throws()
    {
        var profile = new LiquidityCalculationProfile { ReferenceTimeZoneId = "Not/A_Real_Zone" };
        Assert.Throws<ArgumentOutOfRangeException>(() => profile.Validate());
    }

    [Test]
    public void Validate_RoundNumberEnabledWithNoSteps_Throws()
    {
        var profile = new LiquidityCalculationProfile
        {
            EnableRoundNumberLevels = true,
            RoundNumberMajorStep = null,
            RoundNumberMinorStep = null
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => profile.Validate());
    }

    [Test]
    public void DeterministicId_SameInputs_ProducesSameGuid()
    {
        DateTimeOffset originatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        Guid first = DeterministicId.Create("pool", "EURUSD", "M15", LiquidityPoolType.EqualHighs, originatedAt, 1.2345m);
        Guid second = DeterministicId.Create("pool", "EURUSD", "M15", LiquidityPoolType.EqualHighs, originatedAt, 1.2345m);

        Assert.That(first, Is.EqualTo(second));
    }

    [Test]
    public void DeterministicId_DifferingPoolType_ProducesDifferentGuid()
    {
        DateTimeOffset originatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        Guid first = DeterministicId.Create("pool", "EURUSD", "M15", LiquidityPoolType.EqualHighs, originatedAt, 1.2345m);
        Guid second = DeterministicId.Create("pool", "EURUSD", "M15", LiquidityPoolType.SwingHigh, originatedAt, 1.2345m);

        Assert.That(first, Is.Not.EqualTo(second));
    }

    [Test]
    public void DisabledSnapshot_HasNoPoolsOrEvents()
    {
        LiquidityAnalysisSnapshot snapshot = LiquidityAnalysisSnapshot.Disabled;

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.IsEnabled, Is.False);
            Assert.That(snapshot.ActivePools, Is.Empty);
            Assert.That(snapshot.RecentEvents, Is.Empty);
        });
    }
}
