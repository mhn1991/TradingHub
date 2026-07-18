using ChartAnnotator.Engine;
using ChartAnnotator.SupplyDemand;

namespace Simulator.Tests;

[TestFixture]
public sealed class SupplyDemandContractsTests
{
    [Test]
    public void Hash_IdenticalProfiles_ProducesSameHash()
    {
        var a = new SupplyDemandCalculationProfile { Enabled = true, AtrPeriod = 21 };
        var b = new SupplyDemandCalculationProfile { Enabled = true, AtrPeriod = 21 };

        Assert.That(
            SupplyDemandCalculationProfileHasher.ComputeHash(a),
            Is.EqualTo(SupplyDemandCalculationProfileHasher.ComputeHash(b)));
    }

    [Test]
    public void Hash_DifferingScoringWeight_ProducesDifferentHash()
    {
        var baseline = new SupplyDemandCalculationProfile();
        var changed = baseline with
        {
            ScoringWeights = baseline.ScoringWeights with { Freshness = baseline.ScoringWeights.Freshness + 0.01m }
        };

        Assert.That(
            SupplyDemandCalculationProfileHasher.ComputeHash(baseline),
            Is.Not.EqualTo(SupplyDemandCalculationProfileHasher.ComputeHash(changed)));
    }

    [Test]
    public void Hash_DifferingRuleSetVersion_ProducesDifferentHash()
    {
        var baseline = new SupplyDemandCalculationProfile();
        var bumped = baseline with { RuleSetVersion = baseline.RuleSetVersion + 1 };

        Assert.That(
            SupplyDemandCalculationProfileHasher.ComputeHash(baseline),
            Is.Not.EqualTo(SupplyDemandCalculationProfileHasher.ComputeHash(bumped)));
    }

    [Test]
    public void Validate_DefaultProfile_DoesNotThrow() =>
        Assert.DoesNotThrow(() => new SupplyDemandCalculationProfile().Validate());

    [TestCase(0)]
    [TestCase(-1)]
    public void Validate_NonPositiveAtrPeriod_Throws(int atrPeriod)
    {
        var profile = new SupplyDemandCalculationProfile { AtrPeriod = atrPeriod };
        Assert.Throws<ArgumentOutOfRangeException>(() => profile.Validate());
    }

    [Test]
    public void Validate_MaximumBaseCandlesBelowMinimum_Throws()
    {
        var profile = new SupplyDemandCalculationProfile { MinimumBaseCandles = 4, MaximumBaseCandles = 2 };
        Assert.Throws<ArgumentOutOfRangeException>(() => profile.Validate());
    }

    [Test]
    public void DeterministicId_SameInputs_ProducesSameGuid()
    {
        DateTimeOffset baseStart = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        Guid first = DeterministicId.Create("zone", "EURUSD", "M15", SupplyDemandZoneType.Demand, baseStart, 1.2345m);
        Guid second = DeterministicId.Create("zone", "EURUSD", "M15", SupplyDemandZoneType.Demand, baseStart, 1.2345m);

        Assert.That(first, Is.EqualTo(second));
    }

    [Test]
    public void DeterministicId_DifferingPrice_ProducesDifferentGuid()
    {
        DateTimeOffset baseStart = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        Guid first = DeterministicId.Create("zone", "EURUSD", "M15", SupplyDemandZoneType.Demand, baseStart, 1.2345m);
        Guid second = DeterministicId.Create("zone", "EURUSD", "M15", SupplyDemandZoneType.Demand, baseStart, 1.2346m);

        Assert.That(first, Is.Not.EqualTo(second));
    }

    [Test]
    public void DeterministicId_DecimalFormatting_IsCultureInvariant()
    {
        var originalCulture = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture =
                System.Globalization.CultureInfo.GetCultureInfo("de-DE");
            Guid underGermanCulture = DeterministicId.Create("zone", 1.2345m);

            System.Threading.Thread.CurrentThread.CurrentCulture =
                System.Globalization.CultureInfo.InvariantCulture;
            Guid underInvariantCulture = DeterministicId.Create("zone", 1.2345m);

            Assert.That(underGermanCulture, Is.EqualTo(underInvariantCulture));
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = originalCulture;
        }
    }

    [Test]
    public void DisabledSnapshot_HasNoZonesOrEvents()
    {
        SupplyDemandAnalysisSnapshot snapshot = SupplyDemandAnalysisSnapshot.Disabled;

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.IsEnabled, Is.False);
            Assert.That(snapshot.ActiveZones, Is.Empty);
            Assert.That(snapshot.RecentEvents, Is.Empty);
        });
    }
}
