using Brokers.Models;
using ChartAnnotator.MarketData;
using PortfolioManager.Correlation;
using PortfolioManager.Risk;
using RiskManager;
using RiskManager.Safety;
using Simulator.Engine;
using Simulator.Models;

namespace Simulator.Tests;

/// <summary>
/// Proves the shared-runtime correlation wiring described in the audit (finding §4/§9)
/// is real, not the old CorrelationPenalty=0 / per-instrument-cluster stub. Today's
/// simulator only streams one instrument per run (§7), so these exercise
/// SharedPortfolioRuntime's correlation evaluation directly with synthetic
/// multi-instrument decisions rather than through the full engine - full production
/// cross-instrument coverage needs the multi-instrument portfolio clock.
/// </summary>
[TestFixture]
public sealed class CorrelationWiringTests
{
    private static readonly InstrumentKey GbpUsd = new("FX:GBP/USD");
    private static readonly InstrumentKey EurUsd = new("FX:EUR/USD");
    private static readonly InstrumentKey UsdJpy = new("FX:USD/JPY");

    // Exact sample Pearson correlation = 0.8 by construction (repeatable tiling
    // preserves the ratio): dx=[-1.5,-.5,.5,1.5], dy=[-1.5,-.5,1.5,.5],
    // numerator=4, denominators=5*5 -> 4/5 = 0.8.
    private static readonly (decimal X, decimal Y)[] Correlated08Unit =
        [(1m, 1m), (2m, 2m), (3m, 4m), (4m, 3m)];

    // Exact sample Pearson correlation = 0 by construction (orthogonal Hadamard rows).
    private static readonly (decimal X, decimal Y)[] UncorrelatedUnit =
        [(1m, 1m), (-1m, 1m), (1m, -1m), (-1m, -1m)];

    [Test]
    public void Evaluate_WithNoOtherPortfolioInstruments_AppliesNoPenalty()
    {
        SharedPortfolioRuntime runtime = CreateRuntime();
        CorrelationPenaltyDecision decision = runtime.EvaluateCorrelation(
            GbpUsd, OrderSide.Buy, new Dictionary<InstrumentKey, int>());

        Assert.Multiple(() =>
        {
            Assert.That(decision.RiskMultiplier, Is.EqualTo(1m));
            Assert.That(decision.ReasonCode, Is.EqualTo("NoOtherPortfolioInstruments"));
        });
    }

    [Test]
    public void Evaluate_HighlyCorrelatedSameDirection_ReceivesHardPenalty()
    {
        SharedPortfolioRuntime runtime = CreateRuntime();
        FeedExactCorrelation(runtime, GbpUsd, EurUsd, Correlated08Unit, repeats: 20);

        CorrelationPenaltyDecision decision = runtime.EvaluateCorrelation(
            GbpUsd, OrderSide.Buy, new Dictionary<InstrumentKey, int> { [EurUsd] = 1 });

        Assert.Multiple(() =>
        {
            Assert.That(decision.ReasonCode, Is.EqualTo("HardCorrelationPenalty"));
            Assert.That(decision.RiskMultiplier, Is.EqualTo(0.40m));
            Assert.That(decision.MaximumRelevantCorrelation, Is.EqualTo(0.8m));
        });
    }

    [Test]
    public void Evaluate_SameCandlesDifferentThresholds_ProducesDifferentResult()
    {
        // Same synthetic return series (exact correlation 0.8) fed into two runtimes
        // that differ only by correlation threshold configuration must produce
        // different risk decisions - this is the required §9 end-to-end proof that
        // Dashboard threshold fields actually change a deterministic result rather
        // than being cosmetic.
        SharedPortfolioRuntime strict = CreateRuntime(new CorrelationRiskOptions
        {
            SoftCorrelationThreshold = 0.50m,
            HardCorrelationThreshold = 0.75m,
            SoftRiskMultiplier = 0.70m,
            HardRiskMultiplier = 0.40m
        });
        SharedPortfolioRuntime lenient = CreateRuntime(new CorrelationRiskOptions
        {
            SoftCorrelationThreshold = 0.85m,
            HardCorrelationThreshold = 0.95m,
            SoftRiskMultiplier = 0.70m,
            HardRiskMultiplier = 0.40m
        });

        FeedExactCorrelation(strict, GbpUsd, EurUsd, Correlated08Unit, repeats: 20);
        FeedExactCorrelation(lenient, GbpUsd, EurUsd, Correlated08Unit, repeats: 20);

        CorrelationPenaltyDecision strictDecision = strict.EvaluateCorrelation(
            GbpUsd, OrderSide.Buy, new Dictionary<InstrumentKey, int> { [EurUsd] = 1 });
        CorrelationPenaltyDecision lenientDecision = lenient.EvaluateCorrelation(
            GbpUsd, OrderSide.Buy, new Dictionary<InstrumentKey, int> { [EurUsd] = 1 });

        Assert.That(strictDecision.RiskMultiplier, Is.Not.EqualTo(lenientDecision.RiskMultiplier));
        Assert.That(strictDecision.ReasonCode, Is.EqualTo("HardCorrelationPenalty"));
        Assert.That(lenientDecision.ReasonCode, Is.EqualTo("NoCorrelationPenalty"));
    }

    [Test]
    public void Evaluate_InverseDirectionOnCorrelatedInstruments_ReceivesCappedHedgeCredit()
    {
        SharedPortfolioRuntime runtime = CreateRuntime();
        FeedExactCorrelation(runtime, GbpUsd, EurUsd, Correlated08Unit, repeats: 20);

        // Existing EUR/USD long, candidate GBP/USD short: correlated instruments in
        // opposite directions net out risk, capped so it never exceeds full size.
        CorrelationPenaltyDecision decision = runtime.EvaluateCorrelation(
            GbpUsd, OrderSide.Sell, new Dictionary<InstrumentKey, int> { [EurUsd] = 1 });

        Assert.Multiple(() =>
        {
            Assert.That(decision.ReasonCode, Is.EqualTo("CappedHedgeCredit"));
            Assert.That(decision.RiskMultiplier, Is.EqualTo(1m));
        });
    }

    [Test]
    public void Evaluate_InsufficientSamples_UsesConservativeFallback()
    {
        SharedPortfolioRuntime runtime = CreateRuntime(new CorrelationRiskOptions
        {
            MinimumSamples = 60
        });
        FeedExactCorrelation(runtime, GbpUsd, EurUsd, Correlated08Unit, repeats: 1);

        CorrelationPenaltyDecision decision = runtime.EvaluateCorrelation(
            GbpUsd, OrderSide.Buy, new Dictionary<InstrumentKey, int> { [EurUsd] = 1 });

        Assert.Multiple(() =>
        {
            Assert.That(decision.ReasonCode, Is.EqualTo("CorrelationDataInsufficient"));
            Assert.That(decision.RiskMultiplier, Is.EqualTo(0.70m));
        });
    }

    [Test]
    public void Evaluate_UncorrelatedInstruments_UsesFullMultiplier()
    {
        SharedPortfolioRuntime runtime = CreateRuntime();
        FeedExactCorrelation(runtime, GbpUsd, UsdJpy, UncorrelatedUnit, repeats: 20);

        CorrelationPenaltyDecision decision = runtime.EvaluateCorrelation(
            GbpUsd, OrderSide.Buy, new Dictionary<InstrumentKey, int> { [UsdJpy] = 1 });

        Assert.Multiple(() =>
        {
            Assert.That(decision.ReasonCode, Is.EqualTo("NoCorrelationPenalty"));
            Assert.That(decision.RiskMultiplier, Is.EqualTo(1m));
            Assert.That(decision.MaximumRelevantCorrelation, Is.EqualTo(0m));
        });
    }

    [Test]
    public void ObserveCompletedReturns_RequiresChronologicalObservations()
    {
        SharedPortfolioRuntime runtime = CreateRuntime();
        DateTimeOffset start = new(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);
        runtime.ObserveCompletedReturns(start, new Dictionary<InstrumentKey, decimal> { [GbpUsd] = 0.001m });
        Assert.Throws<InvalidOperationException>(() =>
            runtime.ObserveCompletedReturns(start, new Dictionary<InstrumentKey, decimal> { [GbpUsd] = 0.001m }));
    }

    private static void FeedExactCorrelation(
        SharedPortfolioRuntime runtime,
        InstrumentKey left,
        InstrumentKey right,
        (decimal X, decimal Y)[] unit,
        int repeats)
    {
        DateTimeOffset time = new(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);
        for (int repeat = 0; repeat < repeats; repeat++)
        {
            foreach ((decimal x, decimal y) in unit)
            {
                time = time.AddHours(1);
                runtime.ObserveCompletedReturns(time, new Dictionary<InstrumentKey, decimal>
                {
                    [left] = x * 0.001m,
                    [right] = y * 0.001m
                });
            }
        }
    }

    private static SharedPortfolioRuntime CreateRuntime(CorrelationRiskOptions? correlationOptions = null) =>
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
            new PortfolioRiskOptions(),
            new PositionSizingOptions
            {
                Mode = PositionSizingMode.FixedQuantity,
                FixedQuantity = 50_000m,
                MinimumQuantity = 1m,
                QuantityStep = 1m,
                Leverage = 20m
            },
            new AdaptiveRiskOptions(),
            new TradingSafetyOptions(),
            correlationOptions);
}
