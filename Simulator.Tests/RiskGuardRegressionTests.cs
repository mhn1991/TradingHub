using Agent.Models;
using Brokers.Models;
using RiskManager;

namespace Simulator.Tests;

/// <summary>
/// Regression cover for the risk-formula guards found in the 2026-08-19 audit: a hard-zero
/// risk band must not be resurrected by the combined floor, schedule bands must stay
/// reachable and fail safe out of range, and the sizer must reject rather than throw when a
/// quantity is not representable.
/// </summary>
[TestFixture]
public sealed class RiskGuardRegressionTests
{
    [Test]
    public void HardZeroDrawdownBand_IsNotResurrectedByTheCombinedFloor()
    {
        // The default schedule's terminal band is an explicit stop-trading band (0 at >= 6%).
        // Before the fix, Math.Clamp raised it back to the floor and kept sizing positions.
        var policy = new RiskBudgetPolicy(new AdaptiveRiskOptions
        {
            Enabled = true,
            MinimumCombinedRiskMultiplier = 0.25m
        });

        RiskBudgetDecision result = policy.Evaluate(new RiskBudgetContext
        {
            AccountEquity = 10_000m,
            DrawdownPercent = 8m,
            VolatilityPercentile = 10m
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.DrawdownMultiplier, Is.Zero);
            Assert.That(result.CombinedMultiplier, Is.Zero);
            Assert.That(result.ReasonCode, Is.EqualTo("RiskBudgetRejected"));
        });
    }

    [Test]
    public void CombinedFloor_StillAppliesToANonZeroProduct()
    {
        var policy = new RiskBudgetPolicy(new AdaptiveRiskOptions
        {
            Enabled = true,
            MinimumCombinedRiskMultiplier = 0.5m
        });

        // 0.75 drawdown x 1.0 volatility x 0.1 liquidity = 0.075, floored back up to 0.5.
        RiskBudgetDecision result = policy.Evaluate(new RiskBudgetContext
        {
            AccountEquity = 10_000m,
            DrawdownPercent = 3m,
            VolatilityPercentile = 10m,
            LiquidityMultiplier = 0.1m
        });

        Assert.That(result.CombinedMultiplier, Is.EqualTo(0.5m));
    }

    [Test]
    public void OpenEndedBandBeforeTheFinalBand_IsRejected()
    {
        // An open-ended band matches every larger value in Resolve, so the band after it
        // would never be reachable. That must fail validation rather than size silently wrong.
        var options = new AdaptiveRiskOptions
        {
            Enabled = true,
            DrawdownSchedule =
            [
                new() { FromInclusive = 0m, ToExclusive = null, Multiplier = 1m },
                new() { FromInclusive = 0m, ToExclusive = 5m, Multiplier = 0m }
            ]
        };

        Assert.That(() => new RiskBudgetPolicy(options), Throws.ArgumentException);
    }

    [Test]
    public void SingleOpenEndedBand_RemainsValid()
    {
        var options = new AdaptiveRiskOptions
        {
            Enabled = true,
            VolatilityPercentileSchedule =
            [
                new() { FromInclusive = 0m, ToExclusive = null, Multiplier = 0.5m }
            ]
        };

        RiskBudgetDecision result = new RiskBudgetPolicy(options).Evaluate(new RiskBudgetContext
        {
            AccountEquity = 10_000m,
            VolatilityPercentile = 99m
        });

        Assert.That(result.VolatilityMultiplier, Is.EqualTo(0.5m));
    }

    [Test]
    public void DrawdownBelowTheFirstBand_UsesTheMildestMultiplier()
    {
        var options = new AdaptiveRiskOptions
        {
            Enabled = true,
            DrawdownSchedule =
            [
                new() { FromInclusive = 10m, ToExclusive = 20m, Multiplier = 0.5m },
                new() { FromInclusive = 20m, ToExclusive = null, Multiplier = 0m }
            ]
        };

        // 5% drawdown is milder than every band. Before the fix this fell through to the
        // terminal band and halted trading outright.
        RiskBudgetDecision result = new RiskBudgetPolicy(options).Evaluate(new RiskBudgetContext
        {
            AccountEquity = 10_000m,
            DrawdownPercent = 5m,
            VolatilityPercentile = 10m
        });

        Assert.That(result.DrawdownMultiplier, Is.EqualTo(0.5m));
    }

    [Test]
    public void DrawdownAboveAClosedFinalBand_StaysAtTheMostPunitiveMultiplier()
    {
        var options = new AdaptiveRiskOptions
        {
            Enabled = true,
            DrawdownSchedule =
            [
                new() { FromInclusive = 0m, ToExclusive = 5m, Multiplier = 1m },
                new() { FromInclusive = 5m, ToExclusive = 10m, Multiplier = 0m }
            ]
        };

        RiskBudgetDecision result = new RiskBudgetPolicy(options).Evaluate(new RiskBudgetContext
        {
            AccountEquity = 10_000m,
            DrawdownPercent = 50m,
            VolatilityPercentile = 10m
        });

        Assert.That(result.DrawdownMultiplier, Is.Zero);
    }

    [Test]
    public void UnrepresentableRiskBasedQuantity_IsRejectedRatherThanThrowing()
    {
        // perUnitLoss = 1e-24 distance x 1e-4 multiplier = 1e-28, so budget/perUnitLoss
        // exceeds decimal's range. Every other failure here rejects; this one used to throw.
        var sizer = new PositionSizer(new PositionSizingOptions
        {
            Mode = PositionSizingMode.FixedCashRisk,
            FixedCashRisk = 1_000m,
            Leverage = 100m,
            MaximumAccountMarginUsagePercent = 100m,
            MaximumSinglePositionMarginPercent = 100m
        });

        PositionSizingResult result = sizer.Calculate(new PositionSizingContext
        {
            Decision = Decision(reference: 1m, stop: 1m - 0.000000000000000000000001m),
            RequestedQuantity = 1m,
            Accounts = [new AccountSnapshot { AccountId = "a", Balance = 10_000m }],
            Positions = [],
            QuoteToAccountCurrencyRate = 1m,
            InstrumentSpec = new InstrumentRiskSpec { ContractMultiplier = 0.0001m }
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.Approved, Is.False);
            Assert.That(result.ReasonCode, Is.EqualTo("UnboundedQuantity"));
            Assert.That(result.Quantity, Is.Zero);
        });
    }

    [Test]
    public void UnrepresentableFixedQuantityRounding_FailsClosed()
    {
        // value/step overflows inside RoundDown; it must fall back to zero and be rejected
        // by the MinimumQuantity check rather than crashing the sizer.
        var sizer = new PositionSizer(new PositionSizingOptions
        {
            Mode = PositionSizingMode.FixedQuantity,
            QuantityStep = 0.0000000000000000000000000001m,
            MinimumQuantity = 1m
        });

        PositionSizingResult result = sizer.Calculate(new PositionSizingContext
        {
            Decision = Decision(reference: 1m, stop: 0.99m),
            RequestedQuantity = 10_000_000_000m,
            Accounts = [new AccountSnapshot { AccountId = "a", Balance = 10_000m }],
            Positions = [],
            QuoteToAccountCurrencyRate = 1m
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.Approved, Is.False);
            Assert.That(result.ReasonCode, Is.EqualTo("QuantityBelowMinimum"));
        });
    }

    [Test]
    public void MissingConversionRate_DoesNotReportAFabricatedOpenRisk()
    {
        // Substituting a 1.0 rate produced a real-looking OpenRiskAccountCurrency computed at
        // the wrong rate (the RSK-01 failure mode). It must stay null.
        var manager = new PreTradeRiskManager(new PreTradeRiskOptions
        {
            MaximumOpenRiskPercentOfEquity = 1.5m,
            AssumedOpenPositionRiskDistancePercentOfPrice = 0.5m
        });

        RiskAssessment assessment = manager.Evaluate(new PreTradeRiskContext
        {
            Decision = Decision(reference: 1m, stop: 0.99m),
            Quantity = 1_000m,
            Accounts = [new AccountSnapshot { AccountId = "a", Balance = 10_000m }],
            Positions =
            [
                new BrokerPosition
                {
                    PositionId = "p1",
                    Instrument = new InstrumentKey("FX:EUR/USD"),
                    Side = OrderSide.Buy,
                    Quantity = 5_000m,
                    AveragePrice = 1.1m
                }
            ],
            QuoteToAccountCurrencyRate = 0m
        });

        Assert.Multiple(() =>
        {
            Assert.That(assessment.Approved, Is.False);
            Assert.That(assessment.OpenRiskAccountCurrency, Is.Null);
            Assert.That(assessment.ProjectedOpenRiskAccountCurrency, Is.Null);
        });
    }

    private static AgentDecision Decision(decimal reference, decimal stop) => new()
    {
        Action = AgentAction.Buy,
        Instrument = new InstrumentKey("FX:GBP/USD"),
        SuggestedQuantity = 1m,
        ReferencePrice = reference,
        StopLossPrice = stop,
        Confidence = 80m,
        CreatedAt = DateTimeOffset.UtcNow,
        Reason = "test"
    };
}
