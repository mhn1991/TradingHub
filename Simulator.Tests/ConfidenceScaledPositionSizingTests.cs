using Agent.Models;
using Brokers.Models;
using RiskManager;

namespace Simulator.Tests;

[TestFixture]
public sealed class ConfidenceScaledPositionSizingTests
{
    [Test]
    public void Disabled_IgnoresConfidence_RegressionMatchesFixedMultiplier()
    {
        var sizer = new PositionSizer(new PositionSizingOptions
        {
            Mode = PositionSizingMode.FixedFractionalRisk,
            RiskPercentOfEquity = 1m,
            Leverage = 100m,
            MaximumAccountMarginUsagePercent = 100m,
            MaximumSinglePositionMarginPercent = 100m,
            EnableConfidenceScaledSizing = false
        });

        // Low confidence must not shrink sizing while the feature is disabled.
        PositionSizingResult result = sizer.Calculate(Context(confidence: 20m));

        Assert.Multiple(() =>
        {
            Assert.That(result.RiskBudget, Is.EqualTo(100m));
            Assert.That(result.Quantity, Is.EqualTo(10_000m));
        });
    }

    [Test]
    public void Enabled_ConfidenceAtOrBelowFloor_AppliesMinimumMultiplier()
    {
        var sizer = new PositionSizer(new PositionSizingOptions
        {
            Mode = PositionSizingMode.FixedFractionalRisk,
            RiskPercentOfEquity = 1m,
            Leverage = 100m,
            MaximumAccountMarginUsagePercent = 100m,
            MaximumSinglePositionMarginPercent = 100m,
            EnableConfidenceScaledSizing = true,
            ConfidenceMultiplierFloorConfidence = 40m,
            ConfidenceMultiplierCeilingConfidence = 85m,
            MinimumConfidenceMultiplier = 0.5m,
            MaximumConfidenceMultiplier = 1.0m
        });

        // Below the floor clamps to the same minimum multiplier as exactly-at-floor.
        PositionSizingResult atFloor = sizer.Calculate(Context(confidence: 40m));
        PositionSizingResult belowFloor = sizer.Calculate(Context(confidence: 20m));

        Assert.Multiple(() =>
        {
            Assert.That(atFloor.RiskBudget, Is.EqualTo(50m));
            Assert.That(atFloor.Quantity, Is.EqualTo(5_000m));
            Assert.That(belowFloor.RiskBudget, Is.EqualTo(50m));
            Assert.That(belowFloor.Quantity, Is.EqualTo(5_000m));
        });
    }

    [Test]
    public void Enabled_ConfidenceAtOrAboveCeiling_NeverExceedsMaximumMultiplier()
    {
        var sizer = new PositionSizer(new PositionSizingOptions
        {
            Mode = PositionSizingMode.FixedFractionalRisk,
            RiskPercentOfEquity = 1m,
            Leverage = 100m,
            MaximumAccountMarginUsagePercent = 100m,
            MaximumSinglePositionMarginPercent = 100m,
            EnableConfidenceScaledSizing = true,
            ConfidenceMultiplierFloorConfidence = 40m,
            ConfidenceMultiplierCeilingConfidence = 85m,
            MinimumConfidenceMultiplier = 0.5m,
            MaximumConfidenceMultiplier = 1.0m
        });

        // Above the ceiling clamps to 1.0 - a strong rule score never leverages size up.
        PositionSizingResult atCeiling = sizer.Calculate(Context(confidence: 85m));
        PositionSizingResult aboveCeiling = sizer.Calculate(Context(confidence: 95m));

        Assert.Multiple(() =>
        {
            Assert.That(atCeiling.RiskBudget, Is.EqualTo(100m));
            Assert.That(atCeiling.Quantity, Is.EqualTo(10_000m));
            Assert.That(aboveCeiling.RiskBudget, Is.EqualTo(100m));
            Assert.That(aboveCeiling.Quantity, Is.EqualTo(10_000m));
        });
    }

    [Test]
    public void Enabled_MidConfidence_LinearlyInterpolatesMultiplier()
    {
        var sizer = new PositionSizer(new PositionSizingOptions
        {
            Mode = PositionSizingMode.FixedFractionalRisk,
            RiskPercentOfEquity = 1m,
            Leverage = 100m,
            MaximumAccountMarginUsagePercent = 100m,
            MaximumSinglePositionMarginPercent = 100m,
            EnableConfidenceScaledSizing = true,
            ConfidenceMultiplierFloorConfidence = 40m,
            ConfidenceMultiplierCeilingConfidence = 85m,
            MinimumConfidenceMultiplier = 0.5m,
            MaximumConfidenceMultiplier = 1.0m
        });

        // Halfway between floor (40) and ceiling (85) interpolates to a 0.75 multiplier.
        PositionSizingResult result = sizer.Calculate(Context(confidence: 62.5m));

        Assert.Multiple(() =>
        {
            Assert.That(result.RiskBudget, Is.EqualTo(75m));
            Assert.That(result.Quantity, Is.EqualTo(7_500m));
        });
    }

    [Test]
    public void Enabled_FixedQuantityMode_AlsoScalesByConfidence()
    {
        var sizer = new PositionSizer(new PositionSizingOptions
        {
            Mode = PositionSizingMode.FixedQuantity,
            FixedQuantity = 1_000m,
            EnableConfidenceScaledSizing = true,
            ConfidenceMultiplierFloorConfidence = 40m,
            ConfidenceMultiplierCeilingConfidence = 85m,
            MinimumConfidenceMultiplier = 0.5m,
            MaximumConfidenceMultiplier = 1.0m
        });
        AgentDecision decision = new()
        {
            Action = AgentAction.Buy,
            Instrument = new InstrumentKey("FX:GBP/USD"),
            SuggestedQuantity = 1m,
            ReferencePrice = 1m,
            StopLossPrice = 0.99m,
            Confidence = 40m,
            CreatedAt = DateTimeOffset.UtcNow,
            Reason = "test"
        };

        PositionSizingResult result = sizer.Calculate(new PositionSizingContext
        {
            Decision = decision,
            RequestedQuantity = 0m,
            Accounts = [new AccountSnapshot { AccountId = "a", Balance = 10_000m }],
            Positions = [],
            QuoteToAccountCurrencyRate = 1m
        });

        Assert.That(result.Quantity, Is.EqualTo(500m));
    }

    private static PositionSizingContext Context(decimal confidence = 80m)
    {
        AgentDecision decision = new()
        {
            Action = AgentAction.Buy,
            Instrument = new InstrumentKey("FX:GBP/USD"),
            SuggestedQuantity = 1m,
            ReferencePrice = 1m,
            StopLossPrice = 0.99m,
            Confidence = confidence,
            CreatedAt = DateTimeOffset.UtcNow,
            Reason = "test"
        };

        return new PositionSizingContext
        {
            Decision = decision,
            RequestedQuantity = 1m,
            Accounts = [new AccountSnapshot { AccountId = "a", Balance = 10_000m }],
            Positions = [],
            QuoteToAccountCurrencyRate = 1m
        };
    }
}
