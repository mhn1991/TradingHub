using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Brokers.Abstractions;
using Brokers.Binance;
using Brokers.Exceptions;
using Brokers.Infrastructure;
using Brokers.Models;
using Brokers.Oanda;
using Networking.Abstractions;
using NUnit.Framework;

namespace TradingHub.UnitTests;

[TestFixture]
public sealed class DomainTests
{
    [Test]
    public void CandleQuery_RejectsInvalidDefaultsAndDateRanges()
    {
        var missingInstrument = new CandleQuery(default, BarInterval.Minutes(1));
        var reversedRange = new CandleQuery(
            "FX:EUR/USD",
            BarInterval.Minutes(1),
            From: DateTimeOffset.Parse("2026-01-02T00:00:00Z"),
            To: DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

        Assert.Multiple(() =>
        {
            Assert.That(() => missingInstrument.Validate(100, "Test"), Throws.ArgumentException);
            Assert.That(() => reversedRange.Validate(100, "Test"), Throws.ArgumentException);
        });
    }

    [Test]
    public void MissingRequiredDecimal_IsNotConvertedToZero()
    {
        Assert.That(() => BrokerJson.ParseDecimal(null), Throws.TypeOf<FormatException>());
    }

    [Test]
    public void NativeInstrumentMapping_RoundTripsCanonicalIdentity()
    {
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CRYPTO:BTC/USDT"] = "BTCUSDT"
        };

        InstrumentKey canonical = InstrumentMappers.FromNative("btcusdt", mappings);

        Assert.That(canonical, Is.EqualTo(new InstrumentKey("CRYPTO:BTC/USDT")));
    }

    [Test]
    public void BrokerApiException_BoundsStoredResponseBody()
    {
        var exception = new BrokerApiException(
            BrokerKind.Binance,
            HttpStatusCode.BadRequest,
            new string('x', 20_000));

        Assert.Multiple(() =>
        {
            Assert.That(exception.ResponseBodyTruncated, Is.True);
            Assert.That(exception.ResponseBody.Length, Is.LessThan(20_000));
        });
    }

    [Test]
    public async Task BinanceClient_AllowsPublicMarketDataWithoutApiCredentials()
    {
        await using var client = new BinanceBrokerClient(new BinanceOptions
        {
            Environment = BrokerEnvironment.Live,
            BaseAddress = new Uri("https://data-api.binance.vision/")
        });

        Assert.Multiple(() =>
        {
            Assert.That(client.Capabilities.SupportsMarketData, Is.True);
            Assert.That(client.Capabilities.SupportsAccounts, Is.False);
            Assert.That(client.Capabilities.SupportsOrders, Is.False);
            Assert.That(client.Capabilities.SupportsCosts, Is.False);
        });
    }

    [Test]
    public void BinanceSigner_IsDeterministicWithFixedTime()
    {
        var signer = new BinanceRequestSigner(
            "key",
            "secret",
            TimeSpan.FromSeconds(5),
            new FixedTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000)));

        string first = signer.CreateSignedQuery(
            [new KeyValuePair<string, string?>("symbol", "BTCUSDT")]);
        string second = signer.CreateSignedQuery(
            [new KeyValuePair<string, string?>("symbol", "BTCUSDT")]);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(second));
            Assert.That(first, Does.Contain("timestamp=1700000000000"));
            Assert.That(first, Does.Contain("signature="));
        });
    }

    [Test]
    public void BinanceKline_DoesNotConvertNullPriceToZero()
    {
        using JsonDocument document = JsonDocument.Parse(
            "[1700000000000,null,\"2\",\"0.5\",\"1.5\",\"10\",1700000059999]");
        JsonElement[] row = document.RootElement.EnumerateArray().ToArray();

        Assert.That(() => BinanceKlineRow.FromJson(row), Throws.TypeOf<JsonException>());
    }

    [Test]
    public void OandaCandle_CloseTimeIsDerivedFromItsInterval()
    {
        OandaCandle source = new()
        {
            Complete = true,
            Time = "2026-01-01T00:00:00Z",
            Mid = new OandaPrice
            {
                Open = "1.1000",
                High = "1.1100",
                Low = "1.0900",
                Close = "1.1050"
            }
        };
        Candle candle = OandaMappings.ToCandle(
            source,
            new CandleQuery("FX:EUR/USD", BarInterval.Minutes(5)));

        Assert.That(
            candle.CloseTime,
            Is.EqualTo(DateTimeOffset.Parse("2026-01-01T00:05:00Z")));
    }

    [Test]
    public void OandaOrderMapping_CreatesSignedUnitsAndProtectiveOrders()
    {
        var request = new PlaceOrderRequest
        {
            Instrument = new InstrumentKey("FX:EUR/USD"),
            Side = OrderSide.Sell,
            Type = StandardOrderType.Limit,
            Quantity = new OrderQuantity(250m, QuantityUnit.Units),
            LimitPrice = 1.125m,
            StopLoss = new StopLossInstruction(1.14m),
            TakeProfit = new TakeProfitInstruction(1.10m)
        };

        OandaCreateOrderEnvelope payload = OandaMappings.ToOrderRequest(
            request,
            "EUR_USD",
            "client-1");

        Assert.Multiple(() =>
        {
            Assert.That(payload.Order.Type, Is.EqualTo("LIMIT"));
            Assert.That(payload.Order.Units, Is.EqualTo("-250"));
            Assert.That(payload.Order.Price, Is.EqualTo("1.125"));
            Assert.That(payload.Order.TimeInForce, Is.EqualTo("GTC"));
            Assert.That(payload.Order.PositionFill, Is.EqualTo("DEFAULT"));
            Assert.That(payload.Order.ClientExtensions!.Id, Is.EqualTo("client-1"));
            Assert.That(payload.Order.StopLossOnFill!.Price, Is.EqualTo("1.14"));
            Assert.That(payload.Order.TakeProfitOnFill!.Price, Is.EqualTo("1.1"));
        });
    }

    [Test]
    public void OandaCloseMapping_UsesReduceOnlyPositionFill()
    {
        OandaCreateOrderEnvelope payload = OandaMappings.ToOrderRequest(
            new PlaceOrderRequest
            {
                Instrument = new InstrumentKey("FX:EUR/USD"),
                Side = OrderSide.Sell,
                Type = StandardOrderType.Market,
                Quantity = new OrderQuantity(100m, QuantityUnit.Units),
                ClientOrderId = "reduce-only-close",
                ReduceOnly = true
            },
            "EUR_USD",
            "reduce-only-close");

        Assert.That(payload.Order.PositionFill, Is.EqualTo("REDUCE_ONLY"));
    }

    [Test]
    public void OandaOrderMutationCommands_AreNeverAutomaticallyRetried()
    {
        var payload = new OandaCreateOrderEnvelope
        {
            Order = new OandaCreateOrderRequest
            {
                Type = "MARKET",
                Instrument = "EUR_USD",
                Units = "100",
                TimeInForce = "FOK"
            }
        };
        var place = new OandaPlaceOrderCommand(new TransportId("test"), "account", payload);
        var cancel = new OandaCancelOrderCommand(new TransportId("test"), "account", "42");

        Assert.Multiple(() =>
        {
            Assert.That(place.IsIdempotent, Is.False);
            Assert.That(place.MaxTransientRetries, Is.Zero);
            Assert.That(cancel.IsIdempotent, Is.False);
            Assert.That(cancel.MaxTransientRetries, Is.Zero);
            Assert.That(place.CreateRequest().Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(cancel.CreateRequest().Method, Is.EqualTo(HttpMethod.Put));
        });
    }

    [Test]
    public void OandaPriceStream_UsesBestBidAskAndIgnoresHeartbeats()
    {
        var price = new OandaPricingStreamMessage
        {
            Type = "PRICE",
            Instrument = "EUR_USD",
            Time = "2026-07-13T10:00:00Z",
            Tradeable = true,
            Bids = [new OandaPriceBucket { Price = "1.1000" }, new OandaPriceBucket { Price = "1.1001" }],
            Asks = [new OandaPriceBucket { Price = "1.1004" }, new OandaPriceBucket { Price = "1.1003" }]
        };

        OandaPriceTick tick = OandaMappings.ToPriceTick(price)!;

        Assert.Multiple(() =>
        {
            Assert.That(tick.Bid, Is.EqualTo(1.1001m));
            Assert.That(tick.Ask, Is.EqualTo(1.1003m));
            Assert.That(tick.Midpoint, Is.EqualTo(1.1002m));
            Assert.That(tick.IsTradeable, Is.True);
            Assert.That(
                OandaMappings.ToPriceTick(new OandaPricingStreamMessage { Type = "HEARTBEAT" }),
                Is.Null);
        });
    }

    [Test]
    public void OandaTransactions_MapToCanonicalOrderEvents()
    {
        var transaction = new OandaTransaction
        {
            Id = "100",
            OrderId = "99",
            Type = "ORDER_FILL",
            Instrument = "EUR_USD",
            Units = "-250",
            Price = "1.1234",
            Time = "2026-07-13T10:00:00Z",
            ClientExtensions = new OandaClientExtensions { Id = "client-1" }
        };

        OrderEvent orderEvent = OandaMappings.ToOrderEvent(transaction, new Dictionary<string, string>())!;

        Assert.Multiple(() =>
        {
            Assert.That(orderEvent.BrokerOrderId, Is.EqualTo("99"));
            Assert.That(orderEvent.ClientOrderId, Is.EqualTo("client-1"));
            Assert.That(orderEvent.Instrument, Is.EqualTo(new InstrumentKey("EUR_USD")));
            Assert.That(orderEvent.Type, Is.EqualTo(OrderEventType.Filled));
            Assert.That(orderEvent.FillQuantity, Is.EqualTo(250m));
            Assert.That(orderEvent.FillPrice, Is.EqualTo(1.1234m));
        });
    }

    [Test]
    public async Task OandaTradingClient_ReturnsConfirmedSubmissionAndCancellation()
    {
        var gateway = new FakeOandaGateway();
        using var streamingClient = new HttpClient();
        var client = new OandaOrderClient(
            gateway,
            new TransportId("test"),
            "account",
            new Dictionary<string, string> { ["FX:EUR/USD"] = "EUR_USD" },
            streamingClient);
        var request = new PlaceOrderRequest
        {
            Instrument = new InstrumentKey("FX:EUR/USD"),
            Side = OrderSide.Buy,
            Type = StandardOrderType.Market,
            Quantity = new OrderQuantity(100m, QuantityUnit.Units),
            ClientOrderId = "client-1"
        };

        OrderSubmission submission = await client.PlaceOrderAsync(request);
        await client.CancelOrderAsync("42");

        Assert.Multiple(() =>
        {
            Assert.That(submission.Status, Is.EqualTo(SubmissionStatus.Accepted));
            Assert.That(submission.Certainty, Is.EqualTo(ExecutionCertainty.Accepted));
            Assert.That(submission.BrokerOrderId, Is.EqualTo("42"));
            Assert.That(gateway.Commands, Is.EqualTo(new[]
            {
                nameof(OandaPlaceOrderCommand),
                nameof(OandaCancelOrderCommand)
            }));
        });
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeOandaGateway : INetworkGateway
    {
        public List<string> Commands { get; } = [];

        public Task<TResponse> SendAsync<TResponse>(
            INetworkCommand<TResponse> command,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.GetType().Name);
            object response = command switch
            {
                OandaPlaceOrderCommand => new OandaOrderMutationResponse
                {
                    OrderCreateTransaction = new OandaTransaction { Id = "42", Type = "MARKET_ORDER" }
                },
                OandaCancelOrderCommand => new OandaOrderMutationResponse
                {
                    OrderCancelTransaction = new OandaTransaction
                    {
                        Id = "43",
                        OrderId = "42",
                        Type = "ORDER_CANCEL"
                    }
                },
                _ => throw new AssertionException($"Unexpected command {command.GetType().Name}")
            };
            return Task.FromResult((TResponse)response);
        }

        public async IAsyncEnumerable<TEvent> SubscribeAsync<TEvent>(
            INetworkSubscription<TEvent> subscription,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
