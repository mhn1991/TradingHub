using TradingHub.Domain.Trading;
using TradingHub.Simulation.Broker;
using TradingHub.Simulation.Execution;
using TradingHub.Simulation.Tests.TestDoubles;

namespace TradingHub.Simulation.Tests.Simulation;

[TestFixture]
public sealed class SimulatedBrokerTests
{
    [Test]
    public async Task MarketOrder_CannotFillOnTheBarThatCreatedIt()
    {
        var instrument = TestDataFactory.CreateInstrument();
        var instruments = new Dictionary<string, Domain.Markets.InstrumentDefinition>
        {
            [instrument.Id] = instrument
        };
        var firstBar = TestDataFactory.CreateBar(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            100m,
            101m);
        var secondBar = TestDataFactory.CreateBar(firstBar.CloseTime, 101m, 102m);
        var clock = new TestClock { UtcNow = firstBar.CloseTime };
        var broker = CreateBroker(clock, instruments);

        var submission = await broker.SubmitOrderAsync(CreateMarketOrder(firstBar.CloseTime));
        var sameBarReports = broker.ProcessBar(firstBar);
        var nextBarReports = broker.ProcessBar(secondBar);

        Assert.Multiple(() =>
        {
            Assert.That(submission.Outcome, Is.EqualTo(SubmissionOutcome.Accepted));
            Assert.That(sameBarReports, Is.Empty);
            Assert.That(nextBarReports, Has.Count.EqualTo(1));
            Assert.That(nextBarReports[0].LastFillPrice, Is.EqualTo(101.10m));
            Assert.That(nextBarReports[0].OccurredAt, Is.EqualTo(secondBar.CloseTime));
        });
    }

    [Test]
    public async Task CancelOrder_RemovesItBeforeTheNextBar()
    {
        var instrument = TestDataFactory.CreateInstrument();
        var instruments = new Dictionary<string, Domain.Markets.InstrumentDefinition>
        {
            [instrument.Id] = instrument
        };
        var bar = TestDataFactory.CreateBar(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            100m,
            101m);
        var clock = new TestClock { UtcNow = bar.CloseTime };
        var broker = CreateBroker(clock, instruments);
        var submission = await broker.SubmitOrderAsync(CreateMarketOrder(bar.CloseTime));

        var cancellation = await broker.CancelOrderAsync(new OrderCancellationRequest
        {
            AccountId = "SIM-ACCOUNT",
            BrokerOrderId = submission.BrokerOrderId!,
            InstrumentId = instrument.Id
        });
        var reports = broker.ProcessBar(TestDataFactory.CreateBar(bar.CloseTime, 101m, 102m));

        Assert.Multiple(() =>
        {
            Assert.That(cancellation.Status, Is.EqualTo(OrderStatus.Cancelled));
            Assert.That(reports, Is.Empty);
        });
    }

    private static SimulatedBroker CreateBroker(
        TestClock clock,
        IReadOnlyDictionary<string, Domain.Markets.InstrumentDefinition> instruments)
    {
        return new SimulatedBroker(
            new SimulatedBrokerOptions
            {
                BrokerId = "sim",
                AccountId = "SIM-ACCOUNT",
                AccountCurrency = "USDT",
                InitialBalance = 10_000m
            },
            clock,
            instruments,
            new ExecutionModelOptions());
    }

    private static OrderRequest CreateMarketOrder(DateTimeOffset createdAt)
    {
        return new OrderRequest
        {
            OrderId = "ORD-1",
            ClientOrderId = "th-1",
            IntentId = "INT-1",
            StrategyId = "test",
            AccountId = "SIM-ACCOUNT",
            InstrumentId = "CRYPTO:BTC-USDT:SPOT",
            Side = OrderSide.Buy,
            Quantity = 0.1m,
            OrderType = OrderType.Market,
            TimeInForce = TimeInForce.FillOrKill,
            CreatedAt = createdAt
        };
    }
}
