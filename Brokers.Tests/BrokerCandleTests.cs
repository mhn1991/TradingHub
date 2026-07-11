using System.Net;
using System.Text;
using Brokers.Tests.TestDoubles;
using Networking;

namespace Brokers.Tests;

public sealed class BrokerCandleTests
{
    [Test]
    public async Task Binance_TranslatesCanonicalQueryAndRepackagesKlines()
    {
        const string payload = """
            [
              [
                1704067200000,
                "42000.10",
                "42100.20",
                "41900.30",
                "42050.40",
                "12.50",
                1704067499999,
                "525630.75",
                81,
                "0",
                "0",
                "0"
              ]
            ]
            """;
        var dispatcher = CreateDispatcher(HttpStatusCode.OK, payload);
        var factory = new BrokerFactory(
            dispatcher,
            timeProvider: new FixedTimeProvider(new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero)));
        IBroker broker = factory.Create(
            CreateBinanceConfiguration(),
            [new BrokerInstrumentConfiguration("CRYPTO:BTC-USDT", "BTCUSDT")]);
        DateTimeOffset start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset end = new(2024, 1, 1, 1, 0, 0, TimeSpan.Zero);
        var query = new CandleQuery(
            "CRYPTO:BTC-USDT",
            CandleTimeframe.Minutes(5),
            start,
            end,
            limit: 100);

        BrokerResult<CandleBatch> result = await broker.GetCandlesAsync(query);

        Assert.That(result.IsSuccess, Is.True);
        HttpNetworkRequest request = (HttpNetworkRequest)dispatcher.Requests.Single();
        IReadOnlyDictionary<string, string> parameters = ParseQuery(request.Endpoint);
        Candle candle = result.Value!.Candles.Single();
        Assert.Multiple(() =>
        {
            Assert.That(request.Endpoint.AbsolutePath, Is.EqualTo("/api/v3/klines"));
            Assert.That(parameters["symbol"], Is.EqualTo("BTCUSDT"));
            Assert.That(parameters["interval"], Is.EqualTo("5m"));
            Assert.That(parameters["timeZone"], Is.EqualTo("0"));
            Assert.That(parameters["limit"], Is.EqualTo("100"));
            Assert.That(parameters["startTime"], Is.EqualTo(start.ToUnixTimeMilliseconds().ToString()));
            Assert.That(parameters["endTime"], Is.EqualTo(end.AddMilliseconds(-1).ToUnixTimeMilliseconds().ToString()));
            Assert.That(candle.InstrumentId, Is.EqualTo("CRYPTO:BTC-USDT"));
            Assert.That(candle.PriceBasis, Is.EqualTo(CandlePriceBasis.Trades));
            Assert.That(candle.Open, Is.EqualTo(42000.10m));
            Assert.That(candle.High, Is.EqualTo(42100.20m));
            Assert.That(candle.Low, Is.EqualTo(41900.30m));
            Assert.That(candle.Close, Is.EqualTo(42050.40m));
            Assert.That(candle.BaseAssetVolume, Is.EqualTo(12.50m));
            Assert.That(candle.QuoteAssetVolume, Is.EqualTo(525630.75m));
            Assert.That(candle.TradeCount, Is.EqualTo(81));
            Assert.That(candle.TickCount, Is.Null);
            Assert.That(candle.IsComplete, Is.True);
            Assert.That(candle.CloseTime, Is.EqualTo(start.AddMinutes(5)));
        });
    }

    [Test]
    public async Task Oanda_TranslatesSameTimeframeAndRepackagesNamedCandles()
    {
        const string payload = """
            {
              "instrument": "EUR_USD",
              "granularity": "M5",
              "candles": [
                {
                  "complete": true,
                  "volume": 42,
                  "time": "2024-01-01T00:00:00.000000000Z",
                  "mid": {
                    "o": "1.10000",
                    "h": "1.10200",
                    "l": "1.09900",
                    "c": "1.10100"
                  }
                }
              ]
            }
            """;
        var dispatcher = CreateDispatcher(
            HttpStatusCode.OK,
            payload,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["RequestID"] = new[] { "oanda-request-1" }
            });
        var factory = new BrokerFactory(dispatcher);
        IBroker broker = factory.Create(
            CreateOandaConfiguration(),
            [new BrokerInstrumentConfiguration("FX:EUR-USD", "EUR_USD")],
            new BrokerCredentials { AccessToken = "oanda-token" });
        DateTimeOffset start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var query = new CandleQuery(
            "FX:EUR-USD",
            CandleTimeframe.Minutes(5),
            start,
            start.AddHours(1),
            limit: 5);

        BrokerResult<CandleBatch> result = await broker.GetCandlesAsync(query);

        Assert.That(result.IsSuccess, Is.True);
        HttpNetworkRequest request = (HttpNetworkRequest)dispatcher.Requests.Single();
        IReadOnlyDictionary<string, string> parameters = ParseQuery(request.Endpoint);
        CandleBatch batch = result.Value!;
        Candle candle = batch.Candles.Single();
        Assert.Multiple(() =>
        {
            Assert.That(
                request.Endpoint.AbsolutePath,
                Is.EqualTo("/v3/accounts/account-1/instruments/EUR_USD/candles"));
            Assert.That(parameters["granularity"], Is.EqualTo("M5"));
            Assert.That(parameters["price"], Is.EqualTo("M"));
            Assert.That(parameters["alignmentTimezone"], Is.EqualTo("UTC"));
            Assert.That(parameters["dailyAlignment"], Is.EqualTo("0"));
            Assert.That(parameters["weeklyAlignment"], Is.EqualTo("Monday"));
            Assert.That(parameters.ContainsKey("count"), Is.False);
            Assert.That(DateTimeOffset.Parse(parameters["from"]), Is.EqualTo(start));
            Assert.That(DateTimeOffset.Parse(parameters["to"]), Is.EqualTo(start.AddMinutes(25)));
            Assert.That(GetHeader(request, "Authorization"), Is.EqualTo("Bearer oanda-token"));
            Assert.That(GetHeader(request, "Accept-Datetime-Format"), Is.EqualTo("RFC3339"));
            Assert.That(batch.RequestId, Is.EqualTo("oanda-request-1"));
            Assert.That(candle.PriceBasis, Is.EqualTo(CandlePriceBasis.Midpoint));
            Assert.That(candle.Open, Is.EqualTo(1.10000m));
            Assert.That(candle.Close, Is.EqualTo(1.10100m));
            Assert.That(candle.TickCount, Is.EqualTo(42));
            Assert.That(candle.BaseAssetVolume, Is.Null);
            Assert.That(candle.QuoteAssetVolume, Is.Null);
            Assert.That(candle.TradeCount, Is.Null);
            Assert.That(candle.CloseTime, Is.EqualTo(start.AddMinutes(5)));
        });
    }

    [Test]
    public async Task UnsupportedProviderTimeframe_IsStandardFailureWithoutNetworkCall()
    {
        var dispatcher = CreateDispatcher(HttpStatusCode.OK, "{}");
        var factory = new BrokerFactory(dispatcher);
        IBroker broker = factory.Create(
            CreateOandaConfiguration(),
            [new BrokerInstrumentConfiguration("FX:EUR-USD", "EUR_USD")],
            new BrokerCredentials { AccessToken = "token" });

        BrokerResult<CandleBatch> result = await broker.GetCandlesAsync(
            new CandleQuery("FX:EUR-USD", CandleTimeframe.Seconds(1)));

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.Error!.Kind, Is.EqualTo(BrokerErrorKind.Unsupported));
            Assert.That(dispatcher.Requests, Is.Empty);
        });
    }

    [Test]
    public async Task DisabledInstrument_IsRejectedBeforeDispatch()
    {
        var dispatcher = CreateDispatcher(HttpStatusCode.OK, "[]");
        var factory = new BrokerFactory(dispatcher);
        IBroker broker = factory.Create(
            CreateBinanceConfiguration(),
            [new BrokerInstrumentConfiguration("CRYPTO:BTC-USDT", "BTCUSDT", IsEnabled: false)]);

        BrokerResult<CandleBatch> result = await broker.GetCandlesAsync(
            new CandleQuery("CRYPTO:BTC-USDT", CandleTimeframe.Minutes(1)));

        Assert.Multiple(() =>
        {
            Assert.That(result.Error!.Kind, Is.EqualTo(BrokerErrorKind.Validation));
            Assert.That(dispatcher.Requests, Is.Empty);
        });
    }

    [Test]
    public async Task BinanceError_IsRepackagedWithoutLosingProviderCode()
    {
        var dispatcher = CreateDispatcher(
            HttpStatusCode.BadRequest,
            "{\"code\":-1121,\"msg\":\"Invalid symbol.\"}");
        var factory = new BrokerFactory(dispatcher);
        IBroker broker = factory.Create(
            CreateBinanceConfiguration(),
            [new BrokerInstrumentConfiguration("CRYPTO:BTC-USDT", "BAD")]);

        BrokerResult<CandleBatch> result = await broker.GetCandlesAsync(
            new CandleQuery("CRYPTO:BTC-USDT", CandleTimeframe.Minutes(1)));

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.Error!.Kind, Is.EqualTo(BrokerErrorKind.Validation));
            Assert.That(result.Error.ProviderCode, Is.EqualTo("-1121"));
            Assert.That(result.Error.Message, Is.EqualTo("Invalid symbol."));
            Assert.That(result.Error.HttpStatusCode, Is.EqualTo(400));
        });
    }

    [Test]
    public async Task RateLimitResponse_PreservesRetryInformation()
    {
        var dispatcher = CreateDispatcher(
            HttpStatusCode.TooManyRequests,
            "{\"code\":-1003,\"msg\":\"Too many requests.\"}",
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Retry-After"] = new[] { "2" }
            });
        var factory = new BrokerFactory(dispatcher);
        IBroker broker = factory.Create(
            CreateBinanceConfiguration(),
            [new BrokerInstrumentConfiguration("CRYPTO:BTC-USDT", "BTCUSDT")]);

        BrokerResult<CandleBatch> result = await broker.GetCandlesAsync(
            new CandleQuery("CRYPTO:BTC-USDT", CandleTimeframe.Minutes(1)));

        Assert.Multiple(() =>
        {
            Assert.That(result.Error!.Kind, Is.EqualTo(BrokerErrorKind.RateLimited));
            Assert.That(result.Error.IsRetryable, Is.True);
            Assert.That(result.Error.RetryAfter, Is.EqualTo(TimeSpan.FromSeconds(2)));
        });
    }

    [Test]
    public async Task IncompleteCandles_AreExcludedByDefault()
    {
        const string payload = """
            [[1704067200000,"1","2","0.5","1.5","1",4102444799999,"1.5",1,"0","0","0"]]
            """;
        var dispatcher = CreateDispatcher(HttpStatusCode.OK, payload);
        var factory = new BrokerFactory(
            dispatcher,
            timeProvider: new FixedTimeProvider(new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero)));
        IBroker broker = factory.Create(
            CreateBinanceConfiguration(),
            [new BrokerInstrumentConfiguration("CRYPTO:BTC-USDT", "BTCUSDT")]);

        BrokerResult<CandleBatch> result = await broker.GetCandlesAsync(
            new CandleQuery("CRYPTO:BTC-USDT", CandleTimeframe.Minutes(1)));

        Assert.That(result.Value!.Candles, Is.Empty);
    }

    private static StubNetworkDispatcher CreateDispatcher(
        HttpStatusCode statusCode,
        string body,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? headers = null) =>
        new(request =>
        {
            var httpRequest = (HttpNetworkRequest)request;
            return new HttpNetworkResponse(
                NetworkProtocol.Https,
                httpRequest.Endpoint,
                statusCode,
                headers: headers,
                body: Encoding.UTF8.GetBytes(body));
        });

    private static BrokerConfiguration CreateBinanceConfiguration() => new()
    {
        Id = "binance-main",
        Name = "Binance Spot",
        Provider = BrokerProvider.BinanceSpot,
        Environment = BrokerEnvironment.Live,
        RestBaseUrl = "https://unit.binance.test/"
    };

    private static BrokerConfiguration CreateOandaConfiguration() => new()
    {
        Id = "oanda-practice",
        Name = "OANDA Practice",
        Provider = BrokerProvider.OandaV20,
        Environment = BrokerEnvironment.Sandbox,
        RestBaseUrl = "https://unit.oanda.test/",
        AccountId = "account-1"
    };

    private static IReadOnlyDictionary<string, string> ParseQuery(Uri uri) =>
        uri.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                pair => Uri.UnescapeDataString(pair[0]),
                pair => Uri.UnescapeDataString(pair[1]),
                StringComparer.Ordinal);

    private static string? GetHeader(HttpNetworkRequest request, string name) =>
        request.Headers.SingleOrDefault(
            header => string.Equals(header.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;
}
