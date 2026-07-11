using System.Net;
using TradingHub.Brokers;
using TradingHub.Brokers.Binance;
using TradingHub.Brokers.Oanda;
using TradingHub.Domain.Trading;
using TradingHub.Simulation.Tests.TestDoubles;

namespace TradingHub.Simulation.Tests.Brokers;

[TestFixture]
public sealed class BrokerEnvironmentSafetyTests
{
    [Test]
    public async Task OandaLive_DoesNotSendWithoutExactAccountConfirmation()
    {
        var handler = CreateUnexpectedRequestHandler();
        var gateway = new OandaBrokerGateway(
            new OandaBrokerOptions
            {
                BrokerId = "oanda-live",
                Environment = OandaEnvironment.Live,
                AccountId = "OANDA-LIVE-1",
                AccessToken = "not-a-real-token"
            },
            new HttpClient(handler),
            CreateInstrumentMap("GBP_JPY"),
            CreateClock());

        var result = await gateway.SubmitOrderAsync(CreateOrder("OANDA-LIVE-1", "FX:GBP-JPY"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SubmissionOutcome.Rejected));
            Assert.That(result.Reason, Does.Contain("locked"));
            Assert.That(handler.RequestCount, Is.Zero);
        });
    }

    [Test]
    public async Task BinanceLive_DoesNotSendWithoutExactAccountConfirmation()
    {
        var handler = CreateUnexpectedRequestHandler();
        var gateway = new BinanceBrokerGateway(
            new BinanceBrokerOptions
            {
                BrokerId = "binance-live",
                Environment = BinanceEnvironment.Live,
                AccountId = "BINANCE-LIVE",
                Credentials = new BinanceCredentials
                {
                    ApiKey = "not-a-real-key",
                    SecretKey = "not-a-real-secret"
                }
            },
            new HttpClient(handler),
            CreateInstrumentMap("BTCUSDT"),
            CreateClock());

        var result = await gateway.SubmitOrderAsync(CreateOrder("BINANCE-LIVE", "CRYPTO:BTC-USDT:SPOT"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SubmissionOutcome.Rejected));
            Assert.That(result.Reason, Does.Contain("locked"));
            Assert.That(handler.RequestCount, Is.Zero);
        });
    }

    private static StubHttpMessageHandler CreateUnexpectedRequestHandler()
    {
        return new StubHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.InternalServerError)));
    }

    private static BrokerInstrumentMap CreateInstrumentMap(string brokerSymbol)
    {
        return new BrokerInstrumentMap(new Dictionary<string, string>
        {
            [brokerSymbol == "GBP_JPY" ? "FX:GBP-JPY" : "CRYPTO:BTC-USDT:SPOT"] = brokerSymbol
        });
    }

    private static TestClock CreateClock()
    {
        return new TestClock
        {
            UtcNow = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };
    }

    private static OrderRequest CreateOrder(string accountId, string instrumentId)
    {
        return new OrderRequest
        {
            OrderId = "ORD-1",
            ClientOrderId = "th-1",
            IntentId = "INT-1",
            StrategyId = "test",
            AccountId = accountId,
            InstrumentId = instrumentId,
            Side = OrderSide.Buy,
            Quantity = instrumentId.StartsWith("FX:", StringComparison.Ordinal) ? 100m : 0.1m,
            OrderType = OrderType.Market,
            TimeInForce = TimeInForce.FillOrKill,
            CreatedAt = CreateClock().UtcNow
        };
    }
}
