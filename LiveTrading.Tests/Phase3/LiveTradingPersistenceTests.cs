using System.Text.Json;
using System.Text.Json.Nodes;
using LiveTrading.Persistence;
using LiveTrading.Registry;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using PortfolioManager.Risk;
using RiskManager.Safety;

namespace LiveTrading.Tests.Phase3;

[TestFixture]
public sealed class LiveTradingPersistenceTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp() =>
        _directory = Path.Combine(Path.GetTempPath(), "tradinghub-live-persistence-tests", Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Test]
    public async Task Checkpoint_RoundTripsTransactionCursorAndAppliedEventIds()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero));
        var persistence = new FileLiveTradingPersistence(new LiveTradingPersistenceOptions
        {
            RootDirectory = _directory,
            QueueCapacity = 32,
            FlushEachEvent = true
        }, clock);
        using var writerCts = new CancellationTokenSource();
        Task writer = persistence.RunAsync(writerCts.Token);
        var checkpoint = new LiveEngineCheckpoint
        {
            SavedAt = clock.GetUtcNow(),
            LastBrokerTransactionId = "6412",
            LastReconciliationId = "reconcile-1",
            CleanShutdown = false,
            Registry = new LiveRegistrySnapshot
            {
                Version = 4,
                Orders = [],
                Positions = [],
                AppliedEventIds = ["6410", "6412"]
            },
            Reservations = new PortfolioReservationSnapshot
            {
                Version = 0,
                Reservations = [],
                PendingRisk = 0m,
                ReservedMargin = 0m
            },
            ManualApprovals = [],
            Safety = new TradingSafetyController().Snapshot
        };

        await persistence.SaveCheckpointAsync(checkpoint, CancellationToken.None);
        LiveEngineCheckpoint? loaded = await persistence.LoadCheckpointAsync(CancellationToken.None);
        writerCts.Cancel();
        await writer;

        Assert.Multiple(() =>
        {
            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded!.LastBrokerTransactionId, Is.EqualTo("6412"));
            Assert.That(loaded.Registry.AppliedEventIds, Is.EqualTo(new[] { "6410", "6412" }).AsCollection);
            Assert.That(loaded.LastReconciliationId, Is.EqualTo("reconcile-1"));
        });
    }
    [Test]
    public async Task Journal_StoresPayloadSha256Envelope()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero));
        var persistence = new FileLiveTradingPersistence(new LiveTradingPersistenceOptions
        {
            RootDirectory = _directory,
            QueueCapacity = 32,
            FlushEachEvent = true
        }, clock);
        using var writerCts = new CancellationTokenSource();
        Task writer = persistence.RunAsync(writerCts.Token);

        await persistence.AppendAsync("orders", new { OrderId = "order-1", Quantity = 1_000m }, CancellationToken.None);
        writerCts.Cancel();
        await writer;

        string line = await File.ReadAllTextAsync(Path.Combine(_directory, "orders.ndjson"));
        using JsonDocument document = JsonDocument.Parse(line);
        string? hash = document.RootElement.GetProperty("payloadSha256").GetString();
        Assert.Multiple(() =>
        {
            Assert.That(hash, Has.Length.EqualTo(64));
            Assert.That(document.RootElement.GetProperty("payload").GetProperty("orderId").GetString(),
                Is.EqualTo("order-1"));
        });
    }

    [Test]
    public async Task Checkpoint_HashMismatchIsQuarantined()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero));
        var persistence = new FileLiveTradingPersistence(new LiveTradingPersistenceOptions
        {
            RootDirectory = _directory,
            QueueCapacity = 32,
            FlushEachEvent = true
        }, clock);
        using var writerCts = new CancellationTokenSource();
        Task writer = persistence.RunAsync(writerCts.Token);
        var checkpoint = new LiveEngineCheckpoint
        {
            SavedAt = clock.GetUtcNow(),
            Registry = new LiveRegistrySnapshot { Version = 0, Orders = [], Positions = [] },
            Reservations = new PortfolioReservationSnapshot
            {
                Version = 0,
                Reservations = [],
                PendingRisk = 0m,
                ReservedMargin = 0m
            },
            ManualApprovals = [],
            Safety = new TradingSafetyController().Snapshot,
            CleanShutdown = false
        };
        await persistence.SaveCheckpointAsync(checkpoint, CancellationToken.None);
        writerCts.Cancel();
        await writer;

        string path = Path.Combine(_directory, "engine-checkpoint.json");
        JsonNode root = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        root["payloadSha256"] = new string('0', 64);
        await File.WriteAllTextAsync(path, root.ToJsonString());

        Assert.That(
            async () => await persistence.LoadCheckpointAsync(CancellationToken.None),
            Throws.TypeOf<InvalidDataException>());
        Assert.That(Directory.GetFiles(_directory, "engine-checkpoint.corrupt-*.json"), Has.Length.EqualTo(1));
    }

}
