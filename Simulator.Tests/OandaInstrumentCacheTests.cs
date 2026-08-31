using Dashboard.Live;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Simulator.Tests;

/// <summary>
/// Covers the last-known-good instrument cache that keeps the OANDA workspace usable while the
/// broker's account API is degraded. Observed 2026-08-28: <c>/v3/accounts/{id}/instruments</c>
/// returned 503 while <c>/v3/instruments/{i}/candles</c> was healthy - market data was fine, but
/// the workspace reported itself unconfigured because discovery threw.
/// </summary>
[TestFixture]
public sealed class OandaInstrumentCacheTests
{
    private string _directory = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "oanda-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private OandaInstrumentCache Cache() => new(_directory, NullLogger.Instance);

    private static WorkspaceAsset[] Assets() =>
    [
        new("XAU_USD", "Gold", "METAL:XAU/USD", ["1m", "5m", "15m"]),
        new("EUR_USD", "Euro", "FX:EUR/USD", ["1m", "5m"])
    ];

    [Test]
    public void RoundTripsAListForTheSameAccount()
    {
        OandaInstrumentCache cache = Cache();
        cache.Save("acct-1", "Demo", Assets(), DateTimeOffset.UtcNow);

        IReadOnlyList<WorkspaceAsset>? loaded = cache.TryLoad("acct-1", "Demo");

        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.Select(a => a.Symbol), Is.EquivalentTo(new[] { "XAU_USD", "EUR_USD" }));
    }

    [Test]
    public void EnvironmentComparisonIsCaseInsensitive()
    {
        OandaInstrumentCache cache = Cache();
        cache.Save("acct-1", "Demo", Assets(), DateTimeOffset.UtcNow);

        Assert.That(cache.TryLoad("acct-1", "demo"), Is.Not.Null);
    }

    [Test]
    public void RefusesAListRecordedAgainstADifferentAccount()
    {
        // Serving one account's instruments to another would silently offer symbols that account
        // cannot trade - a wrong answer is worse than no answer here.
        OandaInstrumentCache cache = Cache();
        cache.Save("acct-1", "Demo", Assets(), DateTimeOffset.UtcNow);

        Assert.That(cache.TryLoad("acct-2", "Demo"), Is.Null);
    }

    [Test]
    public void RefusesAListRecordedAgainstADifferentEnvironment()
    {
        OandaInstrumentCache cache = Cache();
        cache.Save("acct-1", "Demo", Assets(), DateTimeOffset.UtcNow);

        Assert.That(cache.TryLoad("acct-1", "Live"), Is.Null);
    }

    [Test]
    public void NeverCachesAnEmptyList()
    {
        // Caching a miss would poison the fallback with "this account has no instruments".
        OandaInstrumentCache cache = Cache();
        cache.Save("acct-1", "Demo", [], DateTimeOffset.UtcNow);

        Assert.That(cache.TryLoad("acct-1", "Demo"), Is.Null);
    }

    [Test]
    public void ReturnsNullWhenNothingHasBeenCached()
    {
        Assert.That(Cache().TryLoad("acct-1", "Demo"), Is.Null);
    }

    [Test]
    public void SurvivesACorruptFileRatherThanThrowing()
    {
        // The cache is an optimisation; an unreadable one must not break startup.
        File.WriteAllText(Path.Combine(_directory, "oanda-instruments.json"), "{ not json");

        Assert.That(() => Cache().TryLoad("acct-1", "Demo"), Throws.Nothing);
        Assert.That(Cache().TryLoad("acct-1", "Demo"), Is.Null);
    }

    [Test]
    public void OverwritesAPreviousListOnRefresh()
    {
        OandaInstrumentCache cache = Cache();
        cache.Save("acct-1", "Demo", Assets(), DateTimeOffset.UtcNow);
        cache.Save("acct-1", "Demo", [new("GBP_USD", "Sterling", "FX:GBP/USD", ["5m"])], DateTimeOffset.UtcNow);

        IReadOnlyList<WorkspaceAsset>? loaded = cache.TryLoad("acct-1", "Demo");

        Assert.That(loaded!.Select(a => a.Symbol), Is.EquivalentTo(new[] { "GBP_USD" }));
    }

    [Test]
    public void PersistsAcrossInstances_SoARestartDuringAnOutageStillHasTheList()
    {
        // The case that actually matters: an in-memory cache is empty at exactly the moment a
        // restart lands in the middle of a broker outage.
        Cache().Save("acct-1", "Demo", Assets(), DateTimeOffset.UtcNow);

        Assert.That(Cache().TryLoad("acct-1", "Demo"), Is.Not.Null);
    }
}
