using Brokers.Models;
using LiveTrading.Execution;
using LiveTrading.Portfolio;
using LiveTrading.Registry;
using NUnit.Framework;

namespace LiveTrading.Tests.Phase3;

[TestFixture]
public sealed class LiveOrderPositionRegistryTests
{
    [Test]
    public void BrokerTransaction_IsAppliedIdempotently()
    {
        var registry = new LiveOrderPositionRegistry();
        var approved = Phase3TestData.ApprovedDecision();
        string clientOrderId = LiveExecutionGateway.CreateClientOrderId(approved);
        registry.RegisterApproved(approved, clientOrderId, approved.Candidate.DecisionTime);
        registry.ApplySubmission(clientOrderId, new OrderSubmission
        {
            ClientOrderId = clientOrderId,
            BrokerOrderId = "broker-order-1",
            Status = SubmissionStatus.Accepted,
            Certainty = ExecutionCertainty.Accepted
        }, approved.Candidate.DecisionTime);
        var fill = new OrderEvent
        {
            BrokerTransactionId = "transaction-1",
            BrokerTradeId = "trade-1",
            BrokerOrderId = "broker-order-1",
            ClientOrderId = clientOrderId,
            Instrument = Phase3TestData.Instrument,
            Type = OrderEventType.Filled,
            Timestamp = approved.Candidate.DecisionTime.AddSeconds(1),
            FillPrice = 1.1001m,
            FillQuantity = 20_000m
        };

        RegistryApplyResult first = registry.Apply(fill);
        RegistryApplyResult duplicate = registry.Apply(fill);

        Assert.Multiple(() =>
        {
            Assert.That(first.Applied, Is.True);
            Assert.That(duplicate.Duplicate, Is.True);
            Assert.That(registry.Snapshot.Orders.Single().FilledQuantity, Is.EqualTo(20_000m));
            Assert.That(registry.Snapshot.Orders.Single().BrokerTradeId, Is.EqualTo("trade-1"));
            Assert.That(registry.Snapshot.Positions, Has.Count.EqualTo(1));
            Assert.That(registry.Snapshot.Positions.Single().StrategyId, Is.EqualTo("improved"));
            Assert.That(registry.Snapshot.Positions.Single().ProtectiveStopPrice, Is.EqualTo(1.0950m));
        });
    }

    [Test]
    public void EntryManagementProfileId_PropagatesFromDecision_ThroughOrderIntoPosition()
    {
        var registry = new LiveOrderPositionRegistry();
        PortfolioApprovedDecision approved = Phase3TestData.ApprovedDecision();
        approved = approved with { Decision = approved.Decision with { RegimeManagementProfileId = "trend-runner" } };
        string clientOrderId = LiveExecutionGateway.CreateClientOrderId(approved);
        registry.RegisterApproved(approved, clientOrderId, approved.Candidate.DecisionTime);

        Assert.That(registry.Snapshot.Orders.Single().EntryManagementProfileId, Is.EqualTo("trend-runner"));

        registry.ApplySubmission(clientOrderId, new OrderSubmission
        {
            ClientOrderId = clientOrderId,
            BrokerOrderId = "broker-order-1",
            Status = SubmissionStatus.Accepted,
            Certainty = ExecutionCertainty.Accepted
        }, approved.Candidate.DecisionTime);
        registry.Apply(new OrderEvent
        {
            BrokerTransactionId = "transaction-1",
            BrokerTradeId = "trade-1",
            BrokerOrderId = "broker-order-1",
            ClientOrderId = clientOrderId,
            Instrument = Phase3TestData.Instrument,
            Type = OrderEventType.Filled,
            Timestamp = approved.Candidate.DecisionTime.AddSeconds(1),
            FillPrice = 1.1001m,
            FillQuantity = 20_000m
        });

        Assert.That(registry.Snapshot.Positions.Single().EntryManagementProfileId, Is.EqualTo("trend-runner"));
    }

    [Test]
    public void EntryManagementProfileId_DefaultsWhenRegimeRoutingIsDisabled()
    {
        var registry = new LiveOrderPositionRegistry();
        PortfolioApprovedDecision approved = Phase3TestData.ApprovedDecision();
        Assert.That(approved.Decision.RegimeManagementProfileId, Is.Null, "fixture assumption: no regime profile set");
        string clientOrderId = LiveExecutionGateway.CreateClientOrderId(approved);

        registry.RegisterApproved(approved, clientOrderId, approved.Candidate.DecisionTime);

        Assert.That(registry.Snapshot.Orders.Single().EntryManagementProfileId, Is.EqualTo("default"));
    }

    [Test]
    public void ClientOrderId_IsStableAndWithinOandaLimit()
    {
        var approved = Phase3TestData.ApprovedDecision();
        string first = LiveExecutionGateway.CreateClientOrderId(approved);
        string second = LiveExecutionGateway.CreateClientOrderId(approved);

        Assert.Multiple(() =>
        {
            Assert.That(second, Is.EqualTo(first));
            Assert.That(first.Length, Is.LessThanOrEqualTo(128));
            Assert.That(first, Does.StartWith("th-live-"));
        });
    }
    [Test]
    public void AppliedBrokerEventIds_SurviveCheckpointRestore()
    {
        var registry = new LiveOrderPositionRegistry();
        PortfolioApprovedDecision approved = Phase3TestData.ApprovedDecision();
        string clientOrderId = LiveExecutionGateway.CreateClientOrderId(approved);
        registry.RegisterApproved(approved, clientOrderId, approved.Candidate.DecisionTime);
        registry.ApplySubmission(clientOrderId, new OrderSubmission
        {
            ClientOrderId = clientOrderId,
            BrokerOrderId = "broker-order-1",
            Status = SubmissionStatus.Accepted,
            Certainty = ExecutionCertainty.Accepted
        }, approved.Candidate.DecisionTime);
        var fill = new OrderEvent
        {
            BrokerTransactionId = "transaction-restore-1",
            BrokerTradeId = "trade-restore-1",
            BrokerOrderId = "broker-order-1",
            ClientOrderId = clientOrderId,
            Instrument = Phase3TestData.Instrument,
            Type = OrderEventType.Filled,
            PositionEffect = OrderPositionEffect.OpenOrIncrease,
            Timestamp = approved.Candidate.DecisionTime.AddSeconds(1),
            FillPrice = 1.1001m,
            FillQuantity = 20_000m
        };
        registry.Apply(fill);

        var restored = new LiveOrderPositionRegistry();
        restored.Restore(registry.Snapshot);
        RegistryApplyResult duplicate = restored.Apply(fill);

        Assert.Multiple(() =>
        {
            Assert.That(duplicate.Duplicate, Is.True);
            Assert.That(restored.Snapshot.Orders.Single().FilledQuantity, Is.EqualTo(20_000m));
            Assert.That(restored.Snapshot.Positions.Single().Quantity, Is.EqualTo(20_000m));
            Assert.That(restored.Snapshot.AppliedEventIds, Does.Contain("transaction-restore-1"));
        });
    }

    [Test]
    public void ReducingFill_ReducesOwnedPositionAndNeverCreatesSecondExposure()
    {
        var registry = new LiveOrderPositionRegistry();
        PortfolioApprovedDecision approved = Phase3TestData.ApprovedDecision();
        string clientOrderId = LiveExecutionGateway.CreateClientOrderId(approved);
        registry.RegisterApproved(approved, clientOrderId, approved.Candidate.DecisionTime);
        registry.ApplySubmission(clientOrderId, new OrderSubmission
        {
            ClientOrderId = clientOrderId,
            BrokerOrderId = "broker-order-1",
            Status = SubmissionStatus.Accepted,
            Certainty = ExecutionCertainty.Accepted
        }, approved.Candidate.DecisionTime);
        registry.Apply(new OrderEvent
        {
            BrokerTransactionId = "transaction-open",
            BrokerTradeId = "trade-1",
            BrokerOrderId = "broker-order-1",
            ClientOrderId = clientOrderId,
            Instrument = Phase3TestData.Instrument,
            Type = OrderEventType.Filled,
            PositionEffect = OrderPositionEffect.OpenOrIncrease,
            Timestamp = approved.Candidate.DecisionTime.AddSeconds(1),
            FillPrice = 1.1001m,
            FillQuantity = 20_000m
        });

        RegistryApplyResult reduction = registry.Apply(new OrderEvent
        {
            BrokerTransactionId = "transaction-reduce",
            BrokerTradeId = "trade-1",
            BrokerOrderId = "close-order-1",
            Instrument = Phase3TestData.Instrument,
            Type = OrderEventType.Filled,
            PositionEffect = OrderPositionEffect.Reduce,
            Timestamp = approved.Candidate.DecisionTime.AddMinutes(1),
            FillPrice = 1.1050m,
            FillQuantity = 5_000m,
            RealizedProfitLoss = 25m
        });

        Assert.Multiple(() =>
        {
            Assert.That(reduction.Applied, Is.True);
            Assert.That(registry.Snapshot.Positions, Has.Count.EqualTo(1));
            Assert.That(registry.Snapshot.Positions.Single().Quantity, Is.EqualTo(15_000m));
            Assert.That(registry.Snapshot.Orders, Has.Count.EqualTo(1));
            Assert.That(registry.Snapshot.Orders.Single().FilledQuantity, Is.EqualTo(20_000m));
        });
    }

    [Test]
    public void ReconciliationWithOwnedOpenTrade_RepairsMissedFillOrderState()
    {
        var registry = new LiveOrderPositionRegistry();
        PortfolioApprovedDecision approved = Phase3TestData.ApprovedDecision();
        string clientOrderId = LiveExecutionGateway.CreateClientOrderId(approved);
        registry.RegisterApproved(approved, clientOrderId, approved.Candidate.DecisionTime);
        registry.ApplySubmission(clientOrderId, new OrderSubmission
        {
            ClientOrderId = clientOrderId,
            BrokerOrderId = "broker-order-reconcile",
            Status = SubmissionStatus.Accepted,
            Certainty = ExecutionCertainty.Accepted
        }, approved.Candidate.DecisionTime);

        registry.ApplyReconciliation([], [new BrokerPosition
        {
            PositionId = "trade-reconcile",
            ClientTradeId = clientOrderId,
            Instrument = Phase3TestData.Instrument,
            Side = OrderSide.Buy,
            Quantity = 15_000m,
            InitialQuantity = 20_000m,
            AveragePrice = 1.1001m,
            ProtectiveStopPrice = 1.0950m,
            ProtectiveStopOrderId = "stop-reconcile",
            OpenedAt = approved.Candidate.DecisionTime.AddSeconds(1)
        }], approved.Candidate.DecisionTime.AddMinutes(1));

        LiveOrderRecord order = registry.Snapshot.Orders.Single();
        Assert.Multiple(() =>
        {
            Assert.That(order.State, Is.EqualTo(LiveOrderState.Filled));
            Assert.That(order.BrokerTradeId, Is.EqualTo("trade-reconcile"));
            Assert.That(order.FilledQuantity, Is.EqualTo(20_000m));
            Assert.That(order.ProtectiveStopOrderId, Is.EqualTo("stop-reconcile"));
            Assert.That(registry.Snapshot.Positions.Single().Quantity, Is.EqualTo(15_000m));
        });
    }

}
