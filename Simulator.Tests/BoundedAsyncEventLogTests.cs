using Simulator.Broker;

namespace Simulator.Tests;

[TestFixture]
public sealed class BoundedAsyncEventLogTests
{
    [Test]
    public void NonPositiveCapacity_IsRejected()
    {
        Assert.That(
            () => new BoundedAsyncEventLog<int>(0),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public async Task CompletedLog_ReplaysAllRetainedItemsInOrder()
    {
        var log = new BoundedAsyncEventLog<int>(3);
        log.Append(1);
        log.Append(2);
        log.Append(3);
        log.Complete();

        IReadOnlyList<int> result = await DrainAsync(log);

        Assert.That(result, Is.EqualTo(new[] { 1, 2, 3 }));
    }

    [Test]
    public async Task ConsumerStartingAfterOverflow_ReplaysRetainedWindow()
    {
        var log = new BoundedAsyncEventLog<int>(2);
        log.Append(1);
        log.Append(2);
        log.Append(3);
        log.Complete();

        IReadOnlyList<int> result = await DrainAsync(log);

        Assert.That(result, Is.EqualTo(new[] { 2, 3 }));
    }

    [Test]
    public async Task ExistingConsumerFallingBehind_ReceivesExplicitError()
    {
        var log = new BoundedAsyncEventLog<int>(2);
        await using IAsyncEnumerator<int> consumer = log.ReadAllAsync().GetAsyncEnumerator();
        Task<bool> pendingRead = consumer.MoveNextAsync().AsTask();
        log.Append(1);
        log.Append(2);
        log.Append(3);

        Assert.That(
            async () => await pendingRead,
            Throws.InvalidOperationException.With.Message.Contains("fell behind"));
    }

    [Test]
    public async Task MultipleConsumers_HaveIndependentReplayCursors()
    {
        var log = new BoundedAsyncEventLog<string>(4);
        log.Append("a");
        log.Append("b");
        log.Complete();

        Task<IReadOnlyList<string>> first = DrainAsync(log);
        Task<IReadOnlyList<string>> second = DrainAsync(log);
        await Task.WhenAll(first, second);
        IReadOnlyList<string> firstResult = await first;
        IReadOnlyList<string> secondResult = await second;

        Assert.Multiple(() =>
        {
            Assert.That(firstResult, Is.EqualTo(new[] { "a", "b" }));
            Assert.That(secondResult, Is.EqualTo(new[] { "a", "b" }));
        });
    }

    [Test]
    public async Task CompletionError_IsRaisedAfterRetainedItemsAreDrained()
    {
        var log = new BoundedAsyncEventLog<int>(2);
        var expected = new IOException("terminal failure");
        log.Append(42);
        log.Complete(expected);
        await using IAsyncEnumerator<int> consumer = log.ReadAllAsync().GetAsyncEnumerator();

        Assert.That(await consumer.MoveNextAsync(), Is.True);
        Assert.That(consumer.Current, Is.EqualTo(42));
        IOException? actual = Assert.ThrowsAsync<IOException>(
            async () => await consumer.MoveNextAsync());
        Assert.That(actual, Is.SameAs(expected));
    }

    [Test]
    public void AppendAfterCompletion_IsRejectedAndCompletionIsIdempotent()
    {
        var log = new BoundedAsyncEventLog<int>(2);
        log.Complete();
        log.Complete(new IOException("ignored"));

        Assert.That(() => log.Append(1), Throws.InvalidOperationException);
    }

    [Test]
    public async Task WaitingConsumer_IsReleasedByNormalCompletion()
    {
        var log = new BoundedAsyncEventLog<int>(2);
        await using IAsyncEnumerator<int> consumer = log.ReadAllAsync().GetAsyncEnumerator();
        Task<bool> pendingRead = consumer.MoveNextAsync().AsTask();

        log.Complete();

        Assert.That(await pendingRead, Is.False);
    }

    [Test]
    public async Task WaitingConsumer_ObservesCancellation()
    {
        var log = new BoundedAsyncEventLog<int>(2);
        using var cancellation = new CancellationTokenSource();
        await using IAsyncEnumerator<int> consumer = log
            .ReadAllAsync(cancellation.Token)
            .GetAsyncEnumerator();
        Task<bool> pendingRead = consumer.MoveNextAsync().AsTask();

        cancellation.Cancel();

        Assert.That(
            async () => await pendingRead,
            Throws.InstanceOf<OperationCanceledException>());
    }

    private static async Task<IReadOnlyList<T>> DrainAsync<T>(BoundedAsyncEventLog<T> log)
    {
        var result = new List<T>();
        await foreach (T item in log.ReadAllAsync())
        {
            result.Add(item);
        }

        return result;
    }
}
