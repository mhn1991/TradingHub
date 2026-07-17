using System.Runtime.CompilerServices;
using Agent.Abstractions;
using Agent.Models;
using Brokers.Models;
using ChartAnnotator.Engine;
using ChartAnnotator.MarketData;
using ChartAnnotator.Models;
using PortfolioManager.CrossMarket;
using PortfolioManager.Risk;
using Simulator.Abstractions;
using Simulator.Engine;
using Simulator.MarketData;
using Simulator.Models;

namespace Simulator.Tests;

/// <summary>
/// Proves the currency-strength side-channel basket fetch is wired into the real
/// production engine (§5/§26.2), not just the coordinator in isolation: basket
/// instrument candles the engine never trades still get fetched and reach
/// AgentMarketContext.CurrencyStrength on a live decision.
/// </summary>
[TestFixture]
public sealed class CurrencyStrengthEngineWiringTests
{
    private static readonly InstrumentKey GbpUsd = new("FX:GBP/USD");
    private static readonly InstrumentKey EurUsd = new("FX:EUR/USD");
    private static readonly BarInterval OneMinute = BarInterval.Minutes(1);
    private static readonly BarInterval Hourly = BarInterval.Hours(1);

    [Test]
    public async Task EngineRun_FetchesBasketCandlesAndAttachesSnapshotToAgentContext()
    {
        DateTimeOffset start = new(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);
        int hours = 10;

        // Primary traded instrument streams at 1-minute execution granularity; the
        // currency-strength basket is fetched independently at 1h, for instruments
        // (EUR/USD) this run never trades - proving the side-channel fetch is real.
        Candle[] gbpUsdExecutionCandles = BuildMinuteSeries(GbpUsd, start, hours * 60, 1.20m);
        Candle[] gbpUsdBasketCandles = BuildHourlySeries(GbpUsd, start, hours, 1.20m, 1.010m, 0.999m);
        Candle[] eurUsdBasketCandles = BuildHourlySeries(EurUsd, start, hours, 1.10m, 1.0006m, 0.9998m);

        var stream = new MultiSeriesCandleStream(new Dictionary<(InstrumentKey, BarInterval), Candle[]>
        {
            [(GbpUsd, OneMinute)] = gbpUsdExecutionCandles,
            [(GbpUsd, Hourly)] = gbpUsdBasketCandles,
            [(EurUsd, Hourly)] = eurUsdBasketCandles
        });

        var capturingAgent = new ContextCapturingAgent(OneMinute);
        var engine = new StreamingComparativeEngine(stream, [new StrategyFactoryEntry("capture", capturingAgent, GbpUsd)]);

        string output = Path.Combine(Path.GetTempPath(), "tradinghub-currency-strength", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        try
        {
            var runtime = new BacktestRuntimeOptions
            {
                BaseInterval = OneMinute,
                AnalysisBaseInterval = OneMinute,
                AnalysisIntervals = [OneMinute],
                WarmupDays = 0,
                ReplayChunkSize = 200,
                ProgressPublishIntervalMilliseconds = 50,
                CurrencyStrength = new CurrencyStrengthOptions
                {
                    Enabled = true,
                    Interval = Hourly,
                    ReturnLookbackBars = 3,
                    VolatilityLookbackBars = 6,
                    MinimumCurrencyCoveragePercent = 1m,
                    Baskets = new Dictionary<string, IReadOnlyList<InstrumentKey>>
                    {
                        ["Majors"] = [GbpUsd, EurUsd]
                    }
                }
            };

            await engine.RunAsync(
                new StreamingComparativeEngineOptions
                {
                    SimulationId = Guid.NewGuid(),
                    Instrument = GbpUsd,
                    EvaluationFrom = start,
                    EvaluationTo = start.AddHours(hours),
                    StreamFrom = start,
                    AnalysisIntervals = runtime.AnalysisIntervals,
                    Runtime = runtime,
                    SimulationOptions = new SimulationOptions
                    {
                        StartingBalance = 100_000m,
                        BaseCurrency = "USD",
                        Leverage = 20m,
                        CommissionRate = 0m,
                        SpreadBasisPoints = 0m,
                        SlippageBasisPoints = 0m,
                        CloseOpenPositionsAtEnd = true,
                        BaseCandleGapPolicy = BaseCandleGapPolicy.Throw
                    },
                    OutputDirectory = output,
                    InputStreamId = "currency-strength-test",
                    AnnotationOptions = new ChartAnnotationOptions
                    {
                        AtrPeriod = 3,
                        RsiPeriod = 3,
                        BollingerPeriod = 3,
                        HeavyAnalysisEveryCandles = 1
                    }
                },
                new HistoricalCandleRequest(GbpUsd, OneMinute, start, start.AddHours(hours)));

            Assert.That(capturingAgent.ObservedCurrencyStrength, Has.Some.Not.Null,
                "Expected at least one decision-time AgentMarketContext to carry a real CurrencyStrengthSnapshot.");
        }
        finally
        {
            if (Directory.Exists(output))
                Directory.Delete(output, recursive: true);
        }
    }

    private static Candle[] BuildMinuteSeries(InstrumentKey instrument, DateTimeOffset start, int bars, decimal price)
    {
        var candles = new Candle[bars];
        for (int i = 0; i < bars; i++)
        {
            decimal next = price * (i % 2 == 0 ? 1.0002m : 0.9999m);
            candles[i] = TestCandles.Create(instrument, start.AddMinutes(i), OneMinute, price, Math.Max(price, next), Math.Min(price, next), next);
            price = next;
        }
        return candles;
    }

    private static Candle[] BuildHourlySeries(
        InstrumentKey instrument,
        DateTimeOffset start,
        int bars,
        decimal startPrice,
        decimal up,
        decimal down)
    {
        var candles = new Candle[bars];
        decimal price = startPrice;
        for (int i = 0; i < bars; i++)
        {
            decimal next = price * (i % 2 == 0 ? up : down);
            candles[i] = TestCandles.Create(instrument, start.AddHours(i), Hourly, price, Math.Max(price, next), Math.Min(price, next), next);
            price = next;
        }
        return candles;
    }

    private sealed class MultiSeriesCandleStream(IReadOnlyDictionary<(InstrumentKey, BarInterval), Candle[]> series)
        : IHistoricalCandleStream
    {
        public async IAsyncEnumerable<MarketCandle> StreamAsync(
            HistoricalCandleRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (!series.TryGetValue((request.Instrument, request.BaseInterval), out Candle[]? candles))
                yield break;
            foreach (Candle candle in candles
                         .Where(item => item.OpenTime >= request.From && item.OpenTime < request.To)
                         .OrderBy(item => item.OpenTime))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return MarketCandle.FromMid(candle);
                await Task.Yield();
            }
        }
    }

    private sealed class ContextCapturingAgent(BarInterval interval) : ITradingAgent
    {
        private readonly List<CurrencyStrengthSnapshot?> _observed = [];

        public string Name => "Currency-strength context capture";
        public IReadOnlySet<BarInterval> RequiredIntervals { get; } = new HashSet<BarInterval> { interval };
        public BarInterval TriggerInterval => interval;
        public AgentExitManagementMode ExitManagementMode => AgentExitManagementMode.Bracket;
        public IReadOnlyList<CurrencyStrengthSnapshot?> ObservedCurrencyStrength => _observed;

        public Task<AgentDecision> EvaluateAsync(
            AgentMarketContext context,
            CancellationToken cancellationToken = default)
        {
            _observed.Add(context.CurrencyStrength);
            return Task.FromResult(new AgentDecision
            {
                Action = AgentAction.Observe,
                Instrument = context.Instrument,
                Confidence = 0m,
                CreatedAt = context.Timestamp,
                Reason = "Capture only"
            });
        }
    }
}
