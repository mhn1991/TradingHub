
using Agent.Abstractions;
using Agent.Strategies;
using Brokers.Models;
using RiskManager;
using Simulator.Engine;
using Simulator.Models;

namespace Simulator.Tests;

[TestFixture]
public sealed class SimulatorNoTradeDiagnosticsTests
{
    [Test]
    public async Task FixedFractional_MissingFxConversion_FailsClosed()
    {
        InstrumentKey instrument = new("FX:GBP/JPY");
        BarInterval minute = BarInterval.Minutes(1);
        DateTimeOffset start = new(2025, 6, 2, 8, 0, 0, TimeSpan.Zero);
        Candle[] candles = BuildCandles(instrument, minute, start, 2500, 190m);

        var options = new ProgressiveStrategyOptions
        {
            TrendInterval = BarInterval.Minutes(15),
            ConfirmationInterval = BarInterval.Minutes(5),
            EntryInterval = minute,
            Quantity = 1_000m,
            MinimumTrendConfidence = 0m,
            MinimumConfirmationConfidence = 0m,
            MinimumEntryConfidence = 0m,
            MaximumEntryCandles = 12,
            MaximumTrendConfirmationBars = 3
        };

        var positionSizing = new PositionSizingOptions
        {
            Mode = PositionSizingMode.FixedFractionalRisk,
            RiskPercentOfEquity = 0.5m,
            MinimumQuantity = 1m,
            QuantityStep = 1m,
            MaximumAccountMarginUsagePercent = 30m,
            MaximumSinglePositionMarginPercent = 10m,
            Leverage = 20m
        };

        string output = Path.Combine(Path.GetTempPath(), "diag-ff-usd", Guid.NewGuid().ToString("N"));
        var runtime = new BacktestRuntimeOptions
        {
            ExecutionInterval = minute,
            AnalysisBaseInterval = minute,
            AnalysisIntervals = [minute, BarInterval.Minutes(5), BarInterval.Minutes(15)],
            StrategyTimeframes = new ProgressiveStrategyTimeframes
            {
                TrendInterval = BarInterval.Minutes(15),
                ConfirmationInterval = BarInterval.Minutes(5),
                EntryInterval = minute
            },
            WarmupDays = 0,
            PrefetchCapacity = 512,
            PrefetchLowWatermark = 64,
            SourcePageSize = 128,
            StrategyExecutionMode = StrategyExecutionMode.Sequential,
            ReplayChunkSize = 200,
            PositionSizing = positionSizing
        };

        var engine = new StreamingComparativeEngine(
            new Simulator.Abstractions.EnumerableMarketCandleStream(candles),
            [new Simulator.Engine.StrategyFactoryEntry("legacy", new LegacyProgressiveAgent(options), instrument)]);

        ComparativeSimulationResult result = await engine.RunAsync(
            new StreamingComparativeEngineOptions
            {
                SimulationId = Guid.NewGuid(),
                Instrument = instrument,
                EvaluationFrom = start,
                EvaluationTo = start.AddMinutes(candles.Length),
                StreamFrom = start,
                AnalysisIntervals = runtime.AnalysisIntervals,
                Runtime = runtime,
                SimulationOptions = new SimulationOptions
                {
                    BaseCurrency = "USD",
                    StartingBalance = 100_000m,
                    Leverage = 20m,
                    CommissionRate = 0m,
                    SpreadBasisPoints = 0m,
                    SlippageBasisPoints = 0m,
                    CloseOpenPositionsAtEnd = true,
                    BaseCandleGapPolicy = ChartAnnotator.MarketData.BaseCandleGapPolicy.ResetIncompleteBuckets,
                    QuoteToBaseCurrencyRates = new Dictionary<string, decimal>()
                },
                OutputDirectory = output,
                InputStreamId = "diag-ff-usd"
            },
            new HistoricalCandleRequest(instrument, minute, start, start.AddMinutes(candles.Length)));

        SimulationResult r = result.Strategies.Single().Result;
        TestContext.WriteLine($"[usd-account-jpy-pair] trades={r.Trades.Count} submitted={r.SubmittedOrders}");
        Assert.Multiple(() =>
        {
            Assert.That(r.SubmittedOrders, Is.Zero);
            Assert.That(r.Trades, Is.Empty,
                "Risk-based sizing must not guess an account-currency conversion or fall back to fixed quantity.");
        });
    }

    [Test]
    public async Task DefaultStack_ImprovedAndLegacy_OpenTrades()
    {
        InstrumentKey instrument = new("FX:EUR/USD");
        BarInterval minute = BarInterval.Minutes(1);
        DateTimeOffset start = new(2025, 6, 2, 8, 0, 0, TimeSpan.Zero);
        Candle[] candles = BuildCandles(instrument, minute, start, 2500, 1.10m);

        var options = new ProgressiveStrategyOptions
        {
            TrendInterval = BarInterval.Hours(1),
            ConfirmationInterval = BarInterval.Minutes(15),
            EntryInterval = BarInterval.Minutes(5),
            Quantity = 1_000m,
            MinimumRewardRisk = 1.5m,
            MinimumTrendConfidence = 55m,
            MinimumConfirmationConfidence = 55m,
            MinimumEntryConfidence = 58m,
            PriceActionConfirmation = PriceActionConfirmationMode.Soft
        };

        foreach ((string name, ITradingAgent agent) in new (string, ITradingAgent)[]
                 {
                     ("legacy", new LegacyProgressiveAgent(options)),
                     ("improved", new ImprovedProgressiveAgent(options))
                 })
        {
            await using SimulationSession session = SimulationFactory.CreateStrategyAwareHistorical(
                instrument,
                candles,
                [BarInterval.Minutes(1), BarInterval.Minutes(5), BarInterval.Minutes(15), BarInterval.Hours(1)],
                agent,
                new SimulationOptions
                {
                    BaseCurrency = "USD",
                    StartingBalance = 100_000m,
                    CloseOpenPositionsAtEnd = true,
                    BaseCandleGapPolicy = ChartAnnotator.MarketData.BaseCandleGapPolicy.ResetIncompleteBuckets,
                    CommissionRate = 0m,
                    SpreadBasisPoints = 0m,
                    SlippageBasisPoints = 0m
                },
                safetyOptions: new RiskManager.Safety.TradingSafetyOptions
                {
                    TripOnCriticalDataQualityIssue = false
                },
                dataQualityOptions: new TradingCore.MarketData.MarketDataQualityOptions
                {
                    RequireIndicatorsReady = true,
                    RejectGaps = false
                });

            SimulationResult result = await session.Runner.RunAsync();
            TestContext.WriteLine($"[{name}] trades={result.Trades.Count} submitted={result.SubmittedOrders}");
            Assert.That(result.Trades.Count + result.SubmittedOrders, Is.GreaterThan(0), name);
        }
    }

    private static Candle[] BuildCandles(
        InstrumentKey instrument,
        BarInterval interval,
        DateTimeOffset start,
        int count,
        decimal startPrice)
    {
        var candles = new Candle[count];
        decimal price = startPrice;
        decimal scale = startPrice > 10m ? 1m : 0.01m;
        for (int i = 0; i < count; i++)
        {
            decimal wave = (decimal)Math.Sin(i / 17.0) * 0.15m * scale;
            decimal drift = 0.002m * scale * (i % 200 < 120 ? 1 : -1);
            decimal open = price;
            decimal close = price + drift + wave * 0.1m;
            decimal high = Math.Max(open, close) + 0.03m * scale;
            decimal low = Math.Min(open, close) - 0.03m * scale;
            candles[i] = TestCandles.Create(instrument, start.AddMinutes(i), interval, open, high, low, close);
            price = close;
        }
        return candles;
    }
}
