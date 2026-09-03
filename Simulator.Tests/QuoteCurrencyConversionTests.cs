using Brokers.Models;
using Simulator.Models;

namespace Simulator.Tests;

/// <summary>
/// Proves that profit and loss on a pair quoted in a currency other than the account's is converted
/// to the account currency, at the rate supplied by <c>--quote-rate</c>.
/// <para>
/// 3.48 axis 7 originally claimed no conversion happened, on the evidence that
/// <c>grossProfitLoss / ((exit - entry) * quantity)</c> was exactly 1.00000. That measurement could
/// not distinguish "no conversion" from "conversion by a rate of exactly 1", and the rate was 1 in
/// every run sampled because the account currency was derived from the instrument. These tests pin
/// the real behaviour with a rate that is *not* 1, so the ambiguity cannot recur.
/// </para>
/// </summary>
[TestFixture]
public sealed class QuoteCurrencyConversionTests
{
    private static readonly InstrumentKey GbpJpy = new("FX:GBP/JPY");
    private const decimal JpyToUsd = 0.006322m;

    [Test]
    public async Task UnrealisedProfitOnAYenPair_IsReportedInTheAccountCurrency()
    {
        await using var harness = new SimulationTestHarness(UsdAccountWithJpyRate());

        await harness.ProcessAsync(0, 158m, 158m, 158m, 158m, GbpJpy);
        await harness.PlaceAsync(OrderSide.Buy, quantity: 1_000m, instrument: GbpJpy);
        await harness.ProcessAsync(1, 158m, 158m, 158m, 158m, GbpJpy);

        // One yen of favourable movement on 1,000 units = 1,000 JPY.
        await harness.ProcessAsync(2, 159m, 159m, 159m, 159m, GbpJpy);

        BrokerPosition position = harness.Broker.State.GetPositions()
            .Single(item => item.Instrument == GbpJpy);

        Assert.Multiple(() =>
        {
            Assert.That(position.Quantity, Is.EqualTo(1_000m));
            Assert.That(
                position.UnrealizedProfitLoss,
                Is.EqualTo(1_000m * JpyToUsd).Within(0.01m),
                "1,000 JPY of profit must be reported as about 6.32 USD, not as 1,000");
        });
    }

    [Test]
    public void ConfiguredRate_IsUsedForAYenPairOnAUsdAccount()
    {
        var state = new Simulator.Broker.SimulatedBrokerState(UsdAccountWithJpyRate());

        Assert.That(state.GetQuoteToBaseCurrencyRate(GbpJpy), Is.EqualTo(JpyToUsd));
    }

    [Test]
    public void WithoutARate_TheYenPairIsRefusedRatherThanSilentlyTreatedAsOneToOne()
    {
        // The failure that made --quote-rate necessary: before, the account currency was derived
        // from the instrument so this resolved to 1 and the run silently produced yen results.
        var state = new Simulator.Broker.SimulatedBrokerState(new SimulationOptions
        {
            BaseCurrency = "USD",
            CommissionRate = 0m,
            SpreadBasisPoints = 0m,
            SlippageBasisPoints = 0m
        });

        Assert.That(
            () => state.GetQuoteToBaseCurrencyRate(GbpJpy),
            Throws.InvalidOperationException.With.Message.Contains("JPY"));
    }

    [Test]
    public void AnAccountInTheQuoteCurrency_StillConvertsAtOne()
    {
        var state = new Simulator.Broker.SimulatedBrokerState(new SimulationOptions
        {
            BaseCurrency = "JPY",
            CommissionRate = 0m
        });

        Assert.That(state.GetQuoteToBaseCurrencyRate(GbpJpy), Is.EqualTo(1m));
    }

    private static SimulationOptions UsdAccountWithJpyRate() => new()
    {
        BaseCurrency = "USD",
        StartingBalance = 100_000m,
        Leverage = 20m,
        CommissionRate = 0m,
        SpreadBasisPoints = 0m,
        SlippageBasisPoints = 0m,
        QuoteToBaseCurrencyRates = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            ["JPY"] = JpyToUsd
        }
    };
}
