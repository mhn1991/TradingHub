using Brokers.Models;
using Simulator.Models;

namespace Simulator.Tests;

/// <summary>
/// The account currency used to be derived from the instrument's quote currency, so a GBP/JPY run
/// banked a yen account while its USD-quoted siblings banked dollars and nothing in the output said
/// so - the mechanism behind 3.43's overstated -15,505 (3.48 axis 7). The default is now USD, and a
/// cross whose quote currency cannot reach the account currency is refused up front rather than
/// throwing at the first fill.
/// </summary>
[TestFixture]
public sealed class BaseCurrencyDefaultTests
{
    [Test]
    public void DefaultIsUsd_NotTheInstrumentQuoteCurrency()
    {
        Assert.That(BacktestRequest.DefaultBaseCurrency, Is.EqualTo("USD"));
    }

    [Test]
    public void CrossWithoutARate_IsRejectedUpFront()
    {
        // GBP/JPY on a USD account: quote is not USD, base is not USD, no rate supplied.
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => Request("FX:GBP/JPY").Validate())!;

        Assert.Multiple(() =>
        {
            Assert.That(error.Message, Does.Contain("FX:GBP/JPY"));
            Assert.That(error.Message, Does.Contain("--quote-rate"));
            Assert.That(error.Message, Does.Contain("--base-currency"));
        });
    }

    [Test]
    public void CrossWithAnExplicitRate_IsAccepted()
    {
        Assert.DoesNotThrow(() => (Request("FX:GBP/JPY") with
        {
            QuoteToBaseCurrencyRates = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
            {
                ["JPY"] = 0.0067m
            }
        }).Validate());
    }

    [Test]
    public void CrossDenominatedInItsOwnQuoteCurrency_IsAccepted()
    {
        // The old behaviour remains available, but now only when asked for explicitly.
        Assert.DoesNotThrow(() => (Request("FX:GBP/JPY") with { BaseCurrency = "JPY" }).Validate());
    }

    [TestCase("FX:EUR/USD", TestName = "QuoteIsTheAccountCurrency")]
    [TestCase("METAL:XAU/USD", TestName = "MetalQuotedInTheAccountCurrency")]
    [TestCase("FX:USD/JPY", TestName = "BaseIsTheAccountCurrency_DerivableFromItsOwnPrice")]
    public void ConvertibleInstruments_AreAccepted(string instrument)
    {
        Assert.DoesNotThrow(() => Request(instrument).Validate());
    }

    private static BacktestRequest Request(string instrument) => new()
    {
        Instrument = new InstrumentKey(instrument),
        From = DateTimeOffset.UnixEpoch,
        To = DateTimeOffset.UnixEpoch.AddDays(30),
        Strategies = ["alfonso"]
    };
}
