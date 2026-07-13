using Brokers.Abstractions;
using Brokers.Models;
using Simulator.Broker;
using Simulator.Models;
using Simulator.Time;

namespace Simulator.Tests;

[TestFixture]
public sealed class SimulatorValidationTests
{
    private static readonly DateTimeOffset Start = SimulationTestHarness.DefaultStart;

    [TestCaseSource(nameof(InvalidOptions))]
    public void InvalidSimulationOptions_AreRejected(SimulationOptions options)
    {
        var clock = new HistoricalSimulationClock();

        Assert.That(
            () => new SimulatedBrokerClient(options, clock),
            Throws.InstanceOf<ArgumentException>());
    }

    [TestCaseSource(nameof(InvalidOrders))]
    public async Task InvalidOrder_IsRejectedWithoutEnteringOpenOrders(
        string caseName,
        PlaceOrderRequest request,
        string expectedMessage)
    {
        await using var harness = new SimulationTestHarness();

        OrderSubmission submission = await harness.Broker.Orders.PlaceOrderAsync(request);
        IReadOnlyList<BrokerOrder> openOrders = await harness.Broker.Orders.GetOpenOrdersAsync();
        IReadOnlyList<OrderEvent> events = await harness.ReadEventsAsync(1);

        Assert.Multiple(() =>
        {
            Assert.That(caseName, Is.Not.Empty);
            Assert.That(submission.Status, Is.EqualTo(SubmissionStatus.Rejected));
            Assert.That(submission.Certainty, Is.EqualTo(ExecutionCertainty.Rejected));
            Assert.That(submission.BrokerOrderId, Is.Null);
            Assert.That(submission.RejectionReason, Does.Contain(expectedMessage));
            Assert.That(openOrders, Is.Empty);
            Assert.That(events.Single().Type, Is.EqualTo(OrderEventType.Rejected));
        });
    }

    [TestCase(StandardTimeInForce.GoodTillCancelled)]
    [TestCase(StandardTimeInForce.ImmediateOrCancel)]
    [TestCase(StandardTimeInForce.FillOrKill)]
    [TestCase(StandardTimeInForce.Day)]
    [TestCase(StandardTimeInForce.GoodTillDate)]
    public async Task SupportedTimeInForce_IsAccepted(StandardTimeInForce timeInForce)
    {
        await using var harness = new SimulationTestHarness();

        OrderSubmission result = await harness.PlaceAsync(
            timeInForce: timeInForce,
            expireAt: timeInForce == StandardTimeInForce.GoodTillDate
                ? Start.AddHours(1)
                : null);

        Assert.That(result.Status, Is.EqualTo(SubmissionStatus.Accepted));
    }

    [TestCase(StandardOrderType.Market, null, null)]
    [TestCase(StandardOrderType.Limit, 100, null)]
    [TestCase(StandardOrderType.Stop, null, 100)]
    [TestCase(StandardOrderType.StopLimit, 100, 101)]
    public async Task ProperlyConfiguredOrderType_IsAccepted(
        StandardOrderType type,
        decimal? limit,
        decimal? stop)
    {
        await using var harness = new SimulationTestHarness();

        OrderSubmission result = await harness.PlaceAsync(
            type: type,
            limitPrice: limit,
            stopPrice: stop);

        Assert.That(result.Status, Is.EqualTo(SubmissionStatus.Accepted));
    }

    [TestCase(QuantityUnit.Units)]
    [TestCase(QuantityUnit.BaseAsset)]
    public async Task UnitBasedQuantities_AreAccepted(QuantityUnit unit)
    {
        await using var harness = new SimulationTestHarness();

        OrderSubmission result = await harness.PlaceAsync(quantityUnit: unit);

        Assert.That(result.Status, Is.EqualTo(SubmissionStatus.Accepted));
    }

    private static IEnumerable<TestCaseData> InvalidOptions()
    {
        yield return Option(new SimulationOptions { StartingBalance = 0m }, "zero balance");
        yield return Option(new SimulationOptions { Leverage = 0m }, "zero leverage");
        yield return Option(new SimulationOptions { CommissionRate = -0.01m }, "negative commission");
        yield return Option(new SimulationOptions { CommissionRate = 1.01m }, "commission above one");
        yield return Option(new SimulationOptions { SpreadBasisPoints = -1m }, "negative spread");
        yield return Option(new SimulationOptions { SlippageBasisPoints = -1m }, "negative slippage");
        yield return Option(
            new SimulationOptions { SpreadBasisPoints = 20_000m },
            "non-positive adjusted sell price");
        yield return Option(new SimulationOptions { CandleCapacity = 0 }, "zero candle capacity");
        yield return Option(new SimulationOptions { LedgerCapacity = 0 }, "zero ledger capacity");
        yield return Option(new SimulationOptions { OrderEventCapacity = 0 }, "zero event capacity");
        yield return Option(new SimulationOptions { AccountId = " " }, "blank account ID");
        yield return Option(new SimulationOptions { BaseCurrency = " " }, "blank base currency");
        yield return Option(
            new SimulationOptions { ModelledBroker = (BrokerKind)999 },
            "invalid broker enum");
        yield return Option(
            new SimulationOptions { OcoFillPolicy = (OcoFillPolicy)999 },
            "invalid OCO policy");
        yield return Option(
            new SimulationOptions { QuoteToBaseCurrencyRates = null! },
            "null conversion map");
        yield return Option(
            new SimulationOptions
            {
                QuoteToBaseCurrencyRates = new Dictionary<string, decimal> { [""] = 1m }
            },
            "blank conversion currency");
        yield return Option(
            new SimulationOptions
            {
                QuoteToBaseCurrencyRates = new Dictionary<string, decimal> { ["GBP"] = 0m }
            },
            "zero conversion rate");
    }

    private static TestCaseData Option(SimulationOptions options, string name) =>
        new TestCaseData(options).SetName($"Invalid_options_{name.Replace(' ', '_')}");

    private static IEnumerable<TestCaseData> InvalidOrders()
    {
        PlaceOrderRequest valid = ValidOrder();
        yield return Order(valid with { Instrument = default }, "instrument", "instrument");
        yield return Order(valid with { Side = OrderSide.Unknown }, "unknown side", "Buy or Sell");
        yield return Order(valid with { Side = (OrderSide)999 }, "invalid side", "Buy or Sell");
        yield return Order(valid with { Type = (StandardOrderType)999 }, "invalid type", "type is invalid");
        yield return Order(
            valid with { TimeInForce = (StandardTimeInForce)999 },
            "invalid time in force",
            "time-in-force");
        yield return Order(valid with { Quantity = default }, "default quantity", "quantity must be positive");
        yield return Order(
            valid with { Quantity = new OrderQuantity(1m, (QuantityUnit)999) },
            "invalid quantity unit",
            "valid unit");
        yield return Order(
            valid with { Quantity = new OrderQuantity(1m, QuantityUnit.QuoteCurrency) },
            "quote quantity",
            "conversion metadata");
        yield return Order(
            valid with { Quantity = new OrderQuantity(1m, QuantityUnit.Contracts) },
            "contract quantity",
            "conversion metadata");
        yield return Order(valid with { ClientOrderId = " " }, "blank client ID", "cannot be empty");
        yield return Order(
            valid with { StopLoss = new StopLossInstruction(0m) },
            "zero stop loss",
            "Stop-loss price");
        yield return Order(
            valid with { TakeProfit = new TakeProfitInstruction(-1m) },
            "negative take profit",
            "Take-profit price");
        yield return Order(
            valid with { TimeInForce = StandardTimeInForce.GoodTillDate },
            "GTD without expiry",
            "require an expiry");
        yield return Order(valid with { ExpireAt = Start }, "expiry at submission", "later");
        yield return Order(valid with { ExpireAt = Start.AddTicks(-1) }, "past expiry", "later");
        yield return Order(
            valid with { Type = StandardOrderType.Limit },
            "limit without price",
            "limit price");
        yield return Order(
            valid with { Type = StandardOrderType.Limit, LimitPrice = 0m },
            "limit with zero price",
            "limit price");
        yield return Order(
            valid with { Type = StandardOrderType.Stop },
            "stop without price",
            "stop price");
        yield return Order(
            valid with { Type = StandardOrderType.Stop, StopPrice = -1m },
            "stop with negative price",
            "stop price");
        yield return Order(
            valid with { Type = StandardOrderType.StopLimit, LimitPrice = 100m },
            "stop-limit without stop",
            "stop price");
        yield return Order(
            valid with { Type = StandardOrderType.StopLimit, StopPrice = 100m },
            "stop-limit without limit",
            "limit price");
    }

    private static PlaceOrderRequest ValidOrder() => new()
    {
        Instrument = SimulationTestHarness.DefaultInstrument,
        Side = OrderSide.Buy,
        Type = StandardOrderType.Market,
        Quantity = new OrderQuantity(1m, QuantityUnit.Units)
    };

    private static TestCaseData Order(
        PlaceOrderRequest request,
        string name,
        string expectedMessage) =>
        new TestCaseData(name, request, expectedMessage)
            .SetName($"Invalid_order_{name.Replace(' ', '_')}");
}
