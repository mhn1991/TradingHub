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
