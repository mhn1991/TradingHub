using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Agent.Configuration;
using Agent.Models;
using Agent.Strategies;
using Agent.Strategies.StructuralConfluence;
using Brokers.Models;
using ChartAnnotator.Engine;
using ChartAnnotator.MarketData;
using ChartAnnotator.Models;
using ChartAnnotator.Regime;

namespace StructuralParameterSweep;

/// <summary>
/// Parallel parameter sweep for the structural-confluence agent's playbook/geometry options.
/// Reuses the exact production evidence pipeline (MultiTimeframeAggregator + ChartAnnotationEngine
/// + StructuralConfluenceAgent) but runs decision-only (no broker/fills/trade-management), so it
/// answers "how many trade candidates would this configuration produce, and why do the rest fail" -
/// not "what would the P&amp;L have been." Analysis (liquidity/supply-demand/price-action/indicators)
/// is computed exactly ONCE and shared across every variant, since only the agent's own options
/// differ between them; only the (cheap, pure) agent evaluation itself runs per variant, which is
/// what makes running many variants in parallel fast.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string candlePath = GetArg(args, "--candles") ??
            "/home/mhn70/RiderProjects/TradingHub/.cache/historical/FX_EUR_USD_1m_20251218_20260701_20ec7255a5a18b1ba208738d.jsonl.gz";
        var instrument = new InstrumentKey(GetArg(args, "--instrument") ?? "FX:EUR/USD");
        DateTimeOffset from = DateTimeOffset.Parse(GetArg(args, "--from") ?? "2026-05-11T00:00:00Z");
        DateTimeOffset to = DateTimeOffset.Parse(GetArg(args, "--to") ?? "2026-06-29T00:00:00Z");
        BarInterval context = ParseInterval(GetArg(args, "--context") ?? "2h");
        BarInterval[] additionalContext = (GetArg(args, "--additional-context") ?? "1h")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseInterval).ToArray();
        BarInterval setup = ParseInterval(GetArg(args, "--setup") ?? "30m");
        BarInterval trigger = ParseInterval(GetArg(args, "--trigger") ?? "5m");

        Console.WriteLine($"Instrument={instrument.Value}  window={from:yyyy-MM-dd}..{to:yyyy-MM-dd}");
        Console.WriteLine($"Timeframes: context={context} additionalContext=[{string.Join(',', additionalContext)}] setup={setup} trigger={trigger}");
        Console.WriteLine("Assumption: MarketRegime classification is enabled with default thresholds " +
            "(the profile that produced this run may have had it configured differently).");

        var sw = Stopwatch.StartNew();
        List<Candle> candles = LoadRealCandles(candlePath, instrument, from, to);
        Console.WriteLine($"Loaded {candles.Count:N0} base 1m candles in {sw.Elapsed.TotalSeconds:F1}s");

        BarInterval[] requiredIntervals = new[] { context, setup, trigger }
            .Concat(additionalContext).Distinct().ToArray();

        sw.Restart();
        List<EvalContext> contexts = BuildEvaluationContexts(
            candles, instrument, requiredIntervals, context, additionalContext, setup, trigger);
        Console.WriteLine($"Built {contexts.Count:N0} trigger-close evaluation contexts in {sw.Elapsed.TotalSeconds:F1}s " +
            "(analysis computed once, shared by every variant below)");

        // --- Investigating the reason-code collapse seen in the real backtest: matches the real
        // production profile exactly (adaptive=true, minRR=1.2). One variant, run with per-day
        // reason-code tracking so a decay/collapse pattern is directly visible if it reproduces
        // in this isolated harness too.
        var variant = new Variant(
            "adaptive=True  minRR=1.2 (repro)",
            Options => Options with
            {
                MinimumRewardRisk = 1.2m,
                AdaptiveTargetManagement = Options.AdaptiveTargetManagement with { Enabled = true },
                StrategyVersion = "structural-confluence-v2"
            });

        sw.Restart();
        VariantResult result = RunVariant(variant, contexts, context, additionalContext, setup, trigger);
        Console.WriteLine($"Ran variant in {sw.Elapsed.TotalSeconds:F1}s");

        PrintReport([result]);
        PrintDailyBreakdown(result);
        return 0;
    }

    private static void PrintDailyBreakdown(VariantResult result)
    {
        Console.WriteLine();
        Console.WriteLine("=== Reason-code variety per day (this isolated harness) ===");
        foreach ((string day, Dictionary<string, int> counts) in result.DailyReasonCodes.OrderBy(kv => kv.Key))
        {
            int variety = counts.Count;
            int total = counts.Values.Sum();
            string dominant = counts.OrderByDescending(kv => kv.Value).First().Key;
            string display = variety <= 2
                ? string.Join(", ", counts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}"))
                : $"{dominant} (+{variety - 1} others)";
            Console.WriteLine($"{day}  variety={variety,2}  total={total,4}  {display}");
        }
    }

    private static VariantResult RunVariant(
        Variant variant,
        List<EvalContext> contexts,
        BarInterval contextInterval,
        BarInterval[] additionalContext,
        BarInterval setupInterval,
        BarInterval triggerInterval)
    {
        string playbook = Environment.GetEnvironmentVariable("SWEEP_PLAYBOOK") ?? "sweep";
        StructuralConfluenceStrategyOptions baseOptions = new()
        {
            ContextInterval = contextInterval,
            AdditionalContextIntervals = additionalContext,
            SetupInterval = setupInterval,
            TriggerInterval = triggerInterval,
            // Only one playbook enabled at a time so the reason-code funnel isn't a blend of
            // multiple playbooks' gates. Select via SWEEP_PLAYBOOK=sweep|sd|breakretest.
            LiquiditySweepReversal = new() { Enabled = playbook == "sweep" },
            SupplyDemandPullback = new() { Enabled = playbook == "sd" },
            LiquidityBreakRetest = new() { Enabled = playbook == "breakretest" },
        };
        StructuralConfluenceStrategyOptions options = variant.Apply(baseOptions);
        var agent = new StructuralConfluenceAgent(options);
        var account = new AccountSnapshot { AccountId = "sweep", Balance = 100_000m, Currency = "USD" };
        var reasonCodes = new Dictionary<string, int>();
        var dailyReasonCodes = new Dictionary<string, Dictionary<string, int>>();
        int tradeSignals = 0;
        var rewardRisks = new List<decimal>();

        foreach (EvalContext evalContext in contexts)
        {
            var marketContext = new AgentMarketContext
            {
                Instrument = evalContext.Snapshots.Values.First().Instrument,
                Timestamp = evalContext.Timestamp,
                Analysis = new MultiTimeframeAnalysis(
                    evalContext.Snapshots.Values.First().Instrument, evalContext.Timestamp, evalContext.Snapshots),
                Account = account,
                Positions = [],
                OpenOrders = [],
                ExecutableSpread = 0.00006m, // ~0.6 pip, representative of a EUR/USD demo spread
                MarketDataAvailableAt = evalContext.Timestamp,
                StrategyId = "structural-confluence-sweep"
            };

            AgentDecision decision = agent.EvaluateAsync(marketContext).GetAwaiter().GetResult();
            string code = decision.ReasonCode ?? "NoReasonCode";
            reasonCodes[code] = reasonCodes.GetValueOrDefault(code) + 1;
            string day = evalContext.Timestamp.ToString("yyyy-MM-dd");
            if (!dailyReasonCodes.TryGetValue(day, out Dictionary<string, int>? dayCounts))
                dailyReasonCodes[day] = dayCounts = [];
            dayCounts[code] = dayCounts.GetValueOrDefault(code) + 1;
            if (decision.Action is AgentAction.Buy or AgentAction.Sell)
            {
                tradeSignals++;
                if (decision.ExpectedRewardRisk is decimal rr) rewardRisks.Add(rr);
            }
        }

        return new VariantResult(variant.Name, contexts.Count, tradeSignals, reasonCodes, rewardRisks, dailyReasonCodes);
    }

    private static List<EvalContext> BuildEvaluationContexts(
        List<Candle> candles,
        InstrumentKey instrument,
        BarInterval[] requiredIntervals,
        BarInterval contextInterval,
        BarInterval[] additionalContext,
        BarInterval setupInterval,
        BarInterval triggerInterval)
    {
        var defaults = new ChartAnnotationOptions();
        var options = defaults with
        {
            MarketRegime = new MarketRegimeOptions { Enabled = true },
            SupplyDemand = defaults.SupplyDemand with { Enabled = true },
            Liquidity = defaults.Liquidity with { Enabled = true }
        };
        var engine = new ChartAnnotationEngine(options);
        var aggregator = new MultiTimeframeAggregator(
            instrument, requiredIntervals, candleCapacity: 2_000,
            gapPolicy: BaseCandleGapPolicy.ResetIncompleteBuckets);

        var latest = new Dictionary<BarInterval, AnalysisSnapshot>();
        var results = new List<EvalContext>();
        long sequence = 0;

        foreach (Candle baseCandle in candles)
        {
            IReadOnlyList<CandleClosedEvent> closes = aggregator.Apply(baseCandle);
            foreach (CandleClosedEvent closeEvent in closes)
            {
                AnalysisSnapshot snapshot = engine.ProcessAsync(closeEvent, runtimeContext: null)
                    .AsTask().GetAwaiter().GetResult();
                latest[closeEvent.Interval] = snapshot;

                if (closeEvent.Interval == triggerInterval &&
                    requiredIntervals.All(latest.ContainsKey))
                {
                    results.Add(new EvalContext(
                        closeEvent.Candle.CloseTime ?? closeEvent.Candle.OpenTime,
                        new Dictionary<BarInterval, AnalysisSnapshot>(latest)));
                }
            }
            sequence++;
        }

        return results;
    }

    private static void PrintReport(VariantResult[] results)
    {
        Console.WriteLine();
        Console.WriteLine("=== Trade-candidate counts per variant ===");
        Console.WriteLine($"{"Variant",-28} {"Signals",8} {"of",4} {"MedianR:R",10}");
        foreach (VariantResult r in results.OrderByDescending(r => r.TradeSignals))
        {
            decimal median = Median(r.RewardRisks);
            Console.WriteLine($"{r.Name,-28} {r.TradeSignals,8} {r.Evaluations,4} {(r.RewardRisks.Count > 0 ? median.ToString("F2") : "-"),10}");
        }

        Console.WriteLine();
        Console.WriteLine("=== Reason-code funnel (top 6 per variant) ===");
        foreach (VariantResult r in results)
        {
            Console.WriteLine($"-- {r.Name} --");
            foreach ((string code, int count) in r.ReasonCodes.OrderByDescending(kv => kv.Value).Take(6))
                Console.WriteLine($"  {count,7}  {code}");
        }
    }

    private static decimal Median(List<decimal> values)
    {
        if (values.Count == 0) return 0m;
        List<decimal> sorted = [.. values];
        sorted.Sort();
        return sorted[sorted.Count / 2];
    }

    private static string? GetArg(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static BarInterval ParseInterval(string text)
    {
        System.Text.RegularExpressions.Match match =
            System.Text.RegularExpressions.Regex.Match(text.Trim(), @"^(\d+)\s*(mo|[smhdw])$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success)
            throw new ArgumentException($"Invalid interval '{text}'. Use forms like 30s, 15m, 2h, 1d.");
        int value = int.Parse(match.Groups[1].Value);
        return match.Groups[2].Value.ToLowerInvariant() switch
        {
            "s" => BarInterval.Seconds(value),
            "m" => BarInterval.Minutes(value),
            "h" => BarInterval.Hours(value),
            "d" => BarInterval.Days(value),
            "w" => BarInterval.Weeks(value),
            "mo" => BarInterval.Months(value),
            _ => throw new ArgumentException($"Unknown interval unit in '{text}'.")
        };
    }

    private sealed record RawCandle(
        DateTimeOffset OpenTime, DateTimeOffset CloseTime,
        decimal Open, decimal High, decimal Low, decimal Close, decimal Volume);

    private static List<Candle> LoadRealCandles(
        string path, InstrumentKey instrument, DateTimeOffset from, DateTimeOffset to)
    {
        var interval = BarInterval.Minutes(1);
        var result = new List<Candle>();
        using var stream = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using var reader = new StreamReader(stream);
        _ = reader.ReadLine(); // manifest header line
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            RawCandle? raw = JsonSerializer.Deserialize<RawCandle>(line, jsonOptions);
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

    private sealed record EvalContext(DateTimeOffset Timestamp, IReadOnlyDictionary<BarInterval, AnalysisSnapshot> Snapshots);

    private sealed record Variant(string Name, Func<StructuralConfluenceStrategyOptions, StructuralConfluenceStrategyOptions> Apply);

    private sealed record VariantResult(
        string Name, int Evaluations, int TradeSignals, Dictionary<string, int> ReasonCodes, List<decimal> RewardRisks,
        Dictionary<string, Dictionary<string, int>> DailyReasonCodes);
}
