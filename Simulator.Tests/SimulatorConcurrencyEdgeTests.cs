using Brokers.Models;

namespace Simulator.Tests;

[TestFixture]
public sealed class SimulatorConcurrencyEdgeTests
{
    [Test]
    public async Task ConcurrentOrders_ReceiveUniqueMonotonicBrokerIds()
    {
        await using var harness = new SimulationTestHarness();
        Task<OrderSubmission>[] submissions = Enumerable.Range(0, 100)
            .Select(index => Task.Run(
                () => harness.PlaceAsync(clientOrderId: $"concurrent-{index}")))
            .ToArray();

        OrderSubmission[] results = await Task.WhenAll(submissions);

        Assert.Multiple(() =>
        {
            Assert.That(results.All(result => result.Status == SubmissionStatus.Accepted), Is.True);
            Assert.That(
                results.Select(result => result.BrokerOrderId).Distinct().Count(),
                Is.EqualTo(100));
            Assert.That(harness.Broker.State.SubmittedOrders, Is.EqualTo(100));
        });
    }

    [Test]
    public async Task ConcurrentDuplicateClientIds_AllowExactlyOneOrder()
    {
        await using var harness = new SimulationTestHarness();
        Task<OrderSubmission>[] submissions = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => harness.PlaceAsync(clientOrderId: "same-id")))
            .ToArray();

        OrderSubmission[] results = await Task.WhenAll(submissions);

        Assert.Multiple(() =>
        {
            Assert.That(results.Count(result => result.Status == SubmissionStatus.Accepted), Is.EqualTo(1));
            Assert.That(results.Count(result => result.Status == SubmissionStatus.Rejected), Is.EqualTo(19));
            Assert.That(results.Where(result => result.Status == SubmissionStatus.Rejected)
                .All(result => result.RejectionReason!.Contains("already been used")), Is.True);
            Assert.That(harness.Broker.State.SubmittedOrders, Is.EqualTo(20));
            Assert.That(harness.Broker.State.RejectedOrders, Is.EqualTo(19));
        });
    }

    [Test]
    public async Task ConcurrentCancellation_OnlyOneCallerSucceeds()
    {
        await using var harness = new SimulationTestHarness();
        OrderSubmission submission = await harness.PlaceAsync(
            type: StandardOrderType.Limit,
            limitPrice: 90m);

        Task[] cancellations = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(
                () => harness.Broker.Orders.CancelOrderAsync(submission.BrokerOrderId!)))
            .ToArray();
        Exception? caught = null;
        try
        {
            await Task.WhenAll(cancellations);
        }
        catch (Exception exception)
        {
            caught = exception;
        }

        Assert.Multiple(() =>
        {
            Assert.That(cancellations.Count(task => task.Status == TaskStatus.RanToCompletion), Is.EqualTo(1));
            Assert.That(cancellations.Count(task => task.IsFaulted), Is.EqualTo(9));
            Assert.That(caught, Is.Not.Null);
        });
    }
}
