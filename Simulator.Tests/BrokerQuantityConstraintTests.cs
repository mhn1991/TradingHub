using Agent.Models;
using Brokers.Models;
using RiskManager;

namespace Simulator.Tests;

/// <summary>
/// Cover for the 2026-09-03 audit finding that broker-supplied per-instrument quantity
/// granularity reached the portfolio sizing path but not the ExecutionCoordinator one, which
/// sized against its configured global step and minimum instead. A quantity off the broker's
/// real step is refused at submission, and a rejected order is dropped rather than resized, so
/// the trade is simply lost. <see cref="PositionSizingOptions.WithBrokerConstraints"/> is now
/// the single definition of the rule and the sizer applies it from the context.
/// </summary>
[TestFixture]
public sealed class BrokerQuantityConstraintTests
{
    [Test]
    public void WithBrokerConstraints_TakesTheStricterBoundOnEachSide()
    {
        var configured = new PositionSizingOptions
        {
            MinimumQuantity = 1m,
            MaximumQuantity = 10_000m,
            QuantityStep = 1m
        };

        PositionSizingOptions effective = configured.WithBrokerConstraints(Metadata(
            minimum: 100m, step: 50m, maximum: 5_000m));

        Assert.Multiple(() =>
        {
            Assert.That(effective.MinimumQuantity, Is.EqualTo(100m), "the larger minimum wins");
            Assert.That(effective.MaximumQuantity, Is.EqualTo(5_000m), "the smaller maximum wins");
            Assert.That(effective.QuantityStep, Is.EqualTo(50m), "the coarser step wins");
        });
    }

    [Test]
    public void WithBrokerConstraints_KeepsTheConfiguredValueWhenItIsStricter()
    {
        var configured = new PositionSizingOptions
        {
            MinimumQuantity = 500m,
            MaximumQuantity = 1_000m,
            QuantityStep = 100m
        };

        PositionSizingOptions effective = configured.WithBrokerConstraints(Metadata(
            minimum: 1m, step: 1m, maximum: 1_000_000m));

        Assert.Multiple(() =>
        {
            Assert.That(effective.MinimumQuantity, Is.EqualTo(500m));
            Assert.That(effective.MaximumQuantity, Is.EqualTo(1_000m));
            Assert.That(effective.QuantityStep, Is.EqualTo(100m));
        });
    }

    [Test]
    public void WithBrokerConstraints_IsIdentityWithoutMetadata()
    {
        var configured = new PositionSizingOptions { MinimumQuantity = 7m, QuantityStep = 3m };

        Assert.That(configured.WithBrokerConstraints(null), Is.SameAs(configured));
    }

    [Test]
    public void Sizer_RoundsDownToTheBrokerStep_NotTheConfiguredOne()
    {
        // Configured step 1 would admit 1,234; the broker only accepts multiples of 1,000.
        var sizer = new PositionSizer(new PositionSizingOptions
        {
            Mode = PositionSizingMode.FixedQuantity,
            FixedQuantity = 1_234m,
            MinimumQuantity = 1m,
            QuantityStep = 1m,
            Leverage = 100m,
            MaximumAccountMarginUsagePercent = 100m,
            MaximumSinglePositionMarginPercent = 100m
        });

        PositionSizingResult result = sizer.Calculate(Context(
            requested: 1_234m,
            spec: Metadata(minimum: 1m, step: 1_000m, maximum: null)));

        Assert.Multiple(() =>
        {
            Assert.That(result.Approved, Is.True, result.Reason);
            Assert.That(result.Quantity, Is.EqualTo(1_000m));
        });
    }

    [Test]
    public void Sizer_RejectsBelowTheBrokerMinimum_WhichTheConfiguredMinimumWouldHaveAdmitted()
    {
        var sizer = new PositionSizer(new PositionSizingOptions
        {
            Mode = PositionSizingMode.FixedQuantity,
            FixedQuantity = 50m,
            MinimumQuantity = 1m,
            QuantityStep = 1m,
            Leverage = 100m,
            MaximumAccountMarginUsagePercent = 100m,
            MaximumSinglePositionMarginPercent = 100m
        });

        PositionSizingResult result = sizer.Calculate(Context(
            requested: 50m,
            spec: Metadata(minimum: 100m, step: 1m, maximum: null)));

        Assert.Multiple(() =>
        {
            Assert.That(result.Approved, Is.False);
            Assert.That(result.ReasonCode, Is.EqualTo("QuantityBelowMinimum"));
        });
    }

    [Test]
    public void Sizer_WithoutABrokerSpec_KeepsTheConfiguredBehaviour()
    {
        var sizer = new PositionSizer(new PositionSizingOptions
        {
            Mode = PositionSizingMode.FixedQuantity,
            FixedQuantity = 1_234m,
            MinimumQuantity = 1m,
            QuantityStep = 1m,
            Leverage = 100m,
            MaximumAccountMarginUsagePercent = 100m,
            MaximumSinglePositionMarginPercent = 100m
        });

        PositionSizingResult result = sizer.Calculate(Context(requested: 1_234m, spec: null));

        Assert.Multiple(() =>
        {
            Assert.That(result.Approved, Is.True, result.Reason);
            Assert.That(result.Quantity, Is.EqualTo(1_234m));
        });
    }

    [Test]
    public void Sizer_RejectsRatherThanThrows_WhenBrokerBoundsContradictTheConfiguredOnes()
    {
        // Broker maximum below the configured minimum: the rule throws, and the sizer must
        // convert that into a rejection so it stays fail-closed like every other path.
        var sizer = new PositionSizer(new PositionSizingOptions
        {
            Mode = PositionSizingMode.FixedQuantity,
            FixedQuantity = 100m,
            MinimumQuantity = 1_000m,
            QuantityStep = 1m
        });

        PositionSizingResult result = sizer.Calculate(Context(
            requested: 100m,
            spec: Metadata(minimum: 1m, step: 1m, maximum: 10m)));

        Assert.Multiple(() =>
        {
            Assert.That(result.Approved, Is.False);
            Assert.That(result.ReasonCode, Is.EqualTo("BrokerQuantityConstraintConflict"));
            Assert.That(result.Quantity, Is.Zero);
        });
    }

    private static InstrumentTradingMetadata Metadata(decimal minimum, decimal step, decimal? maximum) => new()
    {
        Instrument = new InstrumentKey("FX:GBP/USD"),
        MinimumQuantity = minimum,
        MaximumOrderQuantity = maximum,
        QuantityStep = step,
        PriceIncrement = 0.00001m
    };

    private static PositionSizingContext Context(decimal requested, InstrumentTradingMetadata? spec) => new()
    {
        Decision = new AgentDecision
        {
            Action = AgentAction.Buy,
            Instrument = new InstrumentKey("FX:GBP/USD"),
            SuggestedQuantity = requested,
            ReferencePrice = 1.25m,
            StopLossPrice = 1.24m,
            Confidence = 80m,
            CreatedAt = DateTimeOffset.UtcNow,
            Reason = "test"
        },
        RequestedQuantity = requested,
        Accounts = [new AccountSnapshot { AccountId = "a", Balance = 1_000_000m }],
        Positions = [],
        QuoteToAccountCurrencyRate = 1m,
        BrokerQuantitySpec = spec
    };
}
