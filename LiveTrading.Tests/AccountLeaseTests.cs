using LiveTrading.AccountLease;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace LiveTrading.Tests;

[TestFixture]
public sealed class AccountLeaseTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "tradinghub-lease-tests", Guid.NewGuid().ToString("N"));
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private AccountLeaseOptions Options(bool force = false) => new()
    {
        Directory = _directory,
        RenewInterval = TimeSpan.FromSeconds(30),
        StaleThreshold = TimeSpan.FromSeconds(90),
        Force = force
    };

    [Test]
    public async Task TryAcquireAsync_FirstCaller_Acquires()
    {
        var clock = new FakeTimeProvider();
        await using var lease = new FileTradingAccountLease(Options(), clock);

        AccountLeaseResult result = await lease.TryAcquireAsync("OANDA", "acct-1", "instance-a", CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(AccountLeaseOutcome.Acquired));
    }

    [Test]
    public async Task TryAcquireAsync_SecondCaller_IsHeldByOther()
    {
        var clock = new FakeTimeProvider();
        await using var first = new FileTradingAccountLease(Options(), clock);
        await using var second = new FileTradingAccountLease(Options(), clock);

        await first.TryAcquireAsync("OANDA", "acct-1", "instance-a", CancellationToken.None);
        AccountLeaseResult result = await second.TryAcquireAsync("OANDA", "acct-1", "instance-b", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(AccountLeaseOutcome.HeldByOther));
            Assert.That(result.CurrentHolder?.InstanceId, Is.EqualTo("instance-a"));
        });
    }

    [Test]
    public async Task RenewAsync_UpdatesTheMetadataFileTimestamp()
    {
        var clock = new FakeTimeProvider();
        await using var lease = new FileTradingAccountLease(Options(), clock);
        await lease.TryAcquireAsync("OANDA", "acct-1", "instance-a", CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(45));
        await lease.RenewAsync(CancellationToken.None);

        // A second instance reads the metadata file (best-effort) to report staleness - renewal
        // having moved RenewedAt forward means the same elapsed time no longer reads as stale.
        await using var second = new FileTradingAccountLease(Options(), clock);
        AccountLeaseResult result = await second.TryAcquireAsync("OANDA", "acct-1", "instance-b", CancellationToken.None);
        Assert.That(result.Outcome, Is.EqualTo(AccountLeaseOutcome.HeldByOther));
    }

    [Test]
    public async Task TryAcquireAsync_StaleLeaseWithoutForce_IsReportedAsStaleButNotStolen()
    {
        var clock = new FakeTimeProvider();
        await using var first = new FileTradingAccountLease(Options(), clock);
        await first.TryAcquireAsync("OANDA", "acct-1", "instance-a", CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(200)); // past StaleThreshold, first never renewed

        await using var second = new FileTradingAccountLease(Options(force: false), clock);
        AccountLeaseResult result = await second.TryAcquireAsync("OANDA", "acct-1", "instance-b", CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(AccountLeaseOutcome.HeldByOtherStale));
    }

    [Test]
    public async Task TryAcquireAsync_StaleLeaseWithForce_IsRecoveredOnlyWhenActuallyStale()
    {
        var clock = new FakeTimeProvider();
        var firstOptions = Options();
        var first = new FileTradingAccountLease(firstOptions, clock);
        await first.TryAcquireAsync("OANDA", "acct-1", "instance-a", CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(200));

        // Force never displaces a live, healthy holder - simulate the crash by disposing (which
        // releases the OS lock) without ReleaseAsync (which would also delete the files); the
        // metadata file is left behind, stale, exactly like a real crash.
        await first.DisposeAsync();

        await using var second = new FileTradingAccountLease(Options(force: true), clock);
        AccountLeaseResult result = await second.TryAcquireAsync("OANDA", "acct-1", "instance-b", CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(AccountLeaseOutcome.Acquired));
    }

    [Test]
    public async Task TryAcquireAsync_AfterAnUngracefulCrash_ASecondInstanceReacquiresImmediately()
    {
        var clock = new FakeTimeProvider();
        var first = new FileTradingAccountLease(Options(), clock);
        await first.TryAcquireAsync("OANDA", "acct-1", "instance-a", CancellationToken.None);

        // Simulate a crash: the OS releases the exclusive file lock when the process dies, which
        // DisposeAsync (without ReleaseAsync) reproduces - files may be left behind, but a fresh
        // acquisition attempt succeeds immediately because the lock itself, not file presence, is
        // the source of truth.
        await first.DisposeAsync();

        await using var second = new FileTradingAccountLease(Options(), clock);
        AccountLeaseResult result = await second.TryAcquireAsync("OANDA", "acct-1", "instance-b", CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(AccountLeaseOutcome.Acquired));
    }

    [Test]
    public async Task ReleaseAsync_AllowsAnImmediateReacquisitionByAnotherInstance()
    {
        var clock = new FakeTimeProvider();
        await using var first = new FileTradingAccountLease(Options(), clock);
        await first.TryAcquireAsync("OANDA", "acct-1", "instance-a", CancellationToken.None);
        await first.ReleaseAsync(CancellationToken.None);

        await using var second = new FileTradingAccountLease(Options(), clock);
        AccountLeaseResult result = await second.TryAcquireAsync("OANDA", "acct-1", "instance-b", CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(AccountLeaseOutcome.Acquired));
    }
}
