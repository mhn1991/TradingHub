using System.Text;
using System.Text.Json;
using System.Net;
using Brokers.Models;
using ChartAnnotator.Engine;
using Dashboard.Live;
using Brokers.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace TradingHub.UnitTests;

[TestFixture]
public sealed class LiveFeedTests
{
    private static readonly InstrumentKey Instrument = new("CRYPTO:BTC/USDT");
    private static readonly BarInterval OneMinute = BarInterval.Minutes(1);

    [Test]
    public void ValidOptions_MapToDomainIdentity()
    {
        var options = new LiveFeedOptions();

        (InstrumentKey instrument, BarInterval interval) = options.Validate();

        Assert.Multiple(() =>
        {
            Assert.That(instrument, Is.EqualTo(Instrument));
            Assert.That(interval, Is.EqualTo(OneMinute));
        });
    }

    [TestCase("BTC-USDT")]
    [TestCase("A")]
    [TestCase("BTC USDT")]
    public void InvalidSymbols_AreRejected(string symbol)
    {
        var options = new LiveFeedOptions { Symbol = symbol };

        Assert.That(options.Validate, Throws.ArgumentException);
    }

    [TestCase("0m")]
    [TestCase("10m")]
    [TestCase("1M ")]
    public void UnsupportedIntervals_AreRejected(string interval)
    {
        var options = new LiveFeedOptions { Interval = interval };

        Assert.That(options.Validate, Throws.ArgumentException);
    }

    [Test]
    public void DisabledOandaWorkspace_DoesNotRequireCredentials()
    {
        var options = new OandaWorkspaceOptions { Enabled = false };

        Assert.Multiple(() =>
        {
            Assert.That(options.Validate, Throws.Nothing);
            Assert.That(options.IsConfigured, Is.False);
        });
    }

    [Test]
    public void EnabledOandaWorkspace_RequiresBackendCredentials()
    {
        var options = new OandaWorkspaceOptions { Enabled = true };

        Assert.That(options.Validate, Throws.ArgumentException);
    }

    [Test]
    public void OandaOrderPolicy_CannotBeEnabledForALiveAccount()
    {
        var options = new OandaWorkspaceOptions
        {
            Enabled = true,
            Environment = BrokerEnvironment.Live,
            AccountId = "account",
            AccessToken = "token",
            AllowDemoOrders = true
        };

        Assert.That(options.Validate, Throws.InvalidOperationException);
    }

    [Test]
    public async Task OandaWorkspace_StopsCleanlyDuringReconnectBackoff()
    {
        using ILoggerFactory loggerFactory = LoggerFactory.Create(_ => { });
        var service = new OandaWorkspaceService(
            Options.Create(new OandaWorkspaceOptions
            {
                Enabled = true,
                Environment = BrokerEnvironment.Demo,
                AccountId = "account",
                AccessToken = "token",
                RestBaseAddress = new Uri("https://127.0.0.1:1/"),
                StreamBaseAddress = new Uri("https://127.0.0.1:1/")
            }),
            TimeProvider.System,
            loggerFactory,
            NullLogger<OandaWorkspaceService>.Instance);
        await service.StartAsync(CancellationToken.None);
        await Task.Delay(100);

        Assert.That(
            async () => await service.StopAsync(CancellationToken.None),
            Throws.Nothing);
    }

    [Test]
    public void WorkspaceAssetDiscovery_KeepsTradingSpotPairsAndSortsCommonQuotesFirst()
    {
        byte[] json = Encoding.UTF8.GetBytes(
            """
            {
              "symbols": [
                {"symbol":"ETHBTC","status":"TRADING","baseAsset":"ETH","quoteAsset":"BTC","isSpotTradingAllowed":true},
                {"symbol":"BTCUSDT","status":"TRADING","baseAsset":"BTC","quoteAsset":"USDT","isSpotTradingAllowed":true},
                {"symbol":"PAUSEDUSDT","status":"HALT","baseAsset":"PAUSED","quoteAsset":"USDT","isSpotTradingAllowed":true},
                {"symbol":"MARGINUSDT","status":"TRADING","baseAsset":"MARGIN","quoteAsset":"USDT","isSpotTradingAllowed":false},
                {"symbol":"BAD-PAIR","status":"TRADING","baseAsset":"BAD","quoteAsset":"PAIR","isSpotTradingAllowed":true}
              ]
            }
            """);

        IReadOnlyList<WorkspaceAsset> assets = BinanceWorkspaceMarketData.ParseAssets(json);

        Assert.Multiple(() =>
        {
            Assert.That(assets.Select(asset => asset.Symbol), Is.EqualTo(new[] { "BTCUSDT", "ETHBTC" }));
            Assert.That(assets[0].DisplayName, Is.EqualTo("BTC / USDT"));
            Assert.That(assets[0].Instrument, Is.EqualTo("CRYPTO:BTC/USDT"));
            Assert.That(assets[0].Timeframes, Does.Contain("1m").And.Contain("1d"));
        });
    }

    [TestCase("{}")]
    [TestCase("{\"symbols\":[]}")]
    [TestCase("{\"symbols\":{}}")]
    public void WorkspaceAssetDiscovery_RejectsMalformedOrEmptyResponses(string json)
    {
        Assert.That(
            () => BinanceWorkspaceMarketData.ParseAssets(Encoding.UTF8.GetBytes(json)),
            Throws.TypeOf<JsonException>());
    }

    [Test]
    public async Task BoundedHttpContent_RejectsAnOversizedUnknownLengthResponse()
    {
        using var content = new StreamContent(new MemoryStream(new byte[11]));
        content.Headers.ContentLength = null;

        Assert.That(
            async () => await BoundedHttpContent.ReadAsync(content, 10, CancellationToken.None),
            Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public async Task BoundedHttpContent_ReadsAResponseAtTheExactLimit()
    {
        byte[] expected = Enumerable.Range(0, 10).Select(value => (byte)value).ToArray();
        using var content = new StreamContent(new MemoryStream(expected));
        content.Headers.ContentLength = null;

        byte[] actual = await BoundedHttpContent.ReadAsync(content, expected.Length, CancellationToken.None);

        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public async Task WorkspaceCatalog_ReportsEnvironmentAndConfigurationHonestly()
    {
        using var client = new HttpClient(new StubHttpMessageHandler(request =>
            JsonResponse(request.RequestUri!.AbsolutePath.EndsWith("exchangeInfo", StringComparison.Ordinal)
                ? ExchangeInfoJson()
                : throw new AssertionException($"Unexpected request {request.RequestUri}"))))
        {
            BaseAddress = new Uri("https://data-api.binance.vision/")
        };
        var service = new BinanceWorkspaceMarketData(
            client,
            TimeProvider.System,
            Options.Create(new LiveFeedOptions()));

        WorkspaceCatalog catalog = await service.GetCatalogAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(catalog.StreamingSelection.Symbol, Is.EqualTo("BTCUSDT"));
            Assert.That(catalog.Brokers.Single(broker => broker.Id == "simulator").Environment,
                Is.EqualTo(WorkspaceEnvironment.Demo));
            Assert.That(catalog.Brokers.Single(broker => broker.Id == "binance").Environment,
                Is.EqualTo(WorkspaceEnvironment.Live));
            Assert.That(catalog.Brokers.Single(broker => broker.Id == "binance").IsReadOnly, Is.True);
            Assert.That(catalog.Brokers.Single(broker => broker.Id == "oanda").IsConfigured, Is.False);
            Assert.That(catalog.Brokers.Single(broker => broker.Id == "ig").Assets, Is.Empty);
        });
    }

    [Test]
    public async Task WorkspaceCatalog_UsesAnExplicitBoundedFallbackWhenDiscoveryIsOffline()
    {
        using var client = new HttpClient(new StubHttpMessageHandler(_ =>
            throw new HttpRequestException("offline")))
        {
            BaseAddress = new Uri("https://data-api.binance.vision/")
        };
        var service = new BinanceWorkspaceMarketData(
            client,
            TimeProvider.System,
            Options.Create(new LiveFeedOptions()));

        WorkspaceCatalog catalog = await service.GetCatalogAsync(CancellationToken.None);
        WorkspaceBroker binance = catalog.Brokers.Single(broker => broker.Id == "binance");

        Assert.Multiple(() =>
        {
            Assert.That(binance.Assets.Select(asset => asset.Symbol),
                Is.EqualTo(new[] { "BTCUSDT", "ETHUSDT", "BNBUSDT", "SOLUSDT", "XRPUSDT" }));
            Assert.That(catalog.Warning, Does.Contain("temporarily unavailable"));
        });
    }

    [Test]
    public async Task WorkspaceSnapshot_UsesSelectedPairAndTimeframeClosedCandlesAndCache()
    {
        int exchangeRequests = 0;
        int klineRequests = 0;
        using var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("exchangeInfo", StringComparison.Ordinal))
            {
                exchangeRequests++;
                return JsonResponse(ExchangeInfoJson());
            }

            if (request.RequestUri.AbsolutePath.EndsWith("klines", StringComparison.Ordinal))
            {
                klineRequests++;
                Assert.That(request.RequestUri.Query, Does.Contain("symbol=ETHUSDT"));
                Assert.That(request.RequestUri.Query, Does.Contain("interval=5m"));
                return JsonResponse(KlineJson(30, TimeSpan.FromMinutes(5)));
            }

            throw new AssertionException($"Unexpected request {request.RequestUri}");
        }))
        {
            BaseAddress = new Uri("https://data-api.binance.vision/")
        };
        var service = new BinanceWorkspaceMarketData(
            client,
            TimeProvider.System,
            Options.Create(new LiveFeedOptions()));

        WorkspaceSnapshot first = await service.GetSnapshotAsync(
            "ethusdt",
            "5m",
            CancellationToken.None);
        WorkspaceSnapshot cached = await service.GetSnapshotAsync(
            "ETHUSDT",
            "5m",
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.SameAs(cached));
            Assert.That(first.Status.State, Is.EqualTo(LiveConnectionState.Connected));
            Assert.That(first.Status.Symbol, Is.EqualTo("ETHUSDT"));
            Assert.That(first.Dataset.Instrument, Is.EqualTo("CRYPTO:ETH/USDT"));
            Assert.That(first.Dataset.Series.Single().Interval, Is.EqualTo("5m"));
            Assert.That(first.Dataset.Series.Single().Frames, Has.Count.EqualTo(30));
            Assert.That(first.Dataset.Series.Single().Frames[^1].Indicators.Rsi, Is.Not.Null);
            Assert.That(first.Dataset.Series.Single().Frames[^1].Indicators.BollingerMiddle, Is.Not.Null);
            Assert.That(exchangeRequests, Is.EqualTo(1));
            Assert.That(klineRequests, Is.EqualTo(1));
        });
    }

    [TestCase("BTC-USDT", "1m")]
    [TestCase("UNKNOWN", "1m")]
    [TestCase("BTCUSDT", "2m")]
    public void WorkspaceSnapshot_RejectsInvalidSelections(string symbol, string interval)
    {
        using var client = new HttpClient(new StubHttpMessageHandler(_ =>
            JsonResponse(ExchangeInfoJson())))
        {
            BaseAddress = new Uri("https://data-api.binance.vision/")
        };
        var service = new BinanceWorkspaceMarketData(
            client,
            TimeProvider.System,
            Options.Create(new LiveFeedOptions()));

        Assert.That(
            async () => await service.GetSnapshotAsync(symbol, interval, CancellationToken.None),
            Throws.InstanceOf<Exception>().And.Property("Message").Not.Empty);
    }

    [Test]
    public void ClosedWebSocketKline_MapsExactOhlcvAndIdentity()
    {
        BinanceStreamMessage message = ParseStream(
            """
            {
              "e":"kline","E":1672515840001,"s":"BTCUSDT",
              "k":{"t":1672515780000,"T":1672515839999,"s":"BTCUSDT","i":"1m",
                   "o":"100.5","c":"101.25","h":"102","l":"99.75","v":"12.5","x":true}
            }
            """);

        Assert.That(message.Kind, Is.EqualTo(BinanceStreamMessageKind.Kline));
        Assert.That(message.Candle, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(message.Candle!.Instrument, Is.EqualTo(Instrument));
            Assert.That(message.Candle.Interval, Is.EqualTo(OneMinute));
            Assert.That(message.Candle.IsComplete, Is.True);
            Assert.That(message.Candle.OpenTime.ToUnixTimeMilliseconds(), Is.EqualTo(1672515780000));
            Assert.That(message.Candle.CloseTime!.Value.ToUnixTimeMilliseconds(), Is.EqualTo(1672515839999));
            Assert.That(message.Candle.Prices, Is.EqualTo(new Ohlc(100.5m, 102m, 99.75m, 101.25m)));
            Assert.That(message.Candle.Volume, Is.EqualTo(new MarketVolume(12.5m, VolumeKind.BaseAssetQuantity)));
        });
    }

    [Test]
    public void FormingWebSocketKline_IsNotMarkedComplete()
    {
        BinanceStreamMessage message = ParseStream(
            """
            {"e":"kline","s":"BTCUSDT","k":{"t":1672515780000,"T":1672515839999,
            "s":"BTCUSDT","i":"1m","o":"100","c":"101","h":"102","l":"99","v":"5","x":false}}
            """);

        Assert.That(message.Candle!.IsComplete, Is.False);
    }

    [Test]
    public void ServerShutdown_IsRecognizedWithoutKlinePayload()
    {
        BinanceStreamMessage message = ParseStream("""{"e":"serverShutdown","E":1770123456789}""");

        Assert.Multiple(() =>
        {
            Assert.That(message.Kind, Is.EqualTo(BinanceStreamMessageKind.ServerShutdown));
            Assert.That(message.Candle, Is.Null);
        });
    }

    [TestCase("ETHUSDT", "1m")]
    [TestCase("BTCUSDT", "5m")]
    public void UnexpectedStreamIdentity_IsRejected(string symbol, string interval)
    {
        string json = """
            {"e":"kline","s":"__SYMBOL__","k":{"t":1672515780000,"T":1672515839999,
            "s":"__SYMBOL__","i":"__INTERVAL__","o":"100","c":"101","h":"102","l":"99","v":"5","x":true}}
            """
            .Replace("__SYMBOL__", symbol, StringComparison.Ordinal)
            .Replace("__INTERVAL__", interval, StringComparison.Ordinal);

        Assert.That(() => ParseStream(json), Throws.TypeOf<JsonException>());
    }

    [Test]
    public void InconsistentWebSocketOhlc_IsRejected()
    {
        const string json = """
            {"e":"kline","s":"BTCUSDT","k":{"t":1672515780000,"T":1672515839999,
            "s":"BTCUSDT","i":"1m","o":"100","c":"101","h":"100.5","l":"99","v":"5","x":true}}
            """;

        Assert.That(() => ParseStream(json), Throws.TypeOf<JsonException>());
    }

    [Test]
    public void RestKlines_AreSortedAndOnlyPastCloseTimesAreComplete()
    {
        byte[] json = Encoding.UTF8.GetBytes(
            """
            [
              [1672515840000,"101","103","100","102","8",1672515899999],
              [1672515780000,"100","102","99","101","7",1672515839999]
            ]
            """);
        DateTimeOffset now = DateTimeOffset.FromUnixTimeMilliseconds(1672515850000);

        IReadOnlyList<Candle> candles = BinanceKlineParser.ParseRest(
            json,
            Instrument,
            OneMinute,
            now);

        Assert.Multiple(() =>
        {
            Assert.That(candles.Select(candle => candle.OpenTime.ToUnixTimeMilliseconds()),
                Is.EqualTo(new long[] { 1672515780000, 1672515840000 }));
            Assert.That(candles[0].IsComplete, Is.True);
            Assert.That(candles[1].IsComplete, Is.False);
        });
    }

    [Test]
    public void ClosedCandleCursor_DeduplicatesAndDetectsGaps()
    {
        var cursor = new ClosedCandleCursor(OneMinute);
        Candle first = CandleAt(0, complete: true);
        Candle duplicate = CandleAt(0, complete: true);
        Candle contiguous = CandleAt(1, complete: true);
        Candle gap = CandleAt(3, complete: true);

        Assert.That(cursor.Classify(first), Is.EqualTo(CandleDisposition.New));
        cursor.Commit(first);
        Assert.That(cursor.Classify(duplicate), Is.EqualTo(CandleDisposition.DuplicateOrOld));
        Assert.That(cursor.Classify(contiguous), Is.EqualTo(CandleDisposition.New));
        cursor.Commit(contiguous);
        Assert.That(cursor.Classify(gap), Is.EqualTo(CandleDisposition.Gap));
    }

    [Test]
    public async Task LiveAnalysis_UsesOnlyClosedCandlesWarmsIndicatorsAndBoundsFrames()
    {
        var state = new LiveAnalysisState(
            Instrument,
            OneMinute,
            new ChartAnnotationOptions
            {
                AtrPeriod = 3,
                RsiPeriod = 3,
                BollingerPeriod = 4,
                HeavyAnalysisEveryCandles = 1
            },
            frameCapacity: 5);

        Assert.That(await state.ProcessAsync(CandleAt(0, complete: false), CancellationToken.None), Is.Null);
        for (int index = 0; index < 10; index++)
        {
            Assert.That(
                await state.ProcessAsync(CandleAt(index, complete: true), CancellationToken.None),
                Is.Not.Null);
        }

        Assert.That(
            await state.ProcessAsync(CandleAt(9, complete: true), CancellationToken.None),
            Is.Null,
            "A replayed closed candle must not be annotated twice.");
        IReadOnlyList<Dashboard.Contracts.ReplayFrame> frames = state.Snapshot();
        Assert.Multiple(() =>
        {
            Assert.That(frames, Has.Count.EqualTo(5));
            Assert.That(frames[0].Index, Is.EqualTo(5));
            Assert.That(frames[^1].Index, Is.EqualTo(9));
            Assert.That(frames[^1].Indicators.Rsi, Is.Not.Null);
            Assert.That(frames[^1].Indicators.BollingerMiddle, Is.Not.Null);
            Assert.That(state.GapsDetected, Is.Zero);
            Assert.That(state.LastAvailableAt, Is.EqualTo(frames[^1].AvailableAt));
        });
    }

    [Test]
    public async Task OandaAnalysis_DoesNotReportTheExpectedWeekendClosureAsCorruptData()
    {
        var state = new LiveAnalysisState(
            new InstrumentKey("FX:EUR/USD"),
            OneMinute,
            new ChartAnnotationOptions(),
            frameCapacity: 50,
            WorkspaceAnalysis.IsExpectedForexClosure);
        await state.ProcessAsync(
            CandleAtTime(new DateTimeOffset(2026, 7, 10, 21, 59, 0, TimeSpan.Zero)),
            CancellationToken.None);
        await state.ProcessAsync(
            CandleAtTime(new DateTimeOffset(2026, 7, 12, 22, 0, 0, TimeSpan.Zero)),
            CancellationToken.None);

        Assert.That(state.GapsDetected, Is.Zero);
    }

    [Test]
    public void ReconnectDelay_IsExponentialJitteredAndCapped()
    {
        TimeSpan firstLow = ReconnectDelay.Calculate(1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), 0d);
        TimeSpan firstHigh = ReconnectDelay.Calculate(1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), 1d);
        TimeSpan fourth = ReconnectDelay.Calculate(4, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), 0.5d);
        TimeSpan capped = ReconnectDelay.Calculate(20, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), 1d);

        Assert.Multiple(() =>
        {
            Assert.That(firstLow, Is.EqualTo(TimeSpan.FromMilliseconds(800)));
            Assert.That(firstHigh, Is.EqualTo(TimeSpan.FromMilliseconds(1_200)));
            Assert.That(fourth, Is.EqualTo(TimeSpan.FromSeconds(8)));
            Assert.That(capped, Is.EqualTo(TimeSpan.FromSeconds(30)));
        });
    }

    [Test]
    public void ReconnectDelay_AlwaysHonoursServerRetryAfter()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                ReconnectDelay.RespectRetryAfter(
                    TimeSpan.FromSeconds(4),
                    TimeSpan.FromSeconds(45)),
                Is.EqualTo(TimeSpan.FromSeconds(45)));
            Assert.That(
                ReconnectDelay.RespectRetryAfter(
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromSeconds(2)),
                Is.EqualTo(TimeSpan.FromSeconds(30)));
        });
    }

    private static BinanceStreamMessage ParseStream(string json) =>
        BinanceKlineParser.ParseStream(
            Encoding.UTF8.GetBytes(json),
            "BTCUSDT",
            "1m",
            Instrument,
            OneMinute);

    private static Candle CandleAt(int minute, bool complete)
    {
        DateTimeOffset openTime = new(2026, 1, 1, 0, minute, 0, TimeSpan.Zero);
        decimal open = 100m + minute;
        decimal close = open + (minute % 2 == 0 ? 1m : -0.5m);
        return new Candle
        {
            Instrument = Instrument,
            Interval = OneMinute,
            OpenTime = openTime,
            CloseTime = openTime.AddMinutes(1).AddMilliseconds(-1),
            Prices = new Ohlc(open, Math.Max(open, close) + 1m, Math.Min(open, close) - 1m, close),
            Volume = new MarketVolume(10m + minute, VolumeKind.BaseAssetQuantity),
            IsComplete = complete
        };
    }

    private static Candle CandleAtTime(DateTimeOffset openTime) => new()
    {
        Instrument = new InstrumentKey("FX:EUR/USD"),
        Interval = OneMinute,
        OpenTime = openTime,
        CloseTime = openTime.AddMinutes(1),
        Prices = new Ohlc(1.1m, 1.11m, 1.09m, 1.105m),
        Volume = new MarketVolume(10m, VolumeKind.TickCount),
        IsComplete = true
    };

    private static string ExchangeInfoJson() =>
        """
        {"symbols":[
          {"symbol":"BTCUSDT","status":"TRADING","baseAsset":"BTC","quoteAsset":"USDT","isSpotTradingAllowed":true},
          {"symbol":"ETHUSDT","status":"TRADING","baseAsset":"ETH","quoteAsset":"USDT","isSpotTradingAllowed":true}
        ]}
        """;

    private static string KlineJson(int count, TimeSpan interval)
    {
        DateTimeOffset start = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        object[][] rows = Enumerable.Range(0, count).Select(index =>
        {
            DateTimeOffset openTime = start + interval * index;
            decimal open = 1_000m + index;
            decimal close = open + (index % 2 == 0 ? 2m : -1m);
            return new object[]
            {
                openTime.ToUnixTimeMilliseconds(),
                open.ToString(System.Globalization.CultureInfo.InvariantCulture),
                (Math.Max(open, close) + 1m).ToString(System.Globalization.CultureInfo.InvariantCulture),
                (Math.Min(open, close) - 1m).ToString(System.Globalization.CultureInfo.InvariantCulture),
                close.ToString(System.Globalization.CultureInfo.InvariantCulture),
                (100m + index).ToString(System.Globalization.CultureInfo.InvariantCulture),
                (openTime + interval - TimeSpan.FromMilliseconds(1)).ToUnixTimeMilliseconds()
            };
        }).ToArray();
        return JsonSerializer.Serialize(rows);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(handle(request));
    }
}
