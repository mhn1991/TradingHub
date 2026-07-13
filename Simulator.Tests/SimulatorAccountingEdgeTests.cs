using Brokers.Models;
using Simulator.Models;

namespace Simulator.Tests;

[TestFixture]
public sealed class SimulatorAccountingEdgeTests
{
    [TestCase(OrderSide.Buy, 100, 110, 10)]
    [TestCase(OrderSide.Buy, 100, 90, -10)]
    [TestCase(OrderSide.Sell, 100, 90, 10)]
    [TestCase(OrderSide.Sell, 100, 110, -10)]
    public async Task MarkToMarket_ReportsSignedUnrealizedProfitLoss(
        OrderSide side,
        decimal entry,
        decimal mark,
        decimal expectedProfitLoss)
    {
        await using var harness = new SimulationTestHarness();
        await harness.PlaceAsync(side: side);
        await harness.ProcessAsync(0, entry, entry, entry, entry);

        await harness.ProcessAsync(1, mark, mark, mark, mark);

        BrokerPosition position = (await harness.Broker.Positions.GetOpenPositionsAsync()).Single();
        AccountSnapshot account = (await harness.Broker.Accounts.GetAccountsAsync()).Single();
        Assert.Multiple(() =>
        {
            Assert.That(position.UnrealizedProfitLoss, Is.EqualTo(expectedProfitLoss));
            Assert.That(account.UnrealizedProfitLoss, Is.EqualTo(expectedProfitLoss));
            Assert.That(account.Balance, Is.EqualTo(harness.Options.StartingBalance));
        });
    }

    [TestCase(OrderSide.Buy, 100, 110, 10)]
    [TestCase(OrderSide.Buy, 100, 90, -10)]
    [TestCase(OrderSide.Sell, 100, 90, 10)]
    [TestCase(OrderSide.Sell, 100, 110, -10)]
    public async Task ClosingPosition_RealizesExpectedProfitLoss(
        OrderSide entrySide,
        decimal entry,
        decimal exit,
        decimal expectedProfitLoss)
    {
        await using var harness = new SimulationTestHarness();
        await harness.PlaceAsync(side: entrySide);
        await harness.ProcessAsync(0, entry, entry, entry, entry);
        await harness.PlaceAsync(side: Opposite(entrySide));

        await harness.ProcessAsync(1, exit, exit, exit, exit);

        AccountSnapshot account = (await harness.Broker.Accounts.GetAccountsAsync()).Single();
        IReadOnlyList<BrokerPosition> positions =
            await harness.Broker.Positions.GetOpenPositionsAsync();
        Assert.Multiple(() =>
        {
            Assert.That(account.Balance,
                Is.EqualTo(harness.Options.StartingBalance + expectedProfitLoss));
            Assert.That(positions, Is.Empty);
        });
    }

    [Test]
    public async Task AddingToPosition_UsesQuantityWeightedAveragePrice()
    {
        await using var harness = new SimulationTestHarness();
        await harness.PlaceAsync(quantity: 1m);
        await harness.ProcessAsync(0, 100m, 100m, 100m, 100m);
        await harness.PlaceAsync(quantity: 3m);

        await harness.ProcessAsync(1, 120m, 120m, 120m, 120m);

        BrokerPosition position = (await harness.Broker.Positions.GetOpenPositionsAsync()).Single();
        Assert.Multiple(() =>
        {
            Assert.That(position.Quantity, Is.EqualTo(4m));
            Assert.That(position.AveragePrice, Is.EqualTo(115m));
        });
    }

    [Test]
    public async Task PartialClose_RealizesOnlyClosedQuantityAndKeepsEntryPrice()
    {
        await using var harness = new SimulationTestHarness();
        await harness.PlaceAsync(quantity: 2m);
        await harness.ProcessAsync(0, 100m, 100m, 100m, 100m);
        await harness.PlaceAsync(side: OrderSide.Sell, quantity: 1m);

        await harness.ProcessAsync(1, 110m, 110m, 110m, 110m);

        BrokerPosition position = (await harness.Broker.Positions.GetOpenPositionsAsync()).Single();
        AccountSnapshot account = (await harness.Broker.Accounts.GetAccountsAsync()).Single();
        Assert.Multiple(() =>
        {
            Assert.That(position.Side, Is.EqualTo(OrderSide.Buy));
            Assert.That(position.Quantity, Is.EqualTo(1m));
            Assert.That(position.AveragePrice, Is.EqualTo(100m));
            Assert.That(account.Balance, Is.EqualTo(harness.Options.StartingBalance + 10m));
        });
    }

    [TestCase(OrderSide.Buy, OrderSide.Sell)]
    [TestCase(OrderSide.Sell, OrderSide.Buy)]
    public async Task OversizedClose_ReversesPositionAtNewFillPrice(
        OrderSide entrySide,
        OrderSide reversalSide)
    {
        await using var harness = new SimulationTestHarness();
        await harness.PlaceAsync(side: entrySide, quantity: 1m);
        await harness.ProcessAsync(0, 100m, 100m, 100m, 100m);
        await harness.PlaceAsync(side: reversalSide, quantity: 3m);

        await harness.ProcessAsync(1, 110m, 110m, 110m, 110m);

        BrokerPosition position = (await harness.Broker.Positions.GetOpenPositionsAsync()).Single();
        decimal expectedRealized = entrySide == OrderSide.Buy ? 10m : -10m;
        AccountSnapshot account = (await harness.Broker.Accounts.GetAccountsAsync()).Single();
        Assert.Multiple(() =>
        {
            Assert.That(position.Side, Is.EqualTo(reversalSide));
            Assert.That(position.Quantity, Is.EqualTo(2m));
            Assert.That(position.AveragePrice, Is.EqualTo(110m));
            Assert.That(account.Balance,
                Is.EqualTo(harness.Options.StartingBalance + expectedRealized));
        });
    }

    [Test]
    public async Task Commission_IsChargedOnEveryFillAndRecordedInBaseCurrency()
    {
        await using var harness = new SimulationTestHarness(new SimulationOptions
        {
            StartingBalance = 1_000m,
            Leverage = 20m,
            CommissionRate = 0.01m,
            SpreadBasisPoints = 0m,
            SlippageBasisPoints = 0m,
            LedgerCapacity = 20
        });
        await harness.PlaceAsync();
        await harness.ProcessAsync(0, 100m, 100m, 100m, 100m);
        await harness.PlaceAsync(side: OrderSide.Sell);
        await harness.ProcessAsync(1, 110m, 110m, 110m, 110m);

        SimulationResult result = harness.Broker.State.BuildResult(
            SimulationTestHarness.DefaultStart,
            SimulationTestHarness.DefaultStart.AddMinutes(10));

        Assert.Multiple(() =>
        {
            Assert.That(result.TotalCommission, Is.EqualTo(2.1m));
            Assert.That(result.FinalBalance, Is.EqualTo(1_007.9m));
            Assert.That(result.Ledger.Count(entry => entry.Type == LedgerEntryType.Commission),
                Is.EqualTo(2));
            Assert.That(result.Ledger.Where(entry => entry.Type == LedgerEntryType.Commission)
                .All(entry => entry.Amount < 0m && entry.Currency == "USD"), Is.True);
        });
    }

    [Test]
    public async Task AccountReportsMarginAndAvailableFunds()
    {
        await using var harness = new SimulationTestHarness(
            SimulationTestHarness.ZeroCostOptions(startingBalance: 1_000m, leverage: 10m));
        await harness.PlaceAsync(quantity: 2m);

        await harness.ProcessAsync(0, 100m, 100m, 100m, 100m);

        AccountSnapshot account = (await harness.Broker.Accounts.GetAccountsAsync()).Single();
        Assert.Multiple(() =>
        {
            Assert.That(account.MarginUsed, Is.EqualTo(20m));
            Assert.That(account.Available, Is.EqualTo(980m));
            Assert.That(account.CanTrade, Is.True);
        });
    }

    [TestCase(1, true)]
    [TestCase(1.0001, false)]
    public async Task MarginBoundary_IsInclusive(decimal quantity, bool expectedFill)
    {
        await using var harness = new SimulationTestHarness(
            SimulationTestHarness.ZeroCostOptions(startingBalance: 100m, leverage: 1m));
        await harness.PlaceAsync(quantity: quantity);

        await harness.ProcessAsync(0, 100m, 100m, 100m, 100m);

        IReadOnlyList<BrokerPosition> positions =
            await harness.Broker.Positions.GetOpenPositionsAsync();
        Assert.That(positions.Count == 1, Is.EqualTo(expectedFill));
    }

    [Test]
    public async Task RiskReducingClose_IsAllowedWhenAvailableFundsAreExhausted()
    {
        await using var harness = new SimulationTestHarness(
            SimulationTestHarness.ZeroCostOptions(startingBalance: 100m, leverage: 1m));
        await harness.PlaceAsync();
        await harness.ProcessAsync(0, 100m, 100m, 100m, 100m);
        await harness.ProcessAsync(1, 1m, 1m, 1m, 1m);
        AccountSnapshot distressed = (await harness.Broker.Accounts.GetAccountsAsync()).Single();
        await harness.PlaceAsync(side: OrderSide.Sell);

        await harness.ProcessAsync(2, 1m, 1m, 1m, 1m);

        AccountSnapshot closed = (await harness.Broker.Accounts.GetAccountsAsync()).Single();
        Assert.Multiple(() =>
        {
            Assert.That(distressed.Available, Is.EqualTo(0m));
            Assert.That(distressed.CanTrade, Is.False);
            Assert.That(closed.Balance, Is.EqualTo(1m));
        });
    }

    [Test]
    public async Task QuoteConversion_AppliesToUnrealizedProfitAndMargin()
    {
        InstrumentKey instrument = new("FX:EUR/GBP");
        await using var harness = new SimulationTestHarness(new SimulationOptions
        {
            StartingBalance = 1_000m,
            Leverage = 10m,
            CommissionRate = 0m,
            SpreadBasisPoints = 0m,
            SlippageBasisPoints = 0m,
            QuoteToBaseCurrencyRates = new Dictionary<string, decimal> { ["gbp"] = 1.25m }
        });
        await harness.PlaceAsync(instrument: instrument, quantity: 2m);
        await harness.ProcessAsync(0, 100m, 100m, 100m, 100m, instrument);

        await harness.ProcessAsync(1, 110m, 110m, 110m, 110m, instrument);

        AccountSnapshot account = (await harness.Broker.Accounts.GetAccountsAsync()).Single();
        BrokerPosition position = (await harness.Broker.Positions.GetOpenPositionsAsync()).Single();
        Assert.Multiple(() =>
        {
            Assert.That(position.UnrealizedProfitLoss, Is.EqualTo(25m));
            Assert.That(account.MarginUsed, Is.EqualTo(27.5m));
            Assert.That(account.Available, Is.EqualTo(997.5m));
        });
    }

    [Test]
    public async Task LedgerCapacity_DropsOldestEntriesButKeepsMonotonicSequences()
    {
        await using var harness = new SimulationTestHarness(new SimulationOptions
        {
            StartingBalance = 1_000m,
            CommissionRate = 0.01m,
            SpreadBasisPoints = 0m,
            SlippageBasisPoints = 0m,
            LedgerCapacity = 2
        });
        await harness.PlaceAsync();
        await harness.ProcessAsync(0, 100m, 100m, 100m, 100m);
        await harness.PlaceAsync(side: OrderSide.Sell);
        await harness.ProcessAsync(1, 110m, 110m, 110m, 110m);

        IReadOnlyList<LedgerEntry> ledger = harness.Broker.State.GetLedger();

        Assert.Multiple(() =>
        {
            Assert.That(ledger, Has.Count.EqualTo(2));
            Assert.That(ledger.Select(entry => entry.Sequence), Is.Ordered.Ascending);
            Assert.That(ledger.All(entry => entry.Type != LedgerEntryType.Deposit), Is.True);
        });
    }

    [Test]
    public async Task MarginEnforcement_CanBeDisabledForStressScenarios()
    {
        await using var harness = new SimulationTestHarness(new SimulationOptions
        {
            StartingBalance = 100m,
            Leverage = 1m,
            CommissionRate = 0m,
            SpreadBasisPoints = 0m,
            SlippageBasisPoints = 0m,
            EnforceMarginRequirements = false
        });
        await harness.PlaceAsync(quantity: 2m);

        await harness.ProcessAsync(0, 100m, 100m, 100m, 100m);

        BrokerPosition position = (await harness.Broker.Positions.GetOpenPositionsAsync()).Single();
        AccountSnapshot account = (await harness.Broker.Accounts.GetAccountsAsync()).Single();
        Assert.Multiple(() =>
        {
            Assert.That(position.Quantity, Is.EqualTo(2m));
            Assert.That(account.Available, Is.EqualTo(-100m));
            Assert.That(account.CanTrade, Is.False);
        });
    }

    [Test]
    public async Task ResultCounters_DistinguishSubmissionFillAndRejection()
    {
        await using var harness = new SimulationTestHarness(
            SimulationTestHarness.ZeroCostOptions(startingBalance: 100m, leverage: 1m));
        await harness.PlaceAsync(quantityUnit: QuantityUnit.QuoteCurrency);
        await harness.PlaceAsync(quantity: 2m);
        await harness.ProcessAsync(0, 100m, 100m, 100m, 100m);
        await harness.PlaceAsync(quantity: 1m);
        await harness.ProcessAsync(1, 50m, 50m, 50m, 50m);

        SimulationResult result = harness.Broker.State.BuildResult(
            SimulationTestHarness.DefaultStart,
            SimulationTestHarness.DefaultStart.AddMinutes(10));
        Assert.Multiple(() =>
        {
            Assert.That(result.SubmittedOrders, Is.EqualTo(3));
            Assert.That(result.FilledOrders, Is.EqualTo(1));
            Assert.That(result.RejectedOrders, Is.EqualTo(2));
            Assert.That(result.OpenPositions, Has.Count.EqualTo(1));
        });
    }

    private static OrderSide Opposite(OrderSide side) =>
        side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
}
