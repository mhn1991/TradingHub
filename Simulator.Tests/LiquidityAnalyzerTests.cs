using Brokers.Models;
using ChartAnnotator.Liquidity;
using ChartAnnotator.Models;

namespace Simulator.Tests;

[TestFixture]
public sealed class LiquidityAnalyzerTests
{
    private static readonly InstrumentKey Instrument = new("EURUSD");
    private static readonly BarInterval Interval = BarInterval.Minutes(15);
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-01-05T00:00:00Z");

    [Test]
    public void EqualHighs_AppearOnlyAfterSecondSourceSwingIsConfirmed()
    {
        Candle[] candles =
        [
            C(0, 99m, 99.5m, 98.5m, 99m),
            C(1, 99m, 99.6m, 98.6m, 99m),
            C(2, 99m, 99.7m, 98.7m, 99m)
        ];
        SwingPoint[] swings =
        [
            Swing(0, 100.00m, SwingType.High, confirmedOn: 1),
            Swing(1, 100.04m, SwingType.High, confirmedOn: 2)
        ];
        var analyzer = new LiquidityAnalyzer(Profile());
        var history = new List<Candle>();

        LiquidityAnalysisSnapshot first = Update(analyzer, candles[0], history, swings, 0);
        LiquidityAnalysisSnapshot second = Update(analyzer, candles[1], history, swings, 1);
        LiquidityAnalysisSnapshot third = Update(analyzer, candles[2], history, swings, 2);

        Assert.Multiple(() =>
        {
            Assert.That(first.ActivePools.Any(pool => pool.Type == LiquidityPoolType.EqualHighs), Is.False);
            Assert.That(second.ActivePools.Any(pool => pool.Type == LiquidityPoolType.EqualHighs), Is.False);
            LiquidityPool pool = third.ActivePools.Single(item => item.Type == LiquidityPoolType.EqualHighs);
            Assert.That(pool.AvailableAt, Is.EqualTo(candles[2].CloseTime));
            Assert.That(pool.SourcePivotTimes, Is.EqualTo(swings.Select(item => item.PivotTime)));
            Assert.That(pool.Side, Is.EqualTo(LiquiditySide.BuySide));
        });
    }

    [Test]
    public void PenetrationClosingBack_IsSweep_NotAcceptedBreak()
    {
        var analyzer = new LiquidityAnalyzer(RoundNumberProfile());
        var history = new List<Candle>();
        Update(analyzer, C(0, 99m, 99.2m, 98.8m, 99m), history, [], 0);

        LiquidityAnalysisSnapshot result = Update(
            analyzer,
            C(1, 99m, 100.40m, 98.9m, 99.85m),
            history,
            [],
            1);

        LiquiditySweepEvent sweep = result.RecentSweeps.Single();
        Assert.Multiple(() =>
        {
            Assert.That(result.RecentEvents.Any(item => item.PoolId == sweep.PoolId &&
                item.EventType == LiquidityEventType.Sweep), Is.True);
            Assert.That(result.RecentEvents.Any(item => item.PoolId == sweep.PoolId &&
                item.EventType == LiquidityEventType.AcceptedBreak), Is.False);
            Assert.That(result.Pools.Single(item => item.PoolId == sweep.PoolId).State,
                Is.EqualTo(LiquidityPoolState.Swept));
            Assert.That(sweep.AvailableAt, Is.EqualTo(result.AvailableAt));
        });
    }

    [Test]
    public void CloseBeyondPool_IsAcceptedBreak_NotSweep()
    {
        var analyzer = new LiquidityAnalyzer(RoundNumberProfile());
        var history = new List<Candle>();
        Update(analyzer, C(0, 99m, 99.2m, 98.8m, 99m), history, [], 0);

        LiquidityAnalysisSnapshot result = Update(
            analyzer,
            C(1, 100.2m, 100.8m, 100.2m, 100.6m),
            history,
            [],
            1);

        LiquidityEvent accepted = result.RecentEvents.Single(item =>
            item.EventType == LiquidityEventType.AcceptedBreak);
        Assert.Multiple(() =>
        {
            Assert.That(result.RecentSweeps, Is.Empty);
            Assert.That(result.Pools.Single(item => item.PoolId == accepted.PoolId).State,
                Is.EqualTo(LiquidityPoolState.AcceptedBreak));
            Assert.That(accepted.AvailableAt, Is.EqualTo(result.AvailableAt));
        });
    }

    [Test]
    public void TouchAfterAcceptedBreak_EmitsRetestAndPoolStaysTrackable()
    {
        // Regression test for a real bug: LiquidityEventType.Retest was never emitted anywhere -
        // an accepted-break pool was marked terminal the instant it accepted, so no later touch
        // could ever be recorded against it. LiquidityBreakRetestPlaybook's Retest gate
        // (RequireRetestEvent, on by default) could therefore never pass.
        var analyzer = new LiquidityAnalyzer(RoundNumberProfile());
        var history = new List<Candle>();
        Update(analyzer, C(0, 99m, 99.2m, 98.8m, 99m), history, [], 0);
        LiquidityAnalysisSnapshot accepted = Update(
            analyzer, C(1, 100.2m, 100.8m, 100.2m, 100.6m), history, [], 1);
        LiquidityEvent acceptedEvent = accepted.RecentEvents.Single(item =>
            item.EventType == LiquidityEventType.AcceptedBreak);

        // Dips back into the pool's [99.90, 100.10] range without closing beyond its lower edge.
        LiquidityAnalysisSnapshot retested = Update(
            analyzer, C(2, 100.3m, 100.4m, 99.95m, 100.15m), history, [], 2);

        Assert.Multiple(() =>
        {
            Assert.That(retested.RecentEvents.Any(item =>
                item.PoolId == acceptedEvent.PoolId && item.EventType == LiquidityEventType.Retest), Is.True);
            Assert.That(retested.Pools.Single(item => item.PoolId == acceptedEvent.PoolId).State,
                Is.EqualTo(LiquidityPoolState.AcceptedBreak));
            Assert.That(retested.ActivePools.Any(item => item.PoolId == acceptedEvent.PoolId), Is.True);
        });
    }

    [Test]
    public void FullReversalAfterAcceptedBreak_FailsThePoolAsBroken()
    {
        var analyzer = new LiquidityAnalyzer(RoundNumberProfile());
        var history = new List<Candle>();
        Update(analyzer, C(0, 99m, 99.2m, 98.8m, 99m), history, [], 0);
        LiquidityAnalysisSnapshot accepted = Update(
            analyzer, C(1, 100.2m, 100.8m, 100.2m, 100.6m), history, [], 1);
        LiquidityEvent acceptedEvent = accepted.RecentEvents.Single(item =>
            item.EventType == LiquidityEventType.AcceptedBreak);

        // Closes back below the pool's lower edge (99.90) - a full reversal, not a retest.
        LiquidityAnalysisSnapshot failed = Update(
            analyzer, C(2, 100.2m, 100.2m, 99.5m, 99.7m), history, [], 2);

        Assert.Multiple(() =>
        {
            Assert.That(failed.RecentEvents.Any(item =>
                item.PoolId == acceptedEvent.PoolId && item.EventType == LiquidityEventType.Failure), Is.True);
            Assert.That(failed.Pools.Single(item => item.PoolId == acceptedEvent.PoolId).State,
                Is.EqualTo(LiquidityPoolState.Broken));
            Assert.That(failed.ActivePools.Any(item => item.PoolId == acceptedEvent.PoolId), Is.False);
        });
    }

    [Test]
    public void PreviousDayLevels_ArePublishedOnlyAfterReferenceDayEnds()
    {
        var analyzer = new LiquidityAnalyzer(Profile() with
        {
            EnablePreviousDayLevels = true
        });
        var history = new List<Candle>();
        LiquidityAnalysisSnapshot dayOne = Update(
            analyzer,
            TestCandles.Create(Instrument, Start, BarInterval.Hours(1), 99m, 101m, 98m, 100m),
            history,
            [],
            0);
        LiquidityAnalysisSnapshot dayTwo = Update(
            analyzer,
            TestCandles.Create(Instrument, Start.AddDays(1), BarInterval.Hours(1), 100m, 100.5m, 99m, 100m),
            history,
            [],
            1);

        Assert.Multiple(() =>
        {
            Assert.That(dayOne.Pools.Any(item => item.Type is LiquidityPoolType.PreviousDayHigh or
                LiquidityPoolType.PreviousDayLow), Is.False);
            LiquidityPool[] levels = dayTwo.Pools.Where(item => item.Type is
                LiquidityPoolType.PreviousDayHigh or LiquidityPoolType.PreviousDayLow).ToArray();
            Assert.That(levels, Has.Length.EqualTo(2));
            Assert.That(levels.All(item => item.ReferencePeriodEndedAt <= item.AvailableAt), Is.True);
            Assert.That(levels.All(item => item.AvailableAt == dayTwo.AvailableAt), Is.True);
        });
    }

    [Test]
    public void Replay_ProducesStablePoolAndEventIds()
    {
        static (Guid Pool, Guid Event) Run()
        {
            var analyzer = new LiquidityAnalyzer(RoundNumberProfile());
            var history = new List<Candle>();
            Update(analyzer, C(0, 99m, 99.2m, 98.8m, 99m), history, [], 0);
            LiquidityAnalysisSnapshot result = Update(
                analyzer, C(1, 99m, 100.4m, 98.9m, 99.85m), history, [], 1);
            LiquiditySweepEvent sweep = result.RecentSweeps.Single();
            return (sweep.PoolId, result.RecentEvents.Single(item =>
                item.PoolId == sweep.PoolId && item.EventType == LiquidityEventType.Sweep).EventId);
        }

        Assert.That(Run(), Is.EqualTo(Run()));
    }

    private static LiquidityCalculationProfile Profile() => new()
    {
        Enabled = true,
        MinimumSwingAgeBars = 100,
        MinimumRangeDurationBars = 100,
        EnablePreviousSessionLevels = false,
        EnablePreviousDayLevels = false,
        EnablePreviousWeekLevels = false
    };

    private static LiquidityCalculationProfile RoundNumberProfile() => Profile() with
    {
        EnableRoundNumberLevels = true,
        RoundNumberMajorStep = 10m,
        MaximumSweepPenetrationAtr = 1m,
        MinimumAcceptedBreakHoldBars = 1
    };

    private static LiquidityAnalysisSnapshot Update(
        LiquidityAnalyzer analyzer,
        Candle candle,
        List<Candle> history,
        IReadOnlyList<SwingPoint> swings,
        long sequence)
    {
        history.Add(candle);
        return analyzer.Update(
            candle,
            history,
            swings,
            1m,
            MarketStructureSnapshot.Empty,
            PriceActionSnapshot.Empty,
            sequence);
    }

    /// <summary>
    /// Regression guard for the terminal-pool trimming added to bound <c>_pools</c> to roughly
    /// MaximumActivePools + MaximumRetainedSweeps (previously every pool ever formed in a run was
    /// retained forever, making every candle's update scan/re-sort the whole history - O(total
    /// pools) per candle, roughly quadratic total cost over a long stream). Drifts price through
    /// hundreds of distinct round-number levels over 5,000 candles (far more than the default
    /// 256-entry retention window) to force many trims, then proves the append-only lifecycle
    /// guarantee still holds: once a pool ID is observed terminal (Swept/Consumed/AcceptedBreak/
    /// Expired/Merged), it never reappears as Active - which is exactly what a resurrection bug
    /// (a trimmed pool's deterministic ID being re-detected as brand new) would produce. Also
    /// confirms the analyzer's own bounded output actually stays bounded.
    /// </summary>
    [Test]
    public void Update_LongRun_NeverResurrectsATrimmedPoolAndKeepsOutputBounded()
    {
        var profile = RoundNumberProfile() with { RoundNumberMajorStep = 1m, RoundNumberMinorStep = null };
        var analyzer = new LiquidityAnalyzer(profile);
        var history = new List<Candle>();
        var terminalStates = new HashSet<LiquidityPoolState>
        {
            LiquidityPoolState.Swept, LiquidityPoolState.Consumed, LiquidityPoolState.AcceptedBreak,
            LiquidityPoolState.Broken, LiquidityPoolState.Expired, LiquidityPoolState.Merged
        };
        var everSeenTerminal = new HashSet<Guid>();
        LiquidityAnalysisSnapshot? last = null;

        for (int i = 0; i < 5_000; i++)
        {
            decimal price = 100m + i * 0.06m;
            Candle candle = C(i, price, price + 0.3m, price - 0.3m, price + 0.05m);
            last = Update(analyzer, candle, history, [], i);

            foreach (LiquidityPool pool in last.Pools)
            {
                if (terminalStates.Contains(pool.State))
                {
                    everSeenTerminal.Add(pool.PoolId);
                }
                else if (everSeenTerminal.Contains(pool.PoolId))
                {
                    Assert.Fail($"Pool {pool.PoolId} was observed terminal, then resurrected as {pool.State}.");
                }
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(last!.Pools.Count, Is.GreaterThan(profile.MaximumActivePools),
                "the drifting round-number stream should have formed more pools than fit in one active set");
            Assert.That(last.Pools.Count, Is.LessThanOrEqualTo(profile.MaximumActivePools + profile.MaximumRetainedSweeps));
            Assert.That(last.ActivePools.Count, Is.LessThanOrEqualTo(profile.MaximumActivePools));
        });
    }

    private static Candle C(int index, decimal open, decimal high, decimal low, decimal close) =>
        TestCandles.Create(Instrument, Start.AddMinutes(15 * index), Interval, open, high, low, close);

    private static SwingPoint Swing(
        int pivotIndex,
        decimal price,
        SwingType type,
        int confirmedOn) => new()
    {
        PivotTime = Start.AddMinutes(15 * pivotIndex),
        ConfirmedAt = Start.AddMinutes(15 * (confirmedOn + 1)),
        Price = price,
        Type = type,
        Strength = 3
    };
}
