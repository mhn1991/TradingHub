using Brokers.Models;
using Simulator.Broker;
using Simulator.Models;
using Simulator.Time;

namespace Simulator.Tests;

internal sealed class SimulationTestHarness : IAsyncDisposable
{
    public static readonly InstrumentKey DefaultInstrument = new("FX:GBP/USD");
    public static readonly BarInterval DefaultInterval = BarInterval.Minutes(5);
    public static readonly DateTimeOffset DefaultStart =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public SimulationTestHarness(SimulationOptions? options = null)
    {
        Options = options ?? ZeroCostOptions();
        Clock.AdvanceTo(DefaultStart);
        Broker = new SimulatedBrokerClient(Options, Clock);
    }

    public SimulationOptions Options { get; }
    public HistoricalSimulationClock Clock { get; } = new();
    public SimulatedBrokerClient Broker { get; }

    public Task<OrderSubmission> PlaceAsync(
        OrderSide side = OrderSide.Buy,
        StandardOrderType type = StandardOrderType.Market,
        decimal quantity = 1m,
        QuantityUnit quantityUnit = QuantityUnit.Units,
        InstrumentKey? instrument = null,
        decimal? limitPrice = null,
        decimal? stopPrice = null,
        StandardTimeInForce timeInForce = StandardTimeInForce.GoodTillCancelled,
        DateTimeOffset? expireAt = null,
        decimal? stopLoss = null,
        decimal? takeProfit = null,
        string? clientOrderId = null) => Broker.Orders.PlaceOrderAsync(new PlaceOrderRequest
        {
            Instrument = instrument ?? DefaultInstrument,
            Side = side,
            Type = type,
            Quantity = new OrderQuantity(quantity, quantityUnit),
            LimitPrice = limitPrice,
            StopPrice = stopPrice,
            TimeInForce = timeInForce,
            ExpireAt = expireAt,
            StopLoss = stopLoss is null ? null : new StopLossInstruction(stopLoss.Value),
            TakeProfit = takeProfit is null ? null : new TakeProfitInstruction(takeProfit.Value),
            ClientOrderId = clientOrderId
        });

    public async Task ProcessAsync(
        int candleIndex,
        decimal open,
        decimal high,
        decimal low,
        decimal close,
        InstrumentKey? instrument = null,
        BarInterval? interval = null)
    {
        BarInterval actualInterval = interval ?? DefaultInterval;
        DateTimeOffset openTime = DefaultStart.AddMinutes(candleIndex * 5);
        Candle candle = TestCandles.Create(
            instrument ?? DefaultInstrument,
            openTime,
            actualInterval,
            open,
            high,
            low,
            close);
        Clock.AdvanceTo(candle.CloseTime!.Value);
        await Broker.Runtime.ProcessExecutionCandleAsync(candle);
    }

    public async Task<IReadOnlyList<OrderEvent>> ReadEventsAsync(int count)
    {
        var result = new List<OrderEvent>(count);
        await using IAsyncEnumerator<OrderEvent> enumerator = Broker.Orders
            .StreamOrderEventsAsync()
            .GetAsyncEnumerator();
        while (result.Count < count && await enumerator.MoveNextAsync())
        {
            result.Add(enumerator.Current);
        }

        return result;
    }

    public ValueTask DisposeAsync() => Broker.DisposeAsync();

    public static SimulationOptions ZeroCostOptions(
        decimal startingBalance = 100_000m,
        decimal leverage = 20m) => new()
        {
            StartingBalance = startingBalance,
            Leverage = leverage,
            CommissionRate = 0m,
            SpreadBasisPoints = 0m,
            SlippageBasisPoints = 0m
        };
}
