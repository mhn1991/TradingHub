using System.IO.Compression;
using System.Text.Json;
using Brokers.Models;
using ChartAnnotator.Indicators;
using ChartAnnotator.Liquidity;
using ChartAnnotator.Models;
using ChartAnnotator.PriceAction;
using ChartAnnotator.Structure;
using NUnit.Framework;

namespace Simulator.Tests;

/// <summary>
/// Scratch diagnostic: replay real cached EUR/USD candles through the exact LiquidityAnalyzer
/// wiring the simulator uses, counting sweep events and tallying which sweep sub-condition kills
/// candidates. Investigating why structural-confluence never trades (sweeps never fire).
/// </summary>
[TestFixture]
[Explicit("diagnostic, not a correctness test")]
public sealed class ZZLiquiditySweepDiagnostic
{
    private sealed record RawCandle(
        DateTimeOffset OpenTime, DateTimeOffset CloseTime,
        decimal Open, decimal High, decimal Low, decimal Close, decimal Volume);

    private static List<Candle> LoadRealCandles(DateTimeOffset from, DateTimeOffset to)
    {
        const string path = "/home/mhn70/RiderProjects/TradingHub/.cache/historical/FX_EUR_USD_1m_20251218_20260701_20ec7255a5a18b1ba208738d.jsonl.gz";
        var instrument = new InstrumentKey("FX:EUR/USD");
        var interval = BarInterval.Minutes(1);
        var result = new List<Candle>();
        using var stream = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using var reader = new StreamReader(stream);
        string? line = reader.ReadLine(); // header
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        while ((line = reader.ReadLine()) is not null)
        {
            RawCandle? raw = JsonSerializer.Deserialize<RawCandle>(line, options);
            if (raw is null || raw.CloseTime <= from) continue;
            if (raw.OpenTime >= to) break;
            result.Add(new Candle
            {
                Instrument = instrument,
                Interval = interval,
                OpenTime = raw.OpenTime,
                CloseTime = raw.CloseTime,
                Prices = new Ohlc(raw.Open, raw.High, raw.Low, raw.Close),
                Volume = new MarketVolume(raw.Volume, VolumeKind.TickCount),
                IsComplete = true
            });
        }
        return result;
    }

    private static List<Candle> Aggregate(List<Candle> oneMinute, int minutes)
    {
        var interval = BarInterval.Minutes(minutes);
        var result = new List<Candle>();
        foreach (var group in oneMinute.GroupBy(c =>
            new DateTimeOffset(c.OpenTime.UtcDateTime.Date, TimeSpan.Zero)
                .AddMinutes(minutes * (long)(c.OpenTime.TimeOfDay.TotalMinutes / minutes))))
        {
            Candle[] members = group.OrderBy(c => c.OpenTime).ToArray();
            result.Add(new Candle
            {
                Instrument = members[0].Instrument,
                Interval = interval,
                OpenTime = group.Key,
                CloseTime = group.Key.AddMinutes(minutes),
                Prices = new Ohlc(
                    members[0].Prices.Open,
                    members.Max(c => c.Prices.High),
                    members.Min(c => c.Prices.Low),
                    members[^1].Prices.Close),
                Volume = new MarketVolume(members.Sum(c => c.Volume?.Value ?? 0m), VolumeKind.TickCount),
                IsComplete = true
            });
        }
        return result.OrderBy(c => c.OpenTime).ToList();
    }

    [TestCase(1)]
    [TestCase(30)]
    public void CountSweepsOnRealData(int intervalMinutes)
    {
        DateTimeOffset from = DateTimeOffset.Parse("2026-05-11T00:00:00Z");
        DateTimeOffset to = DateTimeOffset.Parse("2026-06-09T00:00:00Z");
        List<Candle> candles = LoadRealCandles(from, to);
        if (intervalMinutes > 1) candles = Aggregate(candles, intervalMinutes);
        TestContext.WriteLine($"interval={intervalMinutes}m candles={candles.Count}");

        var profile = new LiquidityCalculationProfile { Enabled = true };
        var analyzer = new LiquidityAnalyzer(profile);
        var detector = new SwingDetector(2, 2);
        var atrState = new AtrState(profile.AtrPeriod);
        var swings = new List<SwingPoint>();
        var history = new List<Candle>();

        int sweepEvents = 0, poolFormed = 0;
        long penetratedOnly = 0, closeBackFail = 0, penetrationTooSmall = 0,
             penetrationTooBig = 0, fullPass = 0, activePoolCandles = 0;
        var seenSweepIds = new HashSet<Guid>();
        var seenPoolIds = new HashSet<Guid>();
        IReadOnlyList<LiquidityPool> previousActive = [];
        decimal lastAtr = 0m;

        for (int i = 0; i < candles.Count; i++)
        {
            Candle candle = candles[i];

            // Replicate the sweep sub-condition math against the pools that were active
            // BEFORE this candle (the same set the analyzer's own detection loop sees).
            if (lastAtr > 0m)
            {
                foreach (LiquidityPool pool in previousActive)
                {
                    activePoolCandles++;
                    bool buySide = pool.Side == LiquiditySide.BuySide;
                    bool penetrated = buySide
                        ? candle.Prices.High > pool.UpperPrice
                        : candle.Prices.Low < pool.LowerPrice;
                    if (!penetrated) continue;
                    penetratedOnly++;
                    decimal penetration = buySide
                        ? candle.Prices.High - pool.UpperPrice
                        : pool.LowerPrice - candle.Prices.Low;
                    decimal penetrationAtr = penetration / lastAtr;
                    bool closeReturned = buySide
                        ? candle.Prices.Close <= pool.UpperPrice
                        : candle.Prices.Close >= pool.LowerPrice;
                    if (!closeReturned) { closeBackFail++; continue; }
                    if (penetrationAtr < profile.MinimumSweepPenetrationAtr) { penetrationTooSmall++; continue; }
                    if (penetrationAtr > profile.MaximumSweepPenetrationAtr) { penetrationTooBig++; continue; }
                    fullPass++;
                }
            }

            history.Add(candle);
            atrState.Update(candle);
            IReadOnlyList<SwingPoint> confirmed = detector.Update(candle);
            if (confirmed.Count > 0)
            {
                swings.AddRange(confirmed);
                if (swings.Count > 500) swings.RemoveRange(0, swings.Count - 500);
            }
            decimal? atr = atrState.IsReady ? atrState.Current : null;
            LiquidityAnalysisSnapshot snapshot = analyzer.Update(
                candle, history, swings, atr,
                MarketStructureSnapshot.Empty, PriceActionSnapshot.Empty, i);
            lastAtr = atr ?? 0m;
            previousActive = snapshot.ActivePools;

            foreach (LiquiditySweepEvent sweep in snapshot.RecentSweeps)
                if (seenSweepIds.Add(sweep.SweepId)) sweepEvents++;
            foreach (LiquidityPool pool in snapshot.Pools)
                if (seenPoolIds.Add(pool.PoolId)) poolFormed++;
        }

        TestContext.WriteLine($"pools formed (unique): {poolFormed}");
        TestContext.WriteLine($"sweep events (unique): {sweepEvents}");
        TestContext.WriteLine($"pool-candle checks: {activePoolCandles}");
        TestContext.WriteLine($"  penetrated:          {penetratedOnly}");
        TestContext.WriteLine($"  -> close-back fail:  {closeBackFail}");
        TestContext.WriteLine($"  -> pen < min (0.05): {penetrationTooSmall}");
        TestContext.WriteLine($"  -> pen > max (1.0):  {penetrationTooBig}");
        TestContext.WriteLine($"  -> full pass:        {fullPass}");
    }

    /// <summary>
    /// For every real sweep the 30m analyzer produces, compute what the v1 fixed-target geometry
    /// would compute: stop at sweep extreme +/- 0.2 ATR buffer, target at the NEAREST obstacle
    /// (setup swings + same-side pools) minus 0.1 ATR buffer, then R:R. Zones are omitted, which
    /// only removes obstacles - so real R:R can only be lower than measured here.
    /// </summary>
    [Test]
    public void MeasureSweepGeometryRewardRiskAt30m()
    {
        DateTimeOffset from = DateTimeOffset.Parse("2026-05-11T00:00:00Z");
        DateTimeOffset to = DateTimeOffset.Parse("2026-06-09T00:00:00Z");
        List<Candle> candles = Aggregate(LoadRealCandles(from, to), 30);
        TestContext.WriteLine($"30m candles: {candles.Count}");

        var profile = new LiquidityCalculationProfile { Enabled = true };
        var analyzer = new LiquidityAnalyzer(profile);
        var detector = new SwingDetector(2, 2);
        var atrState = new AtrState(profile.AtrPeriod);
        var swings = new List<SwingPoint>();
        var history = new List<Candle>();
        var seenSweeps = new HashSet<Guid>();
        var rewardRisks = new List<decimal>();
        int stopInvalid = 0, targetUnavailable = 0, sweeps = 0;
        const decimal stopBufferAtr = 0.2m, targetBufferAtr = 0.1m,
            minObstacleAtr = 0.15m, maxStopAtr = 4m;

        for (int i = 0; i < candles.Count; i++)
        {
            Candle candle = candles[i];
            history.Add(candle);
            if (history.Count > 500) history.RemoveRange(0, history.Count - 500);
            atrState.Update(candle);
            IReadOnlyList<SwingPoint> confirmed = detector.Update(candle);
            if (confirmed.Count > 0)
            {
                swings.AddRange(confirmed);
                if (swings.Count > 500) swings.RemoveRange(0, swings.Count - 500);
            }
            decimal? atrN = atrState.IsReady ? atrState.Current : null;
            LiquidityAnalysisSnapshot snapshot = analyzer.Update(
                candle, history, swings, atrN,
                MarketStructureSnapshot.Empty, PriceActionSnapshot.Empty, i);
            if (atrN is not > 0m) continue;
            decimal atr = atrN.Value;

            foreach (LiquiditySweepEvent sweep in snapshot.RecentSweeps)
            {
                if (!seenSweeps.Add(sweep.SweepId)) continue;
                sweeps++;
                LiquidityPool? pool = snapshot.Pools.FirstOrDefault(p => p.PoolId == sweep.PoolId);
                if (pool is null) continue;
                bool buy = pool.Side == LiquiditySide.SellSide; // sweep of sell-side pool -> bullish reversal
                decimal entry = candle.Prices.Close;
                decimal stop = sweep.ExtremePrice + (buy ? -1m : 1m) * stopBufferAtr * atr;
                decimal risk = buy ? entry - stop : stop - entry;
                if (risk <= 0m || risk > maxStopAtr * atr) { stopInvalid++; continue; }

                var obstaclePrices = new List<decimal>();
                foreach (SwingPoint swing in swings)
                {
                    if (buy && swing.Type == SwingType.High && swing.Price > entry) obstaclePrices.Add(swing.Price);
                    if (!buy && swing.Type == SwingType.Low && swing.Price < entry) obstaclePrices.Add(swing.Price);
                }
                LiquiditySide targetSide = buy ? LiquiditySide.BuySide : LiquiditySide.SellSide;
                foreach (LiquidityPool p in snapshot.Pools.Where(p =>
                    p.Side == targetSide && p.State is not (LiquidityPoolState.Consumed
                        or LiquidityPoolState.Broken or LiquidityPoolState.Expired or LiquidityPoolState.Merged)))
                {
                    decimal price = buy ? p.LowerPrice : p.UpperPrice;
                    if (buy && price > entry || !buy && price < entry) obstaclePrices.Add(price);
                }

                decimal[] usable = obstaclePrices
                    .Where(p => Math.Abs(p - entry) >= minObstacleAtr * atr)
                    .OrderBy(p => Math.Abs(p - entry)).ToArray();
                if (usable.Length == 0) { targetUnavailable++; continue; }
                decimal target = usable[0] + (buy ? -1m : 1m) * targetBufferAtr * atr;
                decimal reward = buy ? target - entry : entry - target;
                if (reward <= 0m) { targetUnavailable++; continue; }
                rewardRisks.Add(reward / risk);
            }
        }

        rewardRisks.Sort();
        TestContext.WriteLine($"sweeps: {sweeps} | stopInvalid: {stopInvalid} | targetUnavailable: {targetUnavailable}");
        TestContext.WriteLine($"geometry computed: {rewardRisks.Count}");
        if (rewardRisks.Count > 0)
        {
            decimal P(double q) => rewardRisks[(int)(q * (rewardRisks.Count - 1))];
            TestContext.WriteLine($"R:R  min {rewardRisks[0]:F2}  p25 {P(0.25):F2}  median {P(0.5):F2}  p75 {P(0.75):F2}  p90 {P(0.9):F2}  max {rewardRisks[^1]:F2}");
            int pass = rewardRisks.Count(r => r >= 1.5m);
            TestContext.WriteLine($"pass R:R >= 1.5: {pass}/{rewardRisks.Count} ({100.0 * pass / rewardRisks.Count:F1}%)");
        }
    }
}
