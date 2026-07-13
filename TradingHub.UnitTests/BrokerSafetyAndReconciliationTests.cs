using Brokers.Models;
using Brokers.Reconciliation;
using Brokers.Safety;
using NUnit.Framework;

namespace TradingHub.UnitTests;

[TestFixture]
public sealed class BrokerSafetyAndReconciliationTests
{
    private static readonly InstrumentKey Instrument = new("FX:GBP/USD");

    [Test]
    public void BrokerExecutionSafety_BlocksUnavailableAccountAndDuplicateOrder()
    {
        var safety = new BrokerExecutionSafety();
        BrokerExecutionSafetyAssessment result = safety.Evaluate(
            new BrokerExecutionSafetyContext
            {
                Instrument = Instrument,
                Accounts = [new AccountSnapshot { AccountId = "demo", CanTrade = false }],
                OpenOrders =
                [
                    new BrokerOrder
                    {
                        BrokerOrderId = "order-1",
                        Instrument = Instrument,
                        Side = OrderSide.Buy,
                        Type = "LIMIT",
                        Status = "OPEN",
                        NormalizedStatus = OrderStatus.Open
                    }
                ]
            });

        Assert.Multiple(() =>
        {
            Assert.That(result.Approved, Is.False);
            Assert.That(result.Reasons, Has.Count.EqualTo(2));
            Assert.That(result.Summary, Does.Contain("trading is enabled"));
            Assert.That(result.Summary, Does.Contain("open order"));
        });
    }

    [Test]
    public void PositionReconciler_ReportsSideQuantityAndUnexpectedPositionMismatches()
    {
        var reconciler = new PositionReconciler();
        PositionReconciliationResult result = reconciler.Reconcile(
            [
                new ExpectedPosition
                {
                    Instrument = Instrument,
                    Side = OrderSide.Buy,
                    Quantity = 2m
                }
            ],
            [
                new BrokerPosition
                {
                    PositionId = "broker-1",
                    Instrument = Instrument,
                    Side = OrderSide.Sell,
                    Quantity = 1m
                },
                new BrokerPosition
                {
                    PositionId = "broker-2",
                    Instrument = new InstrumentKey("FX:EUR/USD"),
                    Side = OrderSide.Buy,
                    Quantity = 1m
                }
            ]);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsConsistent, Is.False);
            Assert.That(
                result.Mismatches.Select(mismatch => mismatch.Type),
                Does.Contain(PositionMismatchType.SideMismatch));
            Assert.That(
                result.Mismatches.Select(mismatch => mismatch.Type),
                Does.Contain(PositionMismatchType.UnexpectedAtBroker));
        });
    }

    [Test]
    public void PositionReconciler_AggregatesSameInstrumentPositionsBeforeComparison()
    {
        var reconciler = new PositionReconciler();
        PositionReconciliationResult result = reconciler.Reconcile(
            [
                new ExpectedPosition
                {
                    Instrument = Instrument,
                    Side = OrderSide.Buy,
                    Quantity = 3m
                }
            ],
            [
                new BrokerPosition
                {
                    PositionId = "broker-1",
                    Instrument = Instrument,
                    Side = OrderSide.Buy,
                    Quantity = 1m
                },
                new BrokerPosition
                {
                    PositionId = "broker-2",
                    Instrument = Instrument,
                    Side = OrderSide.Buy,
                    Quantity = 2m
                }
            ]);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsConsistent, Is.False);
            Assert.That(
                result.Mismatches.Select(mismatch => mismatch.Type),
                Is.EquivalentTo([PositionMismatchType.DuplicateBrokerPosition]));
        });
    }
}
