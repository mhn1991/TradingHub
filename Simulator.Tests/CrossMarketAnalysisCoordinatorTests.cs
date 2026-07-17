using Brokers.Models;
using ChartAnnotator.Models;
using PortfolioManager.CrossMarket;

namespace Simulator.Tests;

/// <summary>
/// Proves the currency-strength coordinator described in the audit (§5/§26.2) is real:
/// leave-one-out excludes the traded pair from its own differential, insufficient
/// coverage produces null (not a fabricated neutral value), and observations must be
/// chronological (no lookahead).
/// </summary>
[TestFixture]
public sealed class CrossMarketAnalysisCoordinatorTests
{
    private static readonly InstrumentKey GbpUsd = new("FX:GBP/USD");
    private static readonly InstrumentKey EurUsd = new("FX:EUR/USD");
    private static readonly InstrumentKey UsdJpy = new("FX:USD/JPY");

    // Alternating up/down multipliers give genuine non-zero realized volatility (real
    // market data is never perfectly smooth) while still trending in one net direction -
    // a pure constant multiplicative drift produces identical 1-bar returns every step,
    // i.e. exactly zero variance, which the coordinator correctly treats as "no usable
    // volatility" and excludes.
    private static decimal Step(decimal price, int index, decimal up, decimal down) =>
        price * (index % 2 == 0 ? up : down);

    [Test]
    public void ComputeDifferential_LeavesTradedPairOutOfItsOwnComputation()
    {
        CrossMarketAnalysisCoordinator coordinator = CreateCoordinator();
        DateTimeOffset time = new(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);

        // GBP/USD trends up; EUR/USD and USD/JPY provide alternative USD observations
        // so leave-one-out still has data to work with.
        decimal gbpUsdClose = 1.20m;
        decimal eurUsdClose = 1.10m;
        decimal usdJpyClose = 150m;
        for (int i = 0; i < 20; i++)
        {
            time = time.AddHours(1);
            gbpUsdClose = Step(gbpUsdClose, i, 1.006m, 0.999m);
            eurUsdClose = Step(eurUsdClose, i, 1.002m, 0.9996m);
            usdJpyClose = Step(usdJpyClose, i, 1.0009m, 0.9997m);
            coordinator.Update(time, new Dictionary<InstrumentKey, decimal>
            {
                [GbpUsd] = gbpUsdClose,
                [EurUsd] = eurUsdClose,
                [UsdJpy] = usdJpyClose
            });
        }

        CurrencyStrengthSnapshot? withGbpUsd = coordinator.GetSnapshot();
        CurrencyStrengthSnapshot? leaveOut = coordinator.GetSnapshot(GbpUsd);

        Assert.Multiple(() =>
        {
            Assert.That(withGbpUsd, Is.Not.Null);
            Assert.That(leaveOut, Is.Not.Null);
            // GBP only ever appears via GBP/USD, so leaving it out must drop GBP
            // entirely from the snapshot while still scoring USD/EUR/JPY from the
            // remaining pairs.
            Assert.That(leaveOut!.Scores.ContainsKey("GBP"), Is.False);
            Assert.That(withGbpUsd!.Scores.ContainsKey("GBP"), Is.True);
            Assert.That(leaveOut.Scores.ContainsKey("USD"), Is.True);
            Assert.That(leaveOut.Scores.ContainsKey("EUR"), Is.True);
            Assert.That(leaveOut.Scores.ContainsKey("JPY"), Is.True);
        });
    }

    [Test]
    public void ComputeDifferential_StrongerBaseWeakerQuote_IsPositive()
    {
        // Leave-one-out drops GBP/USD entirely, so GBP needs an alternative pair
        // (GBP/JPY) to have any score at all once its own traded pair is excluded -
        // this is the doc's explicit "when enough alternative pairs exist" caveat:
        // without GBP/JPY here, the differential would correctly be null (see
        // ComputeDifferential_MissingInstrumentData_ReturnsNull for that case).
        InstrumentKey gbpJpy = new("FX:GBP/JPY");
        CrossMarketAnalysisCoordinator coordinator = new(new CurrencyStrengthOptions
        {
            Enabled = true,
            Interval = BarInterval.Hours(1),
            ReturnLookbackBars = 4,
            VolatilityLookbackBars = 8,
            MinimumCurrencyCoveragePercent = 1m,
            Baskets = new Dictionary<string, IReadOnlyList<InstrumentKey>>
            {
                ["Majors"] = [GbpUsd, EurUsd, UsdJpy, gbpJpy]
            }
        });
        DateTimeOffset time = new(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);
        decimal gbpUsdClose = 1.20m;
        decimal eurUsdClose = 1.10m;
        decimal usdJpyClose = 150m;
        decimal gbpJpyClose = 180m;
        for (int i = 0; i < 20; i++)
        {
            time = time.AddHours(1);
            // GBP rallies hard on both its pairs (base strong); EUR/USD and USD/JPY
            // only drift mildly, so USD (GBP/USD's quote currency) ends up relatively weak.
            gbpUsdClose = Step(gbpUsdClose, i, 1.012m, 0.999m);
            gbpJpyClose = Step(gbpJpyClose, i, 1.012m, 0.999m);
            eurUsdClose = Step(eurUsdClose, i, 1.0006m, 0.9998m);
            usdJpyClose = Step(usdJpyClose, i, 1.0005m, 0.9997m);
            coordinator.Update(time, new Dictionary<InstrumentKey, decimal>
            {
                [GbpUsd] = gbpUsdClose,
                [EurUsd] = eurUsdClose,
                [UsdJpy] = usdJpyClose,
                [gbpJpy] = gbpJpyClose
            });
        }

        decimal? differential = coordinator.ComputeDifferential(GbpUsd);

        Assert.That(differential, Is.Not.Null);
        Assert.That(differential!.Value, Is.GreaterThan(0m));
    }

    [Test]
    public void ComputeDifferential_InsufficientCoverage_ReturnsNull()
    {
        // Minimum coverage requires the weakest-observed currency's weight to be at
        // least 90% of the strongest - a currency observed only once early on and
        // never again should fail that bar and report unavailable rather than a
        // fabricated neutral value.
        CrossMarketAnalysisCoordinator coordinator = CreateCoordinator(minimumCoveragePercent: 90m);
        DateTimeOffset time = new(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);
        decimal gbpUsdClose = 1.20m;
        decimal eurUsdClose = 1.10m;
        for (int i = 0; i < 20; i++)
        {
            time = time.AddHours(1);
            gbpUsdClose = Step(gbpUsdClose, i, 1.003m, 0.9994m);
            eurUsdClose = Step(eurUsdClose, i, 1.002m, 0.9995m);
            var closes = new Dictionary<InstrumentKey, decimal> { [GbpUsd] = gbpUsdClose };
            // EUR/USD only observed on the first few bars, then goes dark - thin coverage.
            if (i < 3) closes[EurUsd] = eurUsdClose;
            coordinator.Update(time, closes);
        }

        decimal? differential = coordinator.ComputeDifferential(GbpUsd);

        Assert.That(differential, Is.Null);
    }

    [Test]
    public void ComputeDifferential_MissingInstrumentData_ReturnsNull()
    {
        CrossMarketAnalysisCoordinator coordinator = CreateCoordinator();
        // No observations fed at all.
        Assert.That(coordinator.ComputeDifferential(GbpUsd), Is.Null);
        Assert.That(coordinator.GetSnapshot(), Is.Null);
    }

    [Test]
    public void Update_RequiresChronologicalObservations()
    {
        CrossMarketAnalysisCoordinator coordinator = CreateCoordinator();
        DateTimeOffset time = new(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);
        coordinator.Update(time, new Dictionary<InstrumentKey, decimal> { [GbpUsd] = 1.2m });
        Assert.Throws<InvalidOperationException>(() =>
            coordinator.Update(time, new Dictionary<InstrumentKey, decimal> { [GbpUsd] = 1.21m }));
    }

    [Test]
    public void Update_DeterministicForUnorderedInputDictionaries()
    {
        CrossMarketAnalysisCoordinator first = CreateCoordinator();
        CrossMarketAnalysisCoordinator second = CreateCoordinator();
        DateTimeOffset time = new(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);
        decimal gbpUsdClose = 1.20m;
        decimal eurUsdClose = 1.10m;
        for (int i = 0; i < 20; i++)
        {
            time = time.AddHours(1);
            gbpUsdClose = Step(gbpUsdClose, i, 1.004m, 0.9992m);
            eurUsdClose = Step(eurUsdClose, i, 1.0015m, 0.9997m);
            // Same data, deliberately inserted in a different dictionary order.
            first.Update(time, new Dictionary<InstrumentKey, decimal>
            {
                [GbpUsd] = gbpUsdClose,
                [EurUsd] = eurUsdClose
            });
            second.Update(time, new Dictionary<InstrumentKey, decimal>
            {
                [EurUsd] = eurUsdClose,
                [GbpUsd] = gbpUsdClose
            });
        }

        CurrencyStrengthSnapshot? snapshotA = first.GetSnapshot();
        CurrencyStrengthSnapshot? snapshotB = second.GetSnapshot();

        Assert.That(snapshotA!.Scores, Is.EqualTo(snapshotB!.Scores));
    }

    private static CrossMarketAnalysisCoordinator CreateCoordinator(decimal minimumCoveragePercent = 10m) =>
        new(new CurrencyStrengthOptions
        {
            Enabled = true,
            Interval = BarInterval.Hours(1),
            ReturnLookbackBars = 4,
            VolatilityLookbackBars = 8,
            MinimumCurrencyCoveragePercent = minimumCoveragePercent,
            Baskets = new Dictionary<string, IReadOnlyList<InstrumentKey>>
            {
                ["Majors"] = [GbpUsd, EurUsd, UsdJpy]
            }
        });
}
