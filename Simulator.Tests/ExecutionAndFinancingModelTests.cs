using Brokers.Models;
using PortfolioManager.Risk;
using Simulator.Broker;
using Simulator.Execution;
using Simulator.Financing;
using Simulator.Models;
using Simulator.Time;

namespace Simulator.Tests;

[TestFixture]
public sealed class ExecutionAndFinancingModelTests
{
    [Test]
    public void GapThroughSellStop_FillsAtAdverseOpen_NotStop()
    {
        var model = new SimulationExecutionModel(new ExecutionModelOptions());
        SimulatedOrder order = StopOrder(OrderSide.Sell, 0.99m, 100m);
        Candle candle = CandleAt(open: 0.98m, high: 0.985m, low: 0.97m, close: 0.975m);

        FillEvaluation result = model.Evaluate(order, candle, new ExecutionModelContext
        {
            Sequence = 2,
            ConfiguredSpreadBasisPoints = 2m,
            ConfiguredSlippageBasisPoints = 1m,
            OrderQuantity = 100m,
            RemainingQuantity = 100m
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.ShouldFill, Is.True);
            Assert.That(result.GapThroughStop, Is.True);
            Assert.That(result.ExecutablePrice, Is.LessThan(0.98m));
            Assert.That(result.ExecutablePrice, Is.Not.EqualTo(0.99m));
        });
    }

    [Test]
    public void SyntheticCapacity_ProducesDeterministicPartialFill()
    {
        var model = new SimulationExecutionModel(new ExecutionModelOptions
        {
            FillCapacity = new FillCapacityModel { MaximumQuantityPerExecutionFrame = 25m }
        });
        FillEvaluation result = model.Evaluate(
            MarketOrder(OrderSide.Buy, 100m),
            CandleAt(1m, 1.01m, 0.99m, 1m),
            new ExecutionModelContext
            {
                Sequence = 2,
                ConfiguredSpreadBasisPoints = 0m,
                ConfiguredSlippageBasisPoints = 0m,
                OrderQuantity = 100m,
                RemainingQuantity = 100m
            });

        Assert.Multiple(() =>
        {
            Assert.That(result.FillQuantity, Is.EqualTo(25m));
            Assert.That(result.IsPartialFill, Is.True);
        });
    }

    [Test]
    public void WednesdayFinancing_UsesTripleDay()
    {
        var options = new FinancingOptions
        {
            Enabled = true,
            InstrumentRates = new Dictionary<string, FinancingRate>
            {
                ["FX:GBP/USD"] = new() { LongAnnualPercent = -3.65m, ShortAnnualPercent = 1m }
            }
        };
        var model = new ConfiguredFinancingModel(options);
        FinancingCharge result = model.Calculate(new PortfolioPositionLot
        {
            LotId = "lot",
            StrategyId = "strategy",
            DecisionId = "decision",
            Instrument = new InstrumentKey("FX:GBP/USD"),
            Side = OrderSide.Buy,
            Quantity = 10_000m,
            EntryPrice = 1m,
            CurrentPrice = 1m
        }, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, new FinancingContext
        {
            QuoteToAccountCurrencyRate = 1m,
            RolloverDate = new DateOnly(2026, 7, 15),
            RolloverDayCount = 3,
            AccountCurrency = "USD"
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.DayCount, Is.EqualTo(3));
            Assert.That(result.AmountAccountCurrency, Is.EqualTo(-3m));
            Assert.That(result.IsSynthetic, Is.True);
        });
    }

    [Test]
    public async Task PartialEntryAndProtectiveFills_KeepOneReduceOnlyOcoPairAtRemainingQuantity()
    {
        DateTimeOffset start = new(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);
        var clock = new HistoricalSimulationClock();
        clock.AdvanceTo(start);
        await using var broker = new SimulatedBrokerClient(new SimulationOptions
        {
            CommissionRate = 0m,
            SpreadBasisPoints = 0m,
            SlippageBasisPoints = 0m,
            ExecutionModel = new ExecutionModelOptions
            {
                FillCapacity = new FillCapacityModel
                {
                    MaximumQuantityPerExecutionFrame = 25m
                }
            }
        }, clock);
        const string reservationId = "res:strategy-a:decision-a";
        OrderSubmission entry = await broker.Orders.PlaceOrderAsync(new PlaceOrderRequest
        {
            Instrument = new InstrumentKey("FX:GBP/USD"),
            Side = OrderSide.Buy,
            Type = StandardOrderType.Market,
            Quantity = new OrderQuantity(100m, QuantityUnit.Units),
            StopLoss = new StopLossInstruction(0.90m),
            TakeProfit = new TakeProfitInstruction(1.10m),
            ClientOrderId = "entry-a",
            StrategyId = "strategy-a",
            DecisionId = "decision-a",
            SetupId = "setup-a",
            PortfolioReservationId = reservationId,
            RiskClusterId = "cluster:gbpusd"
        });

        for (int index = 1; index <= 4; index++)
        {
            DateTimeOffset close = start.AddMinutes(index);
            clock.AdvanceTo(close);
            await broker.Runtime.ProcessExecutionCandleAsync(CandleAt(
                1m, 1.01m, 0.99m, 1m, close.AddMinutes(-1), close));
        }

        IReadOnlyList<BrokerOrder> protectiveBefore = await broker.Orders
            .GetOpenOrdersAsync(new InstrumentKey("FX:GBP/USD"));
        Assert.Multiple(() =>
        {
            Assert.That(entry.Status, Is.EqualTo(SubmissionStatus.Accepted));
            Assert.That(protectiveBefore, Has.Count.EqualTo(2));
            Assert.That(protectiveBefore.All(order => order.Quantity - order.FilledQuantity == 100m), Is.True);
            Assert.That(protectiveBefore.All(order => order.PortfolioReservationId == reservationId), Is.True);
        });

        DateTimeOffset stopClose = start.AddMinutes(5);
        clock.AdvanceTo(stopClose);
        await broker.Runtime.ProcessExecutionCandleAsync(CandleAt(
            0.95m, 1.00m, 0.85m, 0.92m, stopClose.AddMinutes(-1), stopClose));

        BrokerPosition remainingPosition = (await broker.Positions.GetOpenPositionsAsync()).Single();
        IReadOnlyList<BrokerOrder> protectiveAfter = await broker.Orders
            .GetOpenOrdersAsync(new InstrumentKey("FX:GBP/USD"));
        Assert.Multiple(() =>
        {
            Assert.That(remainingPosition.Quantity, Is.EqualTo(75m));
            Assert.That(remainingPosition.StrategyId, Is.EqualTo("strategy-a"));
            Assert.That(remainingPosition.PortfolioReservationId, Is.EqualTo(reservationId));
            Assert.That(protectiveAfter, Has.Count.EqualTo(2), "A partial stop fill must not cancel its OCO target.");
            Assert.That(protectiveAfter.All(order => order.Quantity - order.FilledQuantity == remainingPosition.Quantity), Is.True);
            Assert.That(protectiveAfter.All(order => order.ReduceOnly), Is.True);
        });
    }

    [Test]
    public async Task MarketEntry_WhenExecutableFillCrossesAttachedTarget_IsRejectedWithoutOpeningPosition()
    {
        DateTimeOffset start = new(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);
        var clock = new HistoricalSimulationClock();
        clock.AdvanceTo(start);
        await using var broker = new SimulatedBrokerClient(new SimulationOptions
        {
            CommissionRate = 0m,
            SpreadBasisPoints = 10m,
            SlippageBasisPoints = 0m
        }, clock);

        OrderSubmission submission = await broker.Orders.PlaceOrderAsync(new PlaceOrderRequest
        {
            Instrument = new InstrumentKey("FX:GBP/USD"),
            Side = OrderSide.Buy,
            Type = StandardOrderType.Market,
            Quantity = new OrderQuantity(100m, QuantityUnit.Units),
            StopLoss = new StopLossInstruction(0.99m),
            // Valid relative to the 1.0 signal, but below the 1.0005 executable buy fill.
            TakeProfit = new TakeProfitInstruction(1.00025m),
            ClientOrderId = "crossed-target-entry"
        });

        DateTimeOffset close = start.AddMinutes(1);
        clock.AdvanceTo(close);
        await broker.Runtime.ProcessExecutionCandleAsync(CandleAt(
            1m, 1.01m, 0.99m, 1m, start, close));

        SimulationResult result = broker.State.BuildResult(start, close);
        Assert.Multiple(() =>
        {
            Assert.That(submission.Status, Is.EqualTo(SubmissionStatus.Accepted));
            Assert.That(result.OpenPositions, Is.Empty);
            Assert.That(result.FilledOrders, Is.Zero);
            Assert.That(result.RejectedOrders, Is.EqualTo(1));
        });
    }

    private static SimulatedOrder StopOrder(OrderSide side, decimal stop, decimal quantity) => new()
    {
        BrokerOrderId = "order",
        ClientOrderId = "client",
        Request = new PlaceOrderRequest
        {
            Instrument = new InstrumentKey("FX:GBP/USD"),
            Side = side,
            Type = StandardOrderType.Stop,
            StopPrice = stop,
            Quantity = new OrderQuantity(quantity, QuantityUnit.Units)
        },
        SubmittedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        SubmittedMarketSequence = 0,
        Status = "ACCEPTED"
    };

    private static SimulatedOrder MarketOrder(OrderSide side, decimal quantity) => new()
    {
        BrokerOrderId = "order",
        ClientOrderId = "client",
        Request = new PlaceOrderRequest
        {
            Instrument = new InstrumentKey("FX:GBP/USD"),
            Side = side,
            Type = StandardOrderType.Market,
            Quantity = new OrderQuantity(quantity, QuantityUnit.Units)
        },
        SubmittedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        SubmittedMarketSequence = 0,
        Status = "ACCEPTED"
    };

    private static Candle CandleAt(decimal open, decimal high, decimal low, decimal close) =>
        CandleAt(
            open,
            high,
            low,
            close,
            new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 15, 10, 1, 0, TimeSpan.Zero));

    private static Candle CandleAt(
        decimal open,
        decimal high,
        decimal low,
        decimal close,
        DateTimeOffset openTime,
        DateTimeOffset closeTime) => new()
    {
        Instrument = new InstrumentKey("FX:GBP/USD"),
        Interval = BarInterval.Minutes(1),
        OpenTime = openTime,
        CloseTime = closeTime,
        Prices = new Ohlc(open, high, low, close),
        IsComplete = true
    };
}
