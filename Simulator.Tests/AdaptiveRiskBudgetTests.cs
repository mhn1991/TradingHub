using Agent.Models;
using Brokers.Models;
using RiskManager;

namespace Simulator.Tests;

[TestFixture]
public sealed class AdaptiveRiskBudgetTests
{
    [Test]
    public void Multipliers_ComposeAndNeverExceedOne()
    {
        var policy = new RiskBudgetPolicy(new AdaptiveRiskOptions { Enabled = true });
        RiskBudgetDecision result = policy.Evaluate(new RiskBudgetContext
        {
            AccountEquity = 10_000m,
            DrawdownPercent = 3m,
            VolatilityPercentile = 90m,
            RegimeMultiplier = 0.8m,
            LiquidityMultiplier = 0.5m,
            CorrelationMultiplier = 0.7m,
            StrategyAllocationMultiplier = 1m,
            EquityProtectionMultiplier = 0.5m,
            CalibrationMultiplier = 1m
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.DrawdownMultiplier, Is.EqualTo(0.75m));
            Assert.That(result.VolatilityMultiplier, Is.EqualTo(0.70m));
            Assert.That(result.CombinedMultiplier, Is.EqualTo(0.0735m));
            Assert.That(result.CombinedMultiplier, Is.LessThanOrEqualTo(1m));
        });
    }

    [Test]
    public void FractionalSizer_AppliesRiskBudgetBeforeQuantityCalculation()
    {
        var sizer = new PositionSizer(new PositionSizingOptions
        {
            Mode = PositionSizingMode.FixedFractionalRisk,
            RiskPercentOfEquity = 1m,
            Leverage = 100m,
            MaximumAccountMarginUsagePercent = 100m,
            MaximumSinglePositionMarginPercent = 100m
        });
        AgentDecision decision = new()
        {
            Action = AgentAction.Buy,
            Instrument = new InstrumentKey("FX:GBP/USD"),
            SuggestedQuantity = 1m,
            ReferencePrice = 1m,
            StopLossPrice = 0.99m,
            Confidence = 80m,
            CreatedAt = DateTimeOffset.UtcNow,
            Reason = "test"
        };

        PositionSizingResult result = sizer.Calculate(new PositionSizingContext
        {
            Decision = decision,
            RequestedQuantity = 1m,
            Accounts = [new AccountSnapshot { AccountId = "a", Balance = 10_000m }],
            Positions = [],
            QuoteToAccountCurrencyRate = 1m,
            RiskBudgetMultiplier = 0.5m
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.RiskBudget, Is.EqualTo(50m));
            Assert.That(result.Quantity, Is.EqualTo(5_000m));
            Assert.That(result.EstimatedLossAtStop, Is.EqualTo(50m));
        });
    }

    [Test]
    public void SixPercentDrawdown_StopsNewRisk()
    {
        RiskBudgetDecision result = new RiskBudgetPolicy(new AdaptiveRiskOptions { Enabled = true })
            .Evaluate(new RiskBudgetContext { AccountEquity = 10_000m, DrawdownPercent = 6m });
        Assert.That(result.CombinedMultiplier, Is.Zero);
    }
}
