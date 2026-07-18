using TradingCore.Analytics;

namespace Simulator.Tests;

[TestFixture]
public sealed class StructuralAnalyticsPersistenceTests
{
    [Test]
    public async Task Writer_IsBoundedNonBlockingAndFlushesAcceptedRecords()
    {
        var store = new BlockingStore();
        var writer = new StructuralAnalyticsWriter(store, new StructuralAnalyticsWriterOptions
        {
            Capacity = 1,
            MaximumBatchSize = 1,
            MaximumWriteAttempts = 1
        });

        Assert.That(writer.TryEnqueue(Record(1)), Is.True);
        await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(writer.TryEnqueue(Record(2)), Is.True);
        Assert.That(writer.TryEnqueue(Record(3)), Is.False);
        store.Release.TrySetResult();
        await writer.DisposeAsync();

        Assert.Multiple(() =>
        {
            Assert.That(writer.AcceptedCount, Is.EqualTo(2));
            Assert.That(writer.DroppedCount, Is.EqualTo(1));
            Assert.That(writer.PersistedCount, Is.EqualTo(2));
            Assert.That(store.Records, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public async Task Writer_RetriesTransientFailureAndReportsPersistence()
    {
        var store = new RetryingStore();
        var writer = new StructuralAnalyticsWriter(store, new StructuralAnalyticsWriterOptions
        {
            Capacity = 4,
            MaximumBatchSize = 4,
            MaximumWriteAttempts = 2,
            RetryDelay = TimeSpan.Zero
        });

        Assert.That(writer.TryEnqueue(Record(1)), Is.True);
        await writer.DisposeAsync();

        Assert.Multiple(() =>
        {
            Assert.That(store.Attempts, Is.EqualTo(2));
            Assert.That(writer.PersistedCount, Is.EqualTo(1));
            Assert.That(writer.FailedCount, Is.Zero);
            Assert.That(writer.LastFailure, Is.Not.Null);
        });
    }

    [Test]
    public void OutcomeAttribution_IsAppendOnlyAndBounded()
    {
        var attributor = new StructuralOutcomeAttributor(2);
        attributor.Record(Outcome(1));
        attributor.Record(Outcome(2));
        attributor.Record(Outcome(3));

        Assert.That(attributor.Outcomes.Select(item => item.StableId),
            Is.EqualTo(new[] { Id(2), Id(3) }));
    }

    private static StructuralAnalyticsRecord Record(int id) => new(
        StructuralAnalyticsEntity.LiquidityPool,
        Id(id),
        "EURUSD",
        "M15",
        "profile",
        DateTimeOffset.Parse("2026-01-05T10:00:00Z"),
        DateTimeOffset.Parse("2026-01-05T10:15:00Z"),
        DateTimeOffset.Parse("2026-01-05T10:15:00Z"),
        id,
        "Active",
        "{}");

    private static StructuralOutcomeObservation Outcome(int id) => new(
        Id(id),
        Id(100 + id),
        null,
        null,
        DateTimeOffset.Parse("2026-01-05T10:00:00Z"),
        DateTimeOffset.Parse("2026-01-05T11:00:00Z"),
        1m,
        0.5m,
        TimeSpan.FromMinutes(10),
        null,
        true,
        id == 1,
        0,
        "EURUSD",
        "M15",
        "London",
        "Trending",
        "Demand",
        new Dictionary<string, decimal> { ["freshness"] = 1m });

    private static Guid Id(int value) =>
        Guid.Parse($"00000000-0000-0000-0000-{value:D12}");

    private sealed class BlockingStore : IStructuralAnalyticsStore
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<StructuralAnalyticsRecord> Records { get; } = [];

        public async ValueTask WriteBatchAsync(
            IReadOnlyList<StructuralAnalyticsRecord> records,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            Records.AddRange(records);
        }
    }

    private sealed class RetryingStore : IStructuralAnalyticsStore
    {
        public int Attempts { get; private set; }

        public ValueTask WriteBatchAsync(
            IReadOnlyList<StructuralAnalyticsRecord> records,
            CancellationToken cancellationToken)
        {
            Attempts++;
            if (Attempts == 1)
                throw new InvalidOperationException("transient");
            return ValueTask.CompletedTask;
        }
    }
}
