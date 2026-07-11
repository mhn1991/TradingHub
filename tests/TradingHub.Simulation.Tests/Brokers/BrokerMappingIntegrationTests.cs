using System.Net;
using TradingHub.Brokers;
using TradingHub.Brokers.Binance;
using TradingHub.Brokers.Oanda;
using TradingHub.Domain.Trading;
using TradingHub.Simulation.Tests.TestDoubles;

namespace TradingHub.Simulation.Tests.Brokers;

[TestFixture]
public sealed class BrokerMappingIntegrationTests
{
    [Test]
    public async Task OandaPractice_MapsExternalFillToCanonicalExecution()
    {
        string? requestBody = null;
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse(HttpStatusCode.Created, OandaFillJson);
        });
        var gateway = new OandaBrokerGateway(
            CreateOandaOptions(),
            new HttpClient(handler),
            Map("FX:GBP-JPY", "GBP_JPY"),
            Clock());

        var result = await gateway.SubmitOrderAsync(Order("OANDA-PRACTICE", "FX:GBP-JPY", 100m));

        Assert.Multiple(() =>
        {
            Assert.That(requestBody, Does.Contain("GBP_JPY"));
            Assert.That(requestBody, Does.Contain("\"units\":\"100\""));
            Assert.That(result.Status, Is.EqualTo(OrderStatus.Filled));
            Assert.That(result.ImmediateExecutions, Has.Count.EqualTo(1));
            Assert.That(result.ImmediateExecutions[0].InstrumentId, Is.EqualTo("FX:GBP-JPY"));
            Assert.That(result.ImmediateExecutions[0].LastFillPrice, Is.EqualTo(191.250m));
        });
    }

    [Test]
    public async Task BinanceTestnet_SignsRequestAndMapsFillToCanonicalExecution()
    {
        Uri? requestUri = null;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            requestUri = request.RequestUri;
            Assert.That(request.Headers.Contains("X-MBX-APIKEY"), Is.True);
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, BinanceFillJson));
        });
        var gateway = new BinanceBrokerGateway(
            CreateBinanceOptions(),
            new HttpClient(handler),
            Map("CRYPTO:BTC-USDT:SPOT", "BTCUSDT"),
            Clock());

        var result = await gateway.SubmitOrderAsync(
            Order("BINANCE-TESTNET", "CRYPTO:BTC-USDT:SPOT", 0.1m));

        Assert.Multiple(() =>
        {
            Assert.That(requestUri!.Host, Is.EqualTo("testnet.binance.vision"));
            Assert.That(requestUri.Query, Does.Contain("symbol=BTCUSDT"));
            Assert.That(requestUri.Query, Does.Contain("signature="));
            Assert.That(result.Status, Is.EqualTo(OrderStatus.Filled));
            Assert.That(result.ImmediateExecutions, Has.Count.EqualTo(1));
            Assert.That(result.ImmediateExecutions[0].InstrumentId, Is.EqualTo("CRYPTO:BTC-USDT:SPOT"));
            Assert.That(result.ImmediateExecutions[0].FeeAsset, Is.EqualTo("BNB"));
        });
    }

    private static OandaBrokerOptions CreateOandaOptions()
    {
        return new OandaBrokerOptions
        {
            BrokerId = "oanda-practice",
            Environment = OandaEnvironment.Practice,
            AccountId = "OANDA-PRACTICE",
            AccessToken = "not-a-real-token"
        };
    }

    private static BinanceBrokerOptions CreateBinanceOptions()
    {
        return new BinanceBrokerOptions
        {
            BrokerId = "binance-testnet",
            Environment = BinanceEnvironment.SpotTestnet,
            AccountId = "BINANCE-TESTNET",
            Credentials = new BinanceCredentials
            {
                ApiKey = "not-a-real-key",
                SecretKey = "not-a-real-secret"
            }
        };
    }

    private static BrokerInstrumentMap Map(string instrumentId, string brokerSymbol)
    {
        return new BrokerInstrumentMap(new Dictionary<string, string>
        {
            [instrumentId] = brokerSymbol
        });
    }

    private static TestClock Clock()
    {
        return new TestClock
        {
            UtcNow = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };
    }

    private static OrderRequest Order(string accountId, string instrumentId, decimal quantity)
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
            Quantity = quantity,
            OrderType = OrderType.Market,
            TimeInForce = TimeInForce.FillOrKill,
            CreatedAt = Clock().UtcNow
        };
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
    }

    private const string OandaFillJson = """
        {
          "orderCreateTransaction": {
            "id": "101",
            "time": "2026-01-01T00:00:00Z"
          },
          "orderFillTransaction": {
            "id": "102",
            "orderID": "101",
            "instrument": "GBP_JPY",
            "units": "100",
            "price": "191.250",
            "commission": "0",
            "time": "2026-01-01T00:00:00Z",
            "reason": "MARKET_ORDER"
          }
        }
        """;

    private const string BinanceFillJson = """
        {
          "symbol": "BTCUSDT",
          "orderId": 12345,
          "clientOrderId": "th-1",
          "transactTime": 1767225600000,
          "price": "0",
          "origQty": "0.1",
          "executedQty": "0.1",
          "cummulativeQuoteQty": "10",
          "status": "FILLED",
          "timeInForce": "FOK",
          "type": "MARKET",
          "side": "BUY",
          "fills": [
            {
              "price": "100",
              "qty": "0.1",
              "commission": "0.0001",
              "commissionAsset": "BNB",
              "tradeId": 99
            }
          ]
        }
        """;
}
