using Agent.Strategies;
using ChartAnnotator.Regime;

namespace Simulator.Tests;

[TestFixture]
public sealed class RegimeRoutingPolicyTests
{
    private static MarketRegimeSnapshot Snapshot(
        MarketRegime regime,
        decimal confidence = 80m,
        bool isTradeable = true,
        string reasonCode = "TestReason") => new()
    {
        Regime = regime,
        Confidence = confidence,
        ConfirmedAt = DateTimeOffset.UtcNow,
        AgeCandles = 5,
        Contributions = [],
        ReasonCode = reasonCode,
        IsTradeable = isTradeable
    };

    [Test]
    public void Disabled_ReturnsNeutralPassthroughRegardlessOfRegime()
    {
        var options = new MarketRegimePolicyOptions { Enabled = false };
        RegimeGateResult result = RegimeRoutingPolicy.Evaluate(
            Snapshot(MarketRegime.Compression), options);

        Assert.Multiple(() =>
        {
            Assert.That(result.RoutingEnabled, Is.False);
            Assert.That(result.AllowNewEntries, Is.True);
        });
    }

    [Test]
    public void Compression_BlocksNewEntriesWithRegimeSpecificReasonCode()
    {
        var options = new MarketRegimePolicyOptions { Enabled = true };
        RegimeGateResult result = RegimeRoutingPolicy.Evaluate(
            Snapshot(MarketRegime.Compression), options);

        Assert.Multiple(() =>
        {
            Assert.That(result.RoutingEnabled, Is.True);
            Assert.That(result.AllowNewEntries, Is.False);
            Assert.That(result.ReasonCode, Is.EqualTo("RegimeBlocked:Compression"));
            Assert.That(result.Policy.RiskMultiplier, Is.EqualTo(0m));
        });
    }

    [Test]
    public void HighVolatilityDisorder_BlocksNewEntries()
    {
        var options = new MarketRegimePolicyOptions { Enabled = true };
        RegimeGateResult result = RegimeRoutingPolicy.Evaluate(
            Snapshot(MarketRegime.HighVolatilityDisorder), options);

        Assert.That(result.AllowNewEntries, Is.False);
    }

    [Test]
    public void IlliquidUnsafe_IsNotTradeable_BlocksBeforePolicyLookup()
    {
        var options = new MarketRegimePolicyOptions { Enabled = true };
        RegimeGateResult result = RegimeRoutingPolicy.Evaluate(
            Snapshot(MarketRegime.IlliquidUnsafe, isTradeable: false, reasonCode: "SpreadExceedsHardLimit"),
            options);

        Assert.Multiple(() =>
        {
            Assert.That(result.AllowNewEntries, Is.False);
            Assert.That(result.ReasonCode, Is.EqualTo($"RegimeBlocked:{MarketRegime.IlliquidUnsafe}"));
            Assert.That(result.Explanation, Does.Contain("SpreadExceedsHardLimit"));
        });
    }

    [Test]
    public void Range_AllowsEntriesWithReducedRiskMultiplier()
    {
        var options = new MarketRegimePolicyOptions { Enabled = true };
        RegimeGateResult result = RegimeRoutingPolicy.Evaluate(
            Snapshot(MarketRegime.Range), options);

        Assert.Multiple(() =>
        {
            Assert.That(result.AllowNewEntries, Is.True);
            Assert.That(result.Policy.RiskMultiplier, Is.EqualTo(0.6m));
        });
    }

    [Test]
    public void BreakoutExpansionUp_AllowsEntriesWithModerateRiskMultiplier()
    {
        var options = new MarketRegimePolicyOptions { Enabled = true };
        RegimeGateResult result = RegimeRoutingPolicy.Evaluate(
            Snapshot(MarketRegime.BreakoutExpansionUp), options);

        Assert.Multiple(() =>
        {
            Assert.That(result.AllowNewEntries, Is.True);
            Assert.That(result.Policy.RiskMultiplier, Is.EqualTo(0.85m));
        });
    }

    [Test]
    public void TrendingUp_AllowsFullRiskMultiplier()
    {
        var options = new MarketRegimePolicyOptions { Enabled = true };
        RegimeGateResult result = RegimeRoutingPolicy.Evaluate(
            Snapshot(MarketRegime.TrendingUp), options);

        Assert.Multiple(() =>
        {
            Assert.That(result.AllowNewEntries, Is.True);
            Assert.That(result.Policy.RiskMultiplier, Is.EqualTo(1m));
        });
    }

    [Test]
    public void Unknown_IsANeutralPassthrough()
    {
        var options = new MarketRegimePolicyOptions { Enabled = true };
        RegimeGateResult result = RegimeRoutingPolicy.Evaluate(
            Snapshot(MarketRegime.Unknown), options);

        Assert.Multiple(() =>
        {
            Assert.That(result.AllowNewEntries, Is.True);
            Assert.That(result.Policy.RiskMultiplier, Is.EqualTo(1m));
        });
    }

    [Test]
    public void ConfidenceBelowMinimum_BlocksAnOtherwiseAllowedRegime()
    {
        var options = new MarketRegimePolicyOptions { Enabled = true, MinimumRegimeConfidence = 50m };
        RegimeGateResult result = RegimeRoutingPolicy.Evaluate(
            Snapshot(MarketRegime.TrendingUp, confidence: 30m), options);

        Assert.Multiple(() =>
        {
            Assert.That(result.AllowNewEntries, Is.False);
            Assert.That(result.ReasonCode, Is.EqualTo("RegimeConfidenceTooLow"));
        });
    }

    [Test]
    public void DefaultPolicies_RejectAnUnconfiguredRegimeAsNeutralPassthrough()
    {
        var options = new MarketRegimePolicyOptions
        {
            Enabled = true,
            Policies = new Dictionary<MarketRegime, RegimeStrategyPolicy>()
        };
        RegimeGateResult result = RegimeRoutingPolicy.Evaluate(
            Snapshot(MarketRegime.Range), options);

        Assert.Multiple(() =>
        {
            Assert.That(result.AllowNewEntries, Is.True);
            Assert.That(result.Policy.RiskMultiplier, Is.EqualTo(1m));
        });
    }
}
