using Agent.Strategies.StructuralConfluence;
using Brokers.Models;
using ChartAnnotator.MarketData;
using Simulator.Calibration;

namespace Simulator.Tests;

/// <summary>
/// Blueprint §19 Phase 1 gate: "no existing default or successful baseline test changes." These
/// tests pin the current, pre-calibration <see cref="IndicatorConfluenceOptions"/> defaults and
/// the deterministic behavior of the new compatibility-identity/hashing primitives. Nothing here
/// touches or depends on the playbook's decision logic - calibration does not exist yet at this
/// phase, so there is no overlay to test; these tests exist so a future accidental change to the
/// strategy's compiled-in defaults fails loudly here rather than silently invalidating every
/// previously-produced calibration artifact.
/// </summary>
[TestFixture]
public sealed class IndicatorCalibrationCompatibilityTests
{
    // Pinned snapshot of IndicatorConfluenceOptions' compiled-in defaults as of this phase.
    // A change to this value means a default changed - confirm that was intentional before
    // updating the constant, since every previously-issued artifact's DefaultConfigurationHash
    // becomes incompatible the moment this changes.
    private const string PinnedIndicatorConfluenceDefaultHash =
        "8733a66d6d9dee5e026ac44dac7a64fc3b8820cbec097a401b3f4329bcc25121";

    [Test]
    public void IndicatorConfluenceDefaults_MatchPinnedSnapshot()
    {
        string hash = IndicatorConfluenceCalibrationCompatibility.Instance.ComputeDefaultConfigurationHash();
        TestContext.Out.WriteLine($"Computed default-configuration hash: {hash}");
        Assert.That(hash, Is.EqualTo(PinnedIndicatorConfluenceDefaultHash),
            "IndicatorConfluenceOptions' compiled-in defaults changed. If this was an intentional " +
            "default change, update the pinned constant - but note every previously-produced " +
            "indicator-calibration artifact for this strategy becomes incompatible as a result.");
    }

    [Test]
    public void DefaultConfigurationHash_IsDeterministicAcrossCalls()
    {
        string first = IndicatorConfluenceCalibrationCompatibility.Instance.ComputeDefaultConfigurationHash();
        string second = IndicatorConfluenceCalibrationCompatibility.Instance.ComputeDefaultConfigurationHash();
        Assert.That(second, Is.EqualTo(first));
    }

    [Test]
    public void DefaultConfigurationHash_ChangesWhenADefaultChanges()
    {
        string baseline = IndicatorCalibrationHash.ComputeOfObject(new IndicatorConfluenceOptions());
        string changed = IndicatorCalibrationHash.ComputeOfObject(
            new IndicatorConfluenceOptions { MinimumAdx = 21m });
        Assert.That(changed, Is.Not.EqualTo(baseline));
    }

    [Test]
    public void TimeframeTopologyHash_IsOrderIndependentAcrossEquivalentLists()
    {
        TimeframeTopology first = BuildTopology(
            confirmations: [BarInterval.Minutes(15), BarInterval.Hours(1)]);
        TimeframeTopology second = BuildTopology(
            confirmations: [BarInterval.Hours(1), BarInterval.Minutes(15)]);

        Assert.That(second.ComputeHash(), Is.EqualTo(first.ComputeHash()),
            "Two topologies describing the same effective interval set must hash identically " +
            "regardless of how the caller ordered the list.");
    }

    [Test]
    public void TimeframeTopologyHash_ChangesWhenAnIntervalChanges()
    {
        TimeframeTopology baseline = BuildTopology(confirmations: [BarInterval.Minutes(15)]);
        TimeframeTopology changed = BuildTopology(confirmations: [BarInterval.Minutes(30)]);

        Assert.That(changed.ComputeHash(), Is.Not.EqualTo(baseline.ComputeHash()));
    }

    [Test]
    public void CompatibilityIdentity_MatchesRequiresEveryField()
    {
        TimeframeTopology topology = BuildTopology(confirmations: [BarInterval.Minutes(15)]);
        CalibrationCompatibilityIdentity identity =
            IndicatorConfluenceCalibrationCompatibility.Instance.Describe(topology);
        CalibrationCompatibilityIdentity sameAgain =
            IndicatorConfluenceCalibrationCompatibility.Instance.Describe(topology);
        Assert.That(identity.Matches(sameAgain), Is.True);

        TimeframeTopology differentTopology = BuildTopology(confirmations: [BarInterval.Minutes(30)]);
        CalibrationCompatibilityIdentity differentIdentity =
            IndicatorConfluenceCalibrationCompatibility.Instance.Describe(differentTopology);
        Assert.That(identity.Matches(differentIdentity), Is.False);
    }

    private static TimeframeTopology BuildTopology(IReadOnlyList<BarInterval> confirmations) => new()
    {
        ExecutionInterval = BarInterval.Minutes(5),
        AnalysisBaseInterval = BarInterval.Minutes(1),
        SetupInterval = BarInterval.Minutes(15),
        ConfirmationIntervals = confirmations,
        TrendIntervals = [BarInterval.Hours(1)],
        ManagementIntervals = [],
        AlignmentPolicy = BaseCandleGapPolicy.ResetIncompleteBuckets,
        WarmupMinimumDays = 21
    };
}
