using System.Net;
using System.Text.Json;
using Brokers.Abstractions;
using Brokers.Binance;
using Brokers.Exceptions;
using Brokers.Infrastructure;
using Brokers.Models;
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

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
