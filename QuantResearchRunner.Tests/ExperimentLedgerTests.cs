using QuantResearchRunner.Experiments;

namespace QuantResearchRunner.Tests;

[TestFixture]
public sealed class ExperimentLedgerTests
{
    [Test]
    public async Task IsCompletedAsync_UnknownKey_ReturnsFalse()
    {
        var ledger = new ExperimentLedger(TempDirectory());

        bool completed = await ledger.IsCompletedAsync("fold-1");

        Assert.That(completed, Is.False);
    }

    [Test]
    public async Task MarkAsync_ThenIsCompletedAsync_RoundTrips()
    {
        var ledger = new ExperimentLedger(TempDirectory());

        await ledger.MarkAsync("fold-1", ExperimentUnitStatus.Completed);

        Assert.That(await ledger.IsCompletedAsync("fold-1"), Is.True);
    }

    [Test]
    public async Task MarkAsync_Failed_DoesNotCountAsCompleted()
    {
        var ledger = new ExperimentLedger(TempDirectory());

        await ledger.MarkAsync("fold-1", ExperimentUnitStatus.Failed, error: "boom");

        Assert.That(await ledger.IsCompletedAsync("fold-1"), Is.False);
    }

    [Test]
    public async Task Ledger_SurvivesReconstruction_ReadingPersistedState()
    {
        // Simulates resuming after a process restart: a fresh ExperimentLedger instance
        // pointed at the same directory must see units a prior instance already marked.
        string directory = TempDirectory();
        var first = new ExperimentLedger(directory);
        await first.MarkAsync("fold-1", ExperimentUnitStatus.Completed);
        await first.MarkAsync("fold-2", ExperimentUnitStatus.Running);

        var second = new ExperimentLedger(directory);

        Assert.Multiple(async () =>
        {
            Assert.That(await second.IsCompletedAsync("fold-1"), Is.True);
            Assert.That(await second.IsCompletedAsync("fold-2"), Is.False);
            Assert.That(await second.IsCompletedAsync("fold-3"), Is.False);
        });
    }

    [Test]
    public async Task MarkAsync_UpdatingExistingKey_OverwritesStatus()
    {
        var ledger = new ExperimentLedger(TempDirectory());
        await ledger.MarkAsync("fold-1", ExperimentUnitStatus.Running);
        Assert.That(await ledger.IsCompletedAsync("fold-1"), Is.False);

        await ledger.MarkAsync("fold-1", ExperimentUnitStatus.Completed);

        Assert.That(await ledger.IsCompletedAsync("fold-1"), Is.True);
    }

    [Test]
    public void ComputeKey_SameInputs_ProducesSameKey()
    {
        string a = ExperimentLedger.ComputeKey("fold", 1, "legacy", 40.0m);
        string b = ExperimentLedger.ComputeKey("fold", 1, "legacy", 40.0m);

        Assert.That(a, Is.EqualTo(b));
    }

    [Test]
    public void ComputeKey_DifferentInputs_ProducesDifferentKeys()
    {
        string a = ExperimentLedger.ComputeKey("fold", 1, "legacy");
        string b = ExperimentLedger.ComputeKey("fold", 1, "improved");

        Assert.That(a, Is.Not.EqualTo(b));
    }

    [Test]
    public async Task MarkCompletedAsync_ThenTryGetResultAsync_RoundTripsPayload()
    {
        var ledger = new ExperimentLedger(TempDirectory());
        var payload = new { Sharpe = 1.25m, TradeCount = 4 };

        await ledger.MarkCompletedAsync("fold-1", payload);

        var restored = await ledger.TryGetResultAsync<Dictionary<string, object>>("fold-1");
        Assert.That(await ledger.IsCompletedAsync("fold-1"), Is.True);
        Assert.That(restored, Is.Not.Null);
    }

    private static string TempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "qr-runner-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
