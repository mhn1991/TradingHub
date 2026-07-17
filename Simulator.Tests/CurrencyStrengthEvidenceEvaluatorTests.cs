using Brokers.Models;
using ChartAnnotator.CurrencyStrength;
using ChartAnnotator.Models;

namespace Simulator.Tests;

/// <summary>
/// Proves the currency-strength confluence/opposition evaluator is a real, pure,
/// reason-coded evaluation over a CurrencyStrengthSnapshot's base/quote scores - soft
/// evidence only, disabled by default, never a hard veto.
/// </summary>
[TestFixture]
public sealed class CurrencyStrengthEvidenceEvaluatorTests
{
    private static readonly InstrumentKey EurUsd = new("FX:EUR/USD");
    private static readonly DateTimeOffset Time = new(2026, 1, 5, 10, 0, 0, TimeSpan.Zero);

    [Test]
    public void Disabled_ProducesNoEvidence()
    {
        CurrencyStrengthSnapshot snapshot = Snapshot(eur: 0.5m, usd: -0.5m);

        CurrencyStrengthEvidence evidence = CurrencyStrengthEvidenceEvaluator.Evaluate(
            EurUsd, snapshot, isBuy: true, new CurrencyStrengthEvidenceOptions { Enabled = false });

        Assert.That(evidence.ReasonCodes, Is.Empty);
    }

    [Test]
    public void NullSnapshot_ProducesNoEvidence()
    {
        CurrencyStrengthEvidence evidence = CurrencyStrengthEvidenceEvaluator.Evaluate(
            EurUsd, snapshot: null, isBuy: true, EnabledOptions());

        Assert.That(evidence.ReasonCodes, Is.Empty);
    }

    [Test]
    public void BaseStrongerThanQuote_BuySide_IsConfluence()
    {
        // EUR strong, USD weak: a EUR/USD Buy is going with currency strength.
        CurrencyStrengthSnapshot snapshot = Snapshot(eur: 0.6m, usd: -0.4m);

        CurrencyStrengthEvidence evidence = CurrencyStrengthEvidenceEvaluator.Evaluate(
            EurUsd, snapshot, isBuy: true, EnabledOptions());

        Assert.Multiple(() =>
        {
            Assert.That(evidence.ReasonCodes, Does.Contain("CurrencyStrengthConfluence"));
            Assert.That(evidence.ConfidenceAdjustment, Is.GreaterThan(0m));
            Assert.That(evidence.Differential, Is.EqualTo(1.0m));
        });
    }

    [Test]
    public void BaseWeakerThanQuote_BuySide_IsOpposition()
    {
        // EUR weak, USD strong: a EUR/USD Buy is fighting currency strength.
        CurrencyStrengthSnapshot snapshot = Snapshot(eur: -0.4m, usd: 0.6m);

        CurrencyStrengthEvidence evidence = CurrencyStrengthEvidenceEvaluator.Evaluate(
            EurUsd, snapshot, isBuy: true, EnabledOptions());

        Assert.Multiple(() =>
        {
            Assert.That(evidence.ReasonCodes, Does.Contain("CurrencyStrengthOpposition"));
            Assert.That(evidence.ConfidenceAdjustment, Is.LessThan(0m));
        });
    }

    [Test]
    public void BaseStrongerThanQuote_SellSide_IsOpposition()
    {
        // Direction flips the interpretation: EUR strong now opposes a Sell.
        CurrencyStrengthSnapshot snapshot = Snapshot(eur: 0.6m, usd: -0.4m);

        CurrencyStrengthEvidence evidence = CurrencyStrengthEvidenceEvaluator.Evaluate(
            EurUsd, snapshot, isBuy: false, EnabledOptions());

        Assert.That(evidence.ReasonCodes, Does.Contain("CurrencyStrengthOpposition"));
    }

    [Test]
    public void DifferentialBelowThreshold_ProducesNoEvidence()
    {
        CurrencyStrengthSnapshot snapshot = Snapshot(eur: 0.05m, usd: 0.0m);

        CurrencyStrengthEvidence evidence = CurrencyStrengthEvidenceEvaluator.Evaluate(
            EurUsd, snapshot, isBuy: true, EnabledOptions());

        Assert.That(evidence.ReasonCodes, Is.Empty);
    }

    [Test]
    public void LowCoverage_TreatedAsUnavailableNotSignal()
    {
        CurrencyStrengthSnapshot snapshot = new()
        {
            AvailableAt = Time,
            Scores = new Dictionary<string, decimal> { ["EUR"] = 0.6m, ["USD"] = -0.4m },
            Coverage = new Dictionary<string, decimal> { ["EUR"] = 30m, ["USD"] = 90m },
            MethodVersion = "v1"
        };

        CurrencyStrengthEvidence evidence = CurrencyStrengthEvidenceEvaluator.Evaluate(
            EurUsd, snapshot, isBuy: true, EnabledOptions());

        Assert.That(evidence.ReasonCodes, Is.Empty);
    }

    [Test]
    public void MissingCurrencyScore_ProducesNoEvidence()
    {
        CurrencyStrengthSnapshot snapshot = new()
        {
            AvailableAt = Time,
            Scores = new Dictionary<string, decimal> { ["EUR"] = 0.6m },
            Coverage = new Dictionary<string, decimal> { ["EUR"] = 90m },
            MethodVersion = "v1"
        };

        CurrencyStrengthEvidence evidence = CurrencyStrengthEvidenceEvaluator.Evaluate(
            EurUsd, snapshot, isBuy: true, EnabledOptions());

        Assert.That(evidence.ReasonCodes, Is.Empty);
    }

    private static CurrencyStrengthEvidenceOptions EnabledOptions() => new()
    {
        Enabled = true,
        MinimumDifferentialForConfluence = 0.15m,
        MinimumDifferentialForOpposition = 0.15m,
        MinimumCoveragePercent = 60m,
        ConfidenceAdjustmentPerSignal = 4m
    };

    private static CurrencyStrengthSnapshot Snapshot(decimal eur, decimal usd) => new()
    {
        AvailableAt = Time,
        Scores = new Dictionary<string, decimal> { ["EUR"] = eur, ["USD"] = usd },
        Coverage = new Dictionary<string, decimal> { ["EUR"] = 90m, ["USD"] = 90m },
        MethodVersion = "v1"
    };
}
