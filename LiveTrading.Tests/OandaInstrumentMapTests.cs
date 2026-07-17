using Brokers.Models;
using LiveTrading.Oanda;
using NUnit.Framework;

namespace LiveTrading.Tests;

[TestFixture]
public sealed class OandaInstrumentMapTests
{
    [TestCase("FX:EUR/USD", "EUR_USD")]
    [TestCase("FX:GBP/JPY", "GBP_JPY")]
    [TestCase("FX:USD-CAD", "USD_CAD")]
    public void ToNative_FallsBackToAssetClassStrippingAndSeparatorReplacement(string canonical, string expectedNative)
    {
        var map = new OandaInstrumentMap(new Dictionary<string, string>());
        Assert.That(map.ToNative(new InstrumentKey(canonical)), Is.EqualTo(expectedNative));
    }

    [Test]
    public void ToNative_PrefersExplicitMapping()
    {
        var map = new OandaInstrumentMap(new Dictionary<string, string>
        {
            ["FX:EUR/USD"] = "EUR_USD_CUSTOM"
        });
        Assert.That(map.ToNative(new InstrumentKey("FX:EUR/USD")), Is.EqualTo("EUR_USD_CUSTOM"));
    }

    [Test]
    public void FromNative_ReversesAnExplicitMapping()
    {
        var map = new OandaInstrumentMap(new Dictionary<string, string>
        {
            ["FX:EUR/USD"] = "EUR_USD"
        });
        Assert.That(map.FromNative("EUR_USD"), Is.EqualTo(new InstrumentKey("FX:EUR/USD")));
        Assert.That(map.FromNative("eur_usd"), Is.EqualTo(new InstrumentKey("FX:EUR/USD")),
            "The reverse lookup must be case-insensitive, matching the real InstrumentMappers.FromNative.");
    }

    [Test]
    public void FromNative_WithoutAnyMapping_FallsBackToWrappingTheNativeStringVerbatim()
    {
        var map = new OandaInstrumentMap(new Dictionary<string, string>());
        // Mirrors the real (internal) Brokers.Infrastructure.InstrumentMappers.FromNative's own
        // fallback exactly - it does not reconstruct a canonical "FX:" form either.
        Assert.That(map.FromNative("EUR_USD"), Is.EqualTo(new InstrumentKey("EUR_USD")));
    }

    [Test]
    public void ToNative_ThenFromNative_RoundTripsThroughAnExplicitMapping()
    {
        var map = new OandaInstrumentMap(new Dictionary<string, string>
        {
            ["FX:EUR/USD"] = "EUR_USD",
            ["FX:GBP/USD"] = "GBP_USD"
        });

        foreach (string canonical in new[] { "FX:EUR/USD", "FX:GBP/USD" })
        {
            InstrumentKey instrument = new(canonical);
            string native = map.ToNative(instrument);
            Assert.That(map.FromNative(native), Is.EqualTo(instrument));
        }
    }
}
