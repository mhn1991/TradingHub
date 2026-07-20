using Agent.Strategies.StructuralConfluence;
using Simulator.Calibration;

namespace Simulator.Tests;

/// <summary>
/// Blueprint §19 Phase 1-equivalent gate for the second calibration-enabled strategy: pins the
/// current, pre-calibration <see cref="LiquidityBreakRetestOptions"/> defaults so a future
/// accidental change to the strategy's compiled-in defaults fails loudly here rather than silently
/// invalidating every previously-produced calibration artifact.
/// </summary>
[TestFixture]
public sealed class LiquidityBreakRetestCalibrationCompatibilityTests
{
    // Pinned snapshot of LiquidityBreakRetestOptions' compiled-in defaults. A change to this value
    // means a default changed - confirm that was intentional before updating the constant, since
    // every previously-issued artifact's DefaultConfigurationHash becomes incompatible as a result.
    private const string PinnedLiquidityBreakRetestDefaultHash =
        "748b74302343043549c5d809b4db591a515351e108a7b559aef2e0b123e442d3";

    [Test]
    public void LiquidityBreakRetestDefaults_MatchPinnedSnapshot()
    {
        string hash = LiquidityBreakRetestCalibrationCompatibility.Instance.ComputeDefaultConfigurationHash();
        TestContext.Out.WriteLine($"Computed default-configuration hash: {hash}");
        Assert.That(hash, Is.EqualTo(PinnedLiquidityBreakRetestDefaultHash),
            "LiquidityBreakRetestOptions' compiled-in defaults changed. If this was an intentional " +
            "default change, update the pinned constant - but note every previously-produced " +
            "liquidity-break-retest calibration artifact becomes incompatible as a result.");
    }

    [Test]
    public void DefaultConfigurationHash_IsDeterministicAcrossCalls()
    {
        string first = LiquidityBreakRetestCalibrationCompatibility.Instance.ComputeDefaultConfigurationHash();
        string second = LiquidityBreakRetestCalibrationCompatibility.Instance.ComputeDefaultConfigurationHash();
        Assert.That(second, Is.EqualTo(first));
    }

    [Test]
    public void DefaultConfigurationHash_ChangesWhenADefaultChanges()
    {
        string baseline = IndicatorCalibrationHash.ComputeOfObject(new LiquidityBreakRetestOptions());
        string changed = IndicatorCalibrationHash.ComputeOfObject(
            new LiquidityBreakRetestOptions { MinimumAdx = 21m });
        Assert.That(changed, Is.Not.EqualTo(baseline));
    }
}
