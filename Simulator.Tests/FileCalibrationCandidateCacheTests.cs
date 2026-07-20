using Simulator.Experiments.IndicatorCalibration;
using Simulator.Experiments.IndicatorCalibration.Persistence;

namespace Simulator.Tests;

[TestFixture]
public sealed class FileCalibrationCandidateCacheTests
{
    private static string TempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "th-candidate-cache-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static BacktestEvaluationIdentity BuildIdentity(string suffix = "") => new()
    {
        StrategyId = "test-strategy" + suffix,
        StrategyImplementationHash = "1.0",
        EffectiveAgentDefinitionHash = "agent-hash",
        CompleteOptionsHash = "options-hash",
        FeatureSwitchHash = "feature-hash",
        Instrument = "FX:EUR/USD",
        CandleDataHash = "candle-hash",
        PriceComponent = "Mid",
        WindowStart = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        WindowEnd = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
        WarmupStart = new DateTimeOffset(2025, 12, 22, 0, 0, 0, TimeSpan.Zero),
        TimeframeTopologyHash = "topology-hash",
        ExecutionModelVersion = "v1",
        BrokerCostModelHash = "cost-hash",
        DataQualityPolicyVersion = "v1",
        RandomSeed = 0
    };

    private static BacktestEvaluationResult BuildResult() => new()
    {
        TradeCount = 10,
        MedianExpectancyR = 0.3m,
        MaximumDrawdownR = 1m,
        ProfitFactor = 2m,
        DataQualityValid = true
    };

    [Test]
    public void SetThenTryGet_RoundTrips()
    {
        var cache = new FileCalibrationCandidateCache(TempDirectory());
        BacktestEvaluationIdentity identity = BuildIdentity();
        BacktestEvaluationResult result = BuildResult();

        cache.Set(identity, result);
        bool hit = cache.TryGet(identity, out BacktestEvaluationResult? retrieved);

        Assert.That(hit, Is.True);
        Assert.That(retrieved, Is.EqualTo(result));
    }

    [Test]
    public void TryGet_UnknownIdentity_IsAMissAndIncrementsCounter()
    {
        var cache = new FileCalibrationCandidateCache(TempDirectory());
        bool hit = cache.TryGet(BuildIdentity(), out BacktestEvaluationResult? result);

        Assert.That(hit, Is.False);
        Assert.That(result, Is.Null);
        Assert.That(cache.Misses, Is.EqualTo(1));
        Assert.That(cache.Hits, Is.EqualTo(0));
    }

    [Test]
    public void SurvivesACacheInstanceRestart_PointedAtTheSameDirectory()
    {
        string directory = TempDirectory();
        BacktestEvaluationIdentity identity = BuildIdentity();
        BacktestEvaluationResult result = BuildResult();

        var firstInstance = new FileCalibrationCandidateCache(directory);
        firstInstance.Set(identity, result);

        // A brand-new instance (simulating a fresh process after a restart) pointed at the same
        // directory must still see the entry - this is the entire premise resume relies on.
        var secondInstance = new FileCalibrationCandidateCache(directory);
        bool hit = secondInstance.TryGet(identity, out BacktestEvaluationResult? retrieved);

        Assert.That(hit, Is.True);
        Assert.That(retrieved, Is.EqualTo(result));
    }

    [Test]
    public void DifferentIdentities_NeverCollide()
    {
        var cache = new FileCalibrationCandidateCache(TempDirectory());
        BacktestEvaluationIdentity first = BuildIdentity("-a");
        BacktestEvaluationIdentity second = BuildIdentity("-b");
        cache.Set(first, BuildResult() with { TradeCount = 1 });
        cache.Set(second, BuildResult() with { TradeCount = 2 });

        cache.TryGet(first, out BacktestEvaluationResult? firstResult);
        cache.TryGet(second, out BacktestEvaluationResult? secondResult);

        Assert.That(firstResult!.TradeCount, Is.EqualTo(1));
        Assert.That(secondResult!.TradeCount, Is.EqualTo(2));
    }
}
