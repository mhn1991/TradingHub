using Agent.Abstractions;
using Agent.Models;
using Brokers.Models;
using ChartAnnotator.Engine;
using Simulator.Engine;
using Simulator.Models;

namespace Simulator.AotSmoke;

internal static class Program
{
    public static async Task<int> Main()
    {
        try
        {
            InstrumentKey instrument = new("FX:GBP/USD");
            BarInterval interval = BarInterval.Minutes(5);
            DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            Candle[] candles =
            [
                CreateCandle(instrument, interval, start, 100m, 101m, 99m, 100m),
                CreateCandle(instrument, interval, start.AddMinutes(5), 110m, 112m, 109m, 111m),
                CreateCandle(instrument, interval, start.AddMinutes(10), 120m, 121m, 119m, 120m)
            ];

            await using SimulationSession session = SimulationFactory.CreateHistorical(
                instrument,
                candles,
                [interval],
                new BuyOnceAgent(interval),
                new SimulationOptions
                {
                    StartingBalance = 10_000m,
                    CommissionRate = 0m,
                    SpreadBasisPoints = 0m,
                    SlippageBasisPoints = 0m
                },
                new ChartAnnotationOptions
                {
                    AtrPeriod = 2,
                    RsiPeriod = 2,
                    BollingerPeriod = 2,
                    HeavyAnalysisEveryCandles = 1
                });

            SimulationResult result = await session.Runner.RunAsync().ConfigureAwait(false);
            BrokerPosition? position = result.OpenPositions.SingleOrDefault();
            if (result.SubmittedOrders != 1 ||
                result.FilledOrders != 1 ||
                result.RejectedOrders != 0 ||
                position is null ||
                position.Quantity != 1m ||
                position.AveragePrice != 110m)
            {
                Console.Error.WriteLine(
                    $"Native AOT smoke test failed: submitted={result.SubmittedOrders}, " +
                    $"filled={result.FilledOrders}, rejected={result.RejectedOrders}, " +
                    $"positions={result.OpenPositions.Count}.");
                return 1;
            }

            Console.WriteLine(
                $"Native AOT simulation passed: {result.FilledOrders} fill at " +
                $"{position.AveragePrice}, final equity {result.FinalEquity}.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Native AOT smoke test crashed: {exception}");
            return 2;
        }
    }

    private static Candle CreateCandle(
        InstrumentKey instrument,
        BarInterval interval,
        DateTimeOffset openTime,
        decimal open,
        decimal high,
        decimal low,
        decimal close) => new()
        {
            Instrument = instrument,
            Interval = interval,
            OpenTime = openTime,
            CloseTime = openTime.AddMinutes(interval.Value),
            Prices = new Ohlc(open, high, low, close),
            Volume = new MarketVolume(1m, VolumeKind.TickCount),
            IsComplete = true
        };

    private sealed class BuyOnceAgent(BarInterval interval) : ITradingAgent
    {
        private bool _submitted;

        public string Name => "Native AOT smoke agent";
        public IReadOnlySet<BarInterval> RequiredIntervals { get; } =
            new HashSet<BarInterval> { interval };
        public BarInterval TriggerInterval => interval;
        public AgentExitManagementMode ExitManagementMode =>
            AgentExitManagementMode.ProtectiveStopAndStrategyExit;

        public Task<AgentDecision> EvaluateAsync(
            AgentMarketContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_submitted)
            {
                return Task.FromResult(new AgentDecision
                {
                    Action = AgentAction.Observe,
                    Instrument = context.Instrument,
                    Confidence = 0m,
                    CreatedAt = context.Timestamp,
                    Reason = "The smoke order was already submitted."
                });
            }

            _submitted = true;
            return Task.FromResult(new AgentDecision
            {
                Action = AgentAction.Buy,
                Instrument = context.Instrument,
                SuggestedQuantity = 1m,
                QuantityUnit = QuantityUnit.Units,
                OrderType = StandardOrderType.Market,
                Confidence = 100m,
                CreatedAt = context.Timestamp,
                Reason = "Exercise the Native AOT simulation path."
            });
        }
    }
}
