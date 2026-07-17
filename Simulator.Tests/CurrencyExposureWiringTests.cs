using Agent.Models;
using Brokers.Models;
using ChartAnnotator.MarketData;
using PortfolioManager.Risk;
using RiskManager;
using RiskManager.Safety;
using Simulator.Engine;
using Simulator.Models;

namespace Simulator.Tests;

/// <summary>
/// Proves the shared-runtime currency-exposure wiring described in the audit (§8/§26.5)
/// is real notional net/gross exposure via CurrencyExposureCalculator, not the old
/// 50/50 stop-risk split, and that it actually gates admission. Exercised directly
/// against SharedPortfolioRuntime.EvaluateCurrencyExposureLimits since today's engine
/// only streams one instrument per run - full production multi-instrument coverage
/// needs the multi-instrument portfolio clock.
/// </summary>
[TestFixture]
public sealed class CurrencyExposureWiringTests
{
    private static readonly InstrumentKey GbpUsd = new("FX:GBP/USD");
    private static readonly InstrumentKey GbpJpy = new("FX:GBP/JPY");
    private static readonly InstrumentKey AudChf = new("FX:AUD/CHF");
    private static readonly DateTimeOffset Time = new(2026, 1, 5, 10, 0, 0, TimeSpan.Zero);

    [Test]
    public void SameDirectionAddition_BreachesNetLimit_IsRejected()
    {
        SharedPortfolioRuntime runtime = CreateRuntime(new PortfolioRiskOptions
        {
            MaximumNetCurrencyExposurePercent = 60m,
            MaximumGrossCurrencyExposurePercent = 600m
        });
        var lots = new[] { GbpUsdLot(quantity: 30_000m, price: 1.30m) };

        // Existing long GBP/USD ~39% of equity net GBP. Adding another 20,000-unit
        // long pushes combined GBP native to 50,000 -> ~65% of equity, above the 60% cap.
        var decision = new AgentDecision
        {
            Action = AgentAction.Buy,
            Instrument = GbpUsd,
            Confidence = 70m,
            CreatedAt = Time,
            Reason = "test"
        };

        (bool approved, string? reasonCode, _) = runtime.EvaluateCurrencyExposureLimits(
            Time, decision, quantity: 20_000m, referencePrice: 1.30m, quoteRate: 1m, lots, equity: 100_000m);

        Assert.Multiple(() =>
        {
            Assert.That(approved, Is.False);
            Assert.That(reasonCode, Is.EqualTo("NetCurrencyExposureLimit"));
        });
    }

    [Test]
    public void OppositeDirectionOnSamePair_NetsDown_IsApprovedDespiteLargeGross()
    {
        SharedPortfolioRuntime runtime = CreateRuntime(new PortfolioRiskOptions
        {
            MaximumNetCurrencyExposurePercent = 20m,
            MaximumGrossCurrencyExposurePercent = 200m
        });
        var lots = new[] { GbpUsdLot(quantity: 40_000m, price: 1.30m) };

        // A same-size SHORT candidate on the same pair fully cancels net GBP/USD
        // exposure even though combined gross exposure (both legs' notional) stays
        // large - proving net and gross are genuinely different computations, not
        // the same number reported twice.
        var decision = new AgentDecision
        {
            Action = AgentAction.Sell,
            Instrument = GbpUsd,
            Confidence = 70m,
            CreatedAt = Time,
            Reason = "test"
        };

        (bool approved, string? reasonCode, _) = runtime.EvaluateCurrencyExposureLimits(
            Time, decision, quantity: 40_000m, referencePrice: 1.30m, quoteRate: 1m, lots, equity: 100_000m);

        Assert.That(approved, Is.True, reasonCode);
    }

    [Test]
    public void Calculator_LongGbpUsdAndShortGbpJpy_NetsGbpButNotUsdOrJpy()
    {
        // Direct calculator-level test (not admission gating) proving the doc's exact
        // §26.5 scenario: a shared-currency leg (GBP) nets down correctly across two
        // different pairs, while the two DIFFERENT quote currencies (USD, JPY) do not
        // spuriously cancel each other - each remains its own large, distinct exposure.
        var calculator = new CurrencyExposureCalculator();
        var lots = new[]
        {
            GbpUsdLot(quantity: 40_000m, price: 1.30m),
            new PortfolioPositionLot
            {
                LotId = "gbpjpy-short",
                StrategyId = "s",
                DecisionId = "d",
                Instrument = GbpJpy,
                Side = OrderSide.Sell,
                Quantity = 40_000m,
                EntryPrice = 195m,
                CurrentPrice = 195m,
                QuoteToAccountCurrencyRate = 0.0067m
            }
        };
        CurrencyExposureResult result = calculator.Calculate(lots, [], new FxConversionSnapshot
        {
            AvailableAt = Time,
            AccountCurrency = "USD",
            CurrencyToAccountRates = new Dictionary<string, decimal> { ["GBP"] = 1.30m, ["JPY"] = 0.0067m }
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.Complete, Is.True);
            Assert.That(result.NetExposureAccountCurrency["GBP"], Is.EqualTo(0m));
            Assert.That(result.NetExposureAccountCurrency["USD"], Is.EqualTo(-52_000m));
            Assert.That(result.NetExposureAccountCurrency["JPY"], Is.EqualTo(52_260m));
            // Gross must not collapse to |net| - GBP nets to zero but still carries
            // 80,000 units of two-sided notional (40,000 long + 40,000 short).
            Assert.That(result.GrossExposureAccountCurrency["GBP"], Is.EqualTo(104_000m));
        });
    }

    [Test]
    public void MissingConversionForAnExistingLot_RejectsCandidate()
    {
        SharedPortfolioRuntime runtime = CreateRuntime(new PortfolioRiskOptions
        {
            MaximumNetCurrencyExposurePercent = 300m,
            MaximumGrossCurrencyExposurePercent = 300m
        });
        // QuoteToAccountCurrencyRate = 0 simulates a lot whose conversion could never be
        // resolved - its currencies must not silently drop out of the exposure picture.
        var lots = new[]
        {
            new PortfolioPositionLot
            {
                LotId = "unresolved",
                StrategyId = "s",
                DecisionId = "d",
                Instrument = AudChf,
                Side = OrderSide.Buy,
                Quantity = 10_000m,
                EntryPrice = 0.55m,
                CurrentPrice = 0.55m,
                QuoteToAccountCurrencyRate = 0m
            }
        };
        var decision = new AgentDecision
        {
            Action = AgentAction.Buy,
            Instrument = GbpUsd,
            Confidence = 70m,
            CreatedAt = Time,
            Reason = "test"
        };

        (bool approved, string? reasonCode, _) = runtime.EvaluateCurrencyExposureLimits(
            Time, decision, quantity: 10_000m, referencePrice: 1.30m, quoteRate: 1m, lots, equity: 100_000m);

        Assert.Multiple(() =>
        {
            Assert.That(approved, Is.False);
            Assert.That(reasonCode, Is.EqualTo("CurrencyConversionUnavailable"));
        });
    }

    [Test]
    public void WellWithinLimits_IsApproved()
    {
        SharedPortfolioRuntime runtime = CreateRuntime(new PortfolioRiskOptions());
        var decision = new AgentDecision
        {
            Action = AgentAction.Buy,
            Instrument = GbpUsd,
            Confidence = 70m,
            CreatedAt = Time,
            Reason = "test"
        };

        (bool approved, string? reasonCode, _) = runtime.EvaluateCurrencyExposureLimits(
            Time, decision, quantity: 1_000m, referencePrice: 1.30m, quoteRate: 1m, [], equity: 100_000m);

        Assert.That(approved, Is.True, reasonCode);
    }

    private static PortfolioPositionLot GbpUsdLot(decimal quantity, decimal price) => new()
    {
        LotId = "existing",
        StrategyId = "s",
        DecisionId = "d",
        Instrument = GbpUsd,
        Side = OrderSide.Buy,
        Quantity = quantity,
        EntryPrice = price,
        CurrentPrice = price,
        QuoteToAccountCurrencyRate = 1m
    };

    private static SharedPortfolioRuntime CreateRuntime(PortfolioRiskOptions portfolioRiskOptions) =>
        new(
            new SimulationOptions
            {
                StartingBalance = 100_000m,
                BaseCurrency = "USD",
                Leverage = 20m,
                CommissionRate = 0m,
                SpreadBasisPoints = 0m,
                SlippageBasisPoints = 0m,
                CloseOpenPositionsAtEnd = true,
                BaseCandleGapPolicy = BaseCandleGapPolicy.Throw
            },
            portfolioRiskOptions,
            new PositionSizingOptions
            {
                Mode = PositionSizingMode.FixedQuantity,
                FixedQuantity = 50_000m,
                MinimumQuantity = 1m,
                QuantityStep = 1m,
                Leverage = 20m
            },
            new AdaptiveRiskOptions(),
            new TradingSafetyOptions());
}
