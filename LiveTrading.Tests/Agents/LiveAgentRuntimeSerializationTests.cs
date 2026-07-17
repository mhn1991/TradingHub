using Agent.Models;
using Brokers.Models;
using ChartAnnotator.Engine;
using LiveTrading.Agents;
using LiveTrading.Shadow;
using LiveTrading.Tests.Fakes;
using NUnit.Framework;
using TradingCore.Pipeline;

namespace LiveTrading.Tests.Agents;

[TestFixture]
public sealed class LiveAgentRuntimeSerializationTests
{
    private static readonly InstrumentKey Instrument = AgentTestSupport.Instrument;
    private static readonly BarInterval M1 = AgentTestSupport.M1;
    private static readonly IReadOnlySet<BarInterval> Intervals = new HashSet<BarInterval> { M1 };

    private static AnalysisProfileKey Profile() =>
        AnalysisProfileKey.Create(new ChartAnnotationOptions(), Intervals, "schema-v1");

    // AgentTestSupport.Snapshot hardcodes Version = 1 - fine when every call in a test reuses an
    // identical snapshot (same AvailableAt/Version), but MarketDataQualityGate's version-collision
    // check rejects a later call whose AvailableAt differs while Version stayed at 1, so any test
    // that varies AvailableAt across calls on the *same* runtime must pass a matching version too.
    private static ChartAnnotator.Models.AnalysisSnapshot AnalysisSnapshot(DateTimeOffset availableAt, long version) => new()
    {
        Instrument = Instrument,
        Interval = M1,
        AvailableAt = availableAt,
        Version = version,
        LatestCandle = AgentTestSupport.Candle(availableAt.AddMinutes(-1), M1),
        Indicators = new ChartAnnotator.Models.IndicatorSnapshot(),
        Swings = [],
        PriceZones = [],
        Trendlines = [],
        Channels = [],
        Confidence = new ChartAnnotator.Models.ConfidenceScore { Total = 0.5m, Contributions = [] }
    };

    private static MarketAnalysisSnapshot Snapshot(DateTimeOffset availableAt, long version = 1) => new()
    {
        Instrument = Instrument,
        Profile = Profile(),
        SnapshotVersion = version,
        DecisionEpoch = version,
        AvailableAt = availableAt,
        Timeframes = new Dictionary<BarInterval, ChartAnnotator.Models.AnalysisSnapshot>
        {
            [M1] = AnalysisSnapshot(availableAt, version)
        },
        CrossMarket = null,
        DataQuality = TradingCore.MarketData.DataQualityResult.Valid
    };

    [Test]
    public async Task EvaluateAsync_ConcurrentCalls_NeverOverlapOnOneRuntime()
    {
        int concurrent = 0;
        int maxConcurrent = 0;
        var agent = new FakeTradingAgent
        {
            RequiredIntervals = Intervals,
            TriggerInterval = M1,
            Decide = context =>
            {
                int current = Interlocked.Increment(ref concurrent);
                int observedMax;
                do
                {
                    observedMax = maxConcurrent;
                } while (current > observedMax &&
                         Interlocked.CompareExchange(ref maxConcurrent, current, observedMax) != observedMax);

                Thread.Sleep(20);
                Interlocked.Decrement(ref concurrent);
                return new AgentDecision
                {
                    Action = AgentAction.Observe,
                    Instrument = context.Instrument,
                    Confidence = 0m,
                    CreatedAt = context.Timestamp,
                    Reason = "no setup"
                };
            }
        };

        using var runtime = new LiveAgentRuntime(
            new AgentInstanceKey(AgentInstanceKey.DefaultDeploymentId, Instrument, "strategy-1", Guid.Empty, 1),
            Profile(),
            AgentExecutionMode.Shadow,
            new SafeTradingPipeline(
                agent,
                new ShadowExecutionCoordinator(),
                dataQuality: new TradingCore.MarketData.MarketDataQualityGate(
                    new TradingCore.MarketData.MarketDataQualityOptions { RejectGaps = false, RejectFutureSnapshots = false })),
            new FakeBrokerClient());

        DateTimeOffset now = DateTimeOffset.UtcNow;
        const int concurrentCalls = 6;
        // Task.Run forces genuine concurrent dispatch onto the thread pool - EvaluateAsync's
        // synchronous prefix (up to its first real async yield) would otherwise run inline on
        // whichever thread calls it, so a plain LINQ Select over FakeBrokerClient's
        // already-completed Task.FromResult calls would just enumerate sequentially rather than
        // actually racing, defeating the point of this test.
        // Identical snapshot for every call - this test is about serialization, not analysis
        // progression, and an identical repeat (same Version/AvailableAt/OpenTime) is the one
        // shape MarketDataQualityGate's version-collision check always accepts regardless of
        // arrival order, which concurrent dispatch does not guarantee.
        MarketAnalysisSnapshot snapshot = Snapshot(now);
        Task<AgentRuntimeResult>[] tasks = Enumerable.Range(0, concurrentCalls)
            .Select(_ => Task.Run(() => runtime.EvaluateAsync(snapshot)))
            .ToArray();

        AgentRuntimeResult[] results = await Task.WhenAll(tasks);

        Assert.Multiple(() =>
        {
            Assert.That(maxConcurrent, Is.EqualTo(1), "Two evaluations overlapped on one runtime.");
            Assert.That(agent.EvaluationCount, Is.EqualTo(concurrentCalls));
            Assert.That(results, Has.All.Matches<AgentRuntimeResult>(r => r.Failure is null));
        });
    }

    [Test]
    public async Task EvaluateAsync_CancellingAQueuedCall_ReleasesTheGateForLaterCalls()
    {
        var release = new TaskCompletionSource();
        var agent = new FakeTradingAgent
        {
            RequiredIntervals = Intervals,
            TriggerInterval = M1,
            Decide = context =>
            {
                release.Task.Wait(TimeSpan.FromSeconds(5));
                return new AgentDecision
                {
                    Action = AgentAction.Observe,
                    Instrument = context.Instrument,
                    Confidence = 0m,
                    CreatedAt = context.Timestamp,
                    Reason = "no setup"
                };
            }
        };

        using var runtime = new LiveAgentRuntime(
            new AgentInstanceKey(AgentInstanceKey.DefaultDeploymentId, Instrument, "strategy-1", Guid.Empty, 1),
            Profile(),
            AgentExecutionMode.Shadow,
            new SafeTradingPipeline(
                agent,
                new ShadowExecutionCoordinator(),
                dataQuality: new TradingCore.MarketData.MarketDataQualityGate(
                    new TradingCore.MarketData.MarketDataQualityOptions { RejectGaps = false, RejectFutureSnapshots = false })),
            new FakeBrokerClient());

        DateTimeOffset now = DateTimeOffset.UtcNow;
        // Task.Run so the blocking Decide (release.Task.Wait) runs on a thread-pool thread
        // instead of synchronously on the test thread - EvaluateAsync's prefix up to its first
        // real async yield would otherwise run inline, blocking this method itself for up to 5s.
        Task<AgentRuntimeResult> holding = Task.Run(() => runtime.EvaluateAsync(Snapshot(now)));
        // Give the holding call time to acquire the gate before the queued call is issued.
        await Task.Delay(50);

        using var queuedCts = new CancellationTokenSource();
        Task queued = Task.Run(() => runtime.EvaluateAsync(Snapshot(now.AddMinutes(1), version: 2), queuedCts.Token));
        // Give the queued call time to actually reach (and block on) the gate before cancelling.
        await Task.Delay(50);
        queuedCts.Cancel();

        // SemaphoreSlim.WaitAsync(CancellationToken) throws OperationCanceledException directly,
        // not the TaskCanceledException subclass some other cancellation paths use.
        Assert.ThrowsAsync<OperationCanceledException>(async () => await queued);

        release.SetResult();
        AgentRuntimeResult holdingResult = await holding;
        Assert.That(holdingResult.Failure, Is.Null);

        // The gate must not be stuck - a fresh call after the cancelled one must still complete.
        // The cancelled call never reached MarketDataQualityGate.Evaluate, so its version (2) was
        // never committed - this call reuses it rather than skipping to 3, which would otherwise
        // look like a gap.
        using var freshCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        AgentRuntimeResult freshResult = await runtime.EvaluateAsync(Snapshot(now.AddMinutes(2), version: 2), freshCts.Token);
        Assert.That(freshResult.Failure, Is.Null);
    }
}
