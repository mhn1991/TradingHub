using Brokers.Abstractions;
using Dashboard.Live;
using NUnit.Framework;

namespace Simulator.Tests;

/// <summary>
/// Covers <see cref="OandaWorkspaceOptions.FallbackInstruments"/> - the configured seed used when
/// account instrument discovery fails and no cached list exists (a restart during a broker
/// outage). Discovery is account-scoped but candles are not, so this supplies the symbol names
/// that keep charts usable while the account API is down.
/// </summary>
[TestFixture]
public sealed class OandaFallbackInstrumentTests
{
    private static OandaWorkspaceOptions Options(params string[] fallback) => new()
    {
        Enabled = true,
        Environment = BrokerEnvironment.Demo,
        AccountId = "101-004-1234567-001",
        AccessToken = "token",
        FallbackInstruments = fallback
    };

    [Test]
    public void DefaultsToEmpty_SoNothingChangesUnlessConfigured()
    {
        Assert.That(new OandaWorkspaceOptions().FallbackInstruments, Is.Empty);
    }

    [Test]
    public void ValidatesAWellFormedList()
    {
        Assert.DoesNotThrow(() => Options("XAU_USD", "EUR_USD").Validate());
    }

    [TestCase("XAUUSD", TestName = "missing the underscore separator")]
    [TestCase("", TestName = "empty symbol")]
    [TestCase("   ", TestName = "whitespace symbol")]
    public void RejectsMalformedSymbolsAtStartup(string symbol)
    {
        // Caught at startup rather than at first use: a typo would otherwise surface much later
        // as a failed candle request for a symbol nobody realised was misspelled.
        Assert.That(
            () => Options(symbol).Validate(),
            Throws.InvalidOperationException.With.Message.Contains("not an OANDA symbol"));
    }

    [Test]
    public void ValidationIsSkippedWhenTheWorkspaceIsDisabled()
    {
        var disabled = new OandaWorkspaceOptions { Enabled = false, FallbackInstruments = ["nonsense"] };

        Assert.DoesNotThrow(() => disabled.Validate());
    }
}
