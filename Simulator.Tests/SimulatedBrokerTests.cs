using Brokers.Models;
using Simulator.Broker;
using Simulator.Models;
using Simulator.Time;

namespace Simulator.Tests;

[TestFixture]
public sealed class SimulatedBrokerTests
{
    private static readonly InstrumentKey Instrument = new("FX:GBP/USD");
    private static readonly BarInterval Interval = BarInterval.Minutes(5);
    private static readonly DateTimeOffset Start =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [TestCase(OcoFillPolicy.StopLossFirst, 995)]
    [TestCase(OcoFillPolicy.TakeProfitFirst, 1005)]
    public async Task CandleTouchingBothOcoExits_UsesConfiguredPolicyWithoutCrashing(
        OcoFillPolicy policy,
        decimal expectedBalance)
    {
        var clock = new HistoricalSimulationClock();
        clock.AdvanceTo(Start);
        await using var broker = CreateBroker(clock, new SimulationOptions
        {
            StartingBalance = 1_000m,
            Leverage = 20m,
            CommissionRate = 0m,
            SpreadBasisPoints = 0m,
            SlippageBasisPoints = 0m,
            OcoFillPolicy = policy
        });

        await broker.Orders.PlaceOrderAsync(new PlaceOrderRequest
        {
            Instrument = Instrument,
            Side = OrderSide.Buy,
            Type = StandardOrderType.Market,
            Quantity = new OrderQuantity(1m, QuantityUnit.Units),
            StopLoss = new StopLossInstruction(95m),
            TakeProfit = new TakeProfitInstruction(105m),
            ClientOrderId = "oco-entry"
        });

        clock.AdvanceTo(Start.AddMinutes(5));
        await broker.Runtime.ProcessExecutionCandleAsync(
            Candle(Start, 100m, 101m, 99m, 100m));
        clock.AdvanceTo(Start.AddMinutes(10));
        await broker.Runtime.ProcessExecutionCandleAsync(
            Candle(Start.AddMinutes(5), 100m, 110m, 90m, 100m));

        AccountSnapshot account = (await broker.Accounts.GetAccountsAsync()).Single();
        IReadOnlyList<BrokerPosition> positions = await broker.Positions.GetOpenPositionsAsync();
        IReadOnlyList<BrokerOrder> orders = await broker.Orders.GetOpenOrdersAsync();
        Assert.Multiple(() =>
        {
            Assert.That(account.Balance, Is.EqualTo(expectedBalance));
            Assert.That(positions, Is.Empty);
            Assert.That(orders, Is.Empty);
        });
    }

    [Test]
    public async Task UnsupportedQuoteCurrencyQuantity_IsRejectedExplicitly()
    {
        var clock = new HistoricalSimulationClock();
        clock.AdvanceTo(Start);
        await using var broker = CreateBroker(clock);

        OrderSubmission result = await broker.Orders.PlaceOrderAsync(new PlaceOrderRequest
        {
            Instrument = Instrument,
            Side = OrderSide.Buy,
            Type = StandardOrderType.Market,
            Quantity = new OrderQuantity(100m, QuantityUnit.QuoteCurrency)
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(SubmissionStatus.Rejected));
            Assert.That(result.Certainty, Is.EqualTo(ExecutionCertainty.Rejected));
            Assert.That(result.RejectionReason, Does.Contain("conversion metadata"));
        });
    }

    [Test]
    public async Task ImmediateOrCancelOrder_ExpiresOnFirstMissedCandle()
    {
        var clock = new HistoricalSimulationClock();
        clock.AdvanceTo(Start);
        await using var broker = CreateBroker(clock);

        await broker.Orders.PlaceOrderAsync(new PlaceOrderRequest
        {
            Instrument = Instrument,
            Side = OrderSide.Buy,
            Type = StandardOrderType.Limit,
            Quantity = new OrderQuantity(1m, QuantityUnit.Units),
            LimitPrice = 90m,
            TimeInForce = StandardTimeInForce.ImmediateOrCancel
        });
        BrokerOrder openOrder = (await broker.Orders.GetOpenOrdersAsync()).Single();

        clock.AdvanceTo(Start.AddMinutes(5));
        await broker.Runtime.ProcessExecutionCandleAsync(
            Candle(Start, 100m, 101m, 99m, 100m));

        await using IAsyncEnumerator<OrderEvent> events = broker.Orders
            .StreamOrderEventsAsync()
            .GetAsyncEnumerator();
        Assert.That(await events.MoveNextAsync(), Is.True);
        Assert.That(await events.MoveNextAsync(), Is.True);
        IReadOnlyList<BrokerOrder> remainingOrders = await broker.Orders.GetOpenOrdersAsync();

        Assert.Multiple(() =>
        {
            Assert.That(openOrder.NormalizedStatus, Is.EqualTo(OrderStatus.Open));
            Assert.That(events.Current.Type, Is.EqualTo(OrderEventType.Expired));
            Assert.That(remainingOrders, Is.Empty);
        });
    }

    [Test]
    public async Task InsufficientMargin_RejectsFillWithoutOpeningPosition()
    {
        var clock = new HistoricalSimulationClock();
        clock.AdvanceTo(Start);
        await using var broker = CreateBroker(clock, new SimulationOptions
        {
            StartingBalance = 100m,
            Leverage = 1m,
            CommissionRate = 0m,
            SpreadBasisPoints = 0m,
            SlippageBasisPoints = 0m
        });

        await broker.Orders.PlaceOrderAsync(new PlaceOrderRequest
        {
            Instrument = Instrument,
            Side = OrderSide.Buy,
            Type = StandardOrderType.Market,
            Quantity = new OrderQuantity(2m, QuantityUnit.Units)
        });

        clock.AdvanceTo(Start.AddMinutes(5));
        await broker.Runtime.ProcessExecutionCandleAsync(
            Candle(Start, 100m, 100m, 100m, 100m));

        IReadOnlyList<BrokerPosition> positions = await broker.Positions.GetOpenPositionsAsync();
        IReadOnlyList<BrokerOrder> orders = await broker.Orders.GetOpenOrdersAsync();
        Assert.Multiple(() =>
        {
            Assert.That(positions, Is.Empty);
            Assert.That(orders, Is.Empty);
        });
    }

    [Test]
    public async Task MissingQuoteCurrencyConversion_IsRejectedExplicitly()
    {
        var clock = new HistoricalSimulationClock();
        clock.AdvanceTo(Start);
        await using var broker = CreateBroker(clock);

        OrderSubmission result = await broker.Orders.PlaceOrderAsync(new PlaceOrderRequest
        {
            Instrument = "FX:EUR/GBP",
            Side = OrderSide.Buy,
            Type = StandardOrderType.Market,
            Quantity = new OrderQuantity(1m, QuantityUnit.Units)
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(SubmissionStatus.Rejected));
            Assert.That(result.RejectionReason, Does.Contain("GBP-to-USD"));
        });
    }

    [Test]
    public async Task RealisedProfitLoss_UsesConfiguredQuoteCurrencyConversion()
    {
        InstrumentKey instrument = new("FX:EUR/GBP");
        var clock = new HistoricalSimulationClock();
        clock.AdvanceTo(Start);
        await using var broker = CreateBroker(clock, new SimulationOptions
        {
            StartingBalance = 1_000m,
            Leverage = 20m,
            CommissionRate = 0m,
            SpreadBasisPoints = 0m,
            SlippageBasisPoints = 0m,
            QuoteToBaseCurrencyRates = new Dictionary<string, decimal>
            {
                ["GBP"] = 1.25m
            }
        });

        await broker.Orders.PlaceOrderAsync(new PlaceOrderRequest
        {
            Instrument = instrument,
            Side = OrderSide.Buy,
            Type = StandardOrderType.Market,
            Quantity = new OrderQuantity(1m, QuantityUnit.Units)
        });
        clock.AdvanceTo(Start.AddMinutes(5));
        await broker.Runtime.ProcessExecutionCandleAsync(TestCandles.Create(
            instrument,
            Start,
            Interval,
            100m,
            100m,
            100m,
            100m));
        await broker.Orders.PlaceOrderAsync(new PlaceOrderRequest
        {
            Instrument = instrument,
            Side = OrderSide.Sell,
            Type = StandardOrderType.Market,
            Quantity = new OrderQuantity(1m, QuantityUnit.Units)
        });
        clock.AdvanceTo(Start.AddMinutes(10));
        await broker.Runtime.ProcessExecutionCandleAsync(TestCandles.Create(
            instrument,
            Start.AddMinutes(5),
            Interval,
            110m,
            110m,
            110m,
            110m));

        AccountSnapshot account = (await broker.Accounts.GetAccountsAsync()).Single();
        Assert.That(account.Balance, Is.EqualTo(1_012.5m));
    }

    [Test]
    public async Task BaseCurrencyMatchesAccountCurrency_DerivesRateFromInstrumentPriceInsteadOfRejecting()
    {
        // USD/JPY on a USD account: neither the direct quote-match (JPY != USD) nor an explicit
        // QuoteToBaseCurrencyRates entry applies, but the rate is exactly derivable from the
        // pair's own price (1 USD = 148.50 JPY => 1 JPY = 1/148.50 USD) - no configuration should
        // be required. Before this fix, every such pair (any USD/XXX pair with no explicit rate
        // configured, e.g. USD/JPY, USD/CHF, USD/CAD) silently rejected every order at this exact
        // point despite passing every upstream risk/sizing check.
        InstrumentKey instrument = new("FX:USD/JPY");
        var clock = new HistoricalSimulationClock();
        clock.AdvanceTo(Start);
        await using var broker = CreateBroker(clock, new SimulationOptions
        {
            StartingBalance = 1_000m,
            Leverage = 20m,
            CommissionRate = 0m,
            SpreadBasisPoints = 0m,
            SlippageBasisPoints = 0m
        });

        // A candle must reach the broker before the rate is derivable - matches how the real
        // engine always feeds the current candle to the broker before a strategy can act on it
        // (StrategySimulationSession.ProcessFrameAsync runs ProcessExecutionCandleAsync before
        // Pipeline.ProcessAsync for every frame).
        await broker.Runtime.ProcessExecutionCandleAsync(TestCandles.Create(
            instrument, Start, Interval, 148.50m, 148.50m, 148.50m, 148.50m));

        OrderSubmission entry = await broker.Orders.PlaceOrderAsync(new PlaceOrderRequest
        {
            Instrument = instrument,
            Side = OrderSide.Buy,
            Type = StandardOrderType.Market,
            Quantity = new OrderQuantity(1_000m, QuantityUnit.Units)
        });
        Assert.That(entry.Status, Is.EqualTo(SubmissionStatus.Accepted),
            "The order must be accepted once the instrument's own price makes the rate derivable.");

        clock.AdvanceTo(Start.AddMinutes(5));
        await broker.Runtime.ProcessExecutionCandleAsync(TestCandles.Create(
            instrument, Start.AddMinutes(5), Interval, 148.50m, 148.50m, 148.50m, 148.50m));

        await broker.Orders.PlaceOrderAsync(new PlaceOrderRequest
        {
            Instrument = instrument,
            Side = OrderSide.Sell,
            Type = StandardOrderType.Market,
            Quantity = new OrderQuantity(1_000m, QuantityUnit.Units)
        });

        clock.AdvanceTo(Start.AddMinutes(10));
        await broker.Runtime.ProcessExecutionCandleAsync(TestCandles.Create(
            instrument, Start.AddMinutes(10), Interval, 150.00m, 150.00m, 150.00m, 150.00m));

        // Quote-currency PnL = (150.00 - 148.50) * 1000 = 1500 JPY. Realised at the closing
        // candle's own price (the rate in effect at fill time): 1500 / 150.00 = 10 USD.
        AccountSnapshot account = (await broker.Accounts.GetAccountsAsync()).Single();
        IReadOnlyList<BrokerPosition> positions = await broker.Positions.GetOpenPositionsAsync();
        Assert.Multiple(() =>
        {
            Assert.That(account.Balance, Is.EqualTo(1_010m));
            Assert.That(positions, Is.Empty);
        });
    }

    private static SimulatedBrokerClient CreateBroker(
        HistoricalSimulationClock clock,
        SimulationOptions? options = null) => new(
            options ?? new SimulationOptions
            {
                CommissionRate = 0m,
                SpreadBasisPoints = 0m,
                SlippageBasisPoints = 0m
            },
            clock);

    private static Candle Candle(
        DateTimeOffset openTime,
        decimal open,
        decimal high,
        decimal low,
        decimal close) => TestCandles.Create(
            Instrument,
            openTime,
            Interval,
            open,
            high,
            low,
            close);
}
