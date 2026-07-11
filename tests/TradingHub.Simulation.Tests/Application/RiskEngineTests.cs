using TradingHub.Application.Risk;
using TradingHub.Domain.Trading;

namespace TradingHub.Simulation.Tests.Application;

[TestFixture]
public sealed class RiskEngineTests
{
    [Test]
    public void Evaluate_RejectsOrderThatWouldCreateShortSpotPosition()
    {
        var instrument = TestDataFactory.CreateInstrument();
        var bar = TestDataFactory.CreateBar(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            100m,
            101m);
        var engine = new RiskEngine(new RiskLimits
        {
            MaximumOrderQuantity = 1m,
            MaximumAbsolutePosition = 1m,
            MaximumSpreadBps = 100m,
            MaximumOpenOrders = 5,
            AllowShortSelling = false
        });
        var intent = new TradeIntent
        {
            IntentId = "INT-1",
            StrategyId = "test",
            AccountId = "SIM-ACCOUNT",
            InstrumentId = instrument.Id,
            Side = OrderSide.Sell,
            Quantity = 0.1m,
            OrderType = OrderType.Market,
            CreatedAt = bar.CloseTime,
            ExpiresAt = bar.CloseTime.AddMinutes(1)
        };
        var context = new RiskContext
        {
            Now = bar.CloseTime,
            Instrument = instrument,
            LatestBar = bar,
            CurrentNetQuantity = 0m,
            OpenOrderCount = 0
        };

        var decision = engine.Evaluate(intent, context);

        Assert.Multiple(() =>
        {
            Assert.That(decision.IsApproved, Is.False);
            Assert.That(decision.Code, Is.EqualTo("SHORT_SELLING_DISABLED"));
        });
    }
}
