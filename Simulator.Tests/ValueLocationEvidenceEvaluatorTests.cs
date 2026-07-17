using Brokers.Models;
using ChartAnnotator.Models;
using ChartAnnotator.Value;

namespace Simulator.Tests;

/// <summary>
/// Proves the optional value-location evidence described in the audit (§13.3) is a
/// real, pure, reason-coded evaluation over already-computed AnchoredValueReference
/// data - soft evidence only, disabled by default, never a hard veto.
/// </summary>
[TestFixture]
public sealed class ValueLocationEvidenceEvaluatorTests
{
    private static readonly InstrumentKey Instrument = new("FX:GBP/USD");
    private static readonly DateTimeOffset Time = new(2026, 1, 5, 10, 0, 0, TimeSpan.Zero);

    [Test]
    public void Disabled_ProducesNoEvidence()
    {
        AnalysisSnapshot snapshot = Snapshot(open: 1.10m, high: 1.11m, low: 1.09m, close: 1.105m,
            valueReferences: [Anchor(1.10m, distanceAtr: 0.1m)]);

        ValueLocationEvidence evidence = ValueLocationEvidenceEvaluator.Evaluate(
            snapshot, isBuy: true, new ValueLocationEvidenceOptions { Enabled = false });

        Assert.That(evidence.ReasonCodes, Is.Empty);
    }

    [Test]
    public void PriceNearAnchor_AddsSupportiveEvidenceAndPositiveAdjustment()
    {
        AnalysisSnapshot snapshot = Snapshot(open: 1.10m, high: 1.101m, low: 1.099m, close: 1.1005m,
            valueReferences: [Anchor(1.10m, distanceAtr: 0.1m)]);

        ValueLocationEvidence evidence = ValueLocationEvidenceEvaluator.Evaluate(
            snapshot, isBuy: true, EnabledOptions());

        Assert.Multiple(() =>
        {
            Assert.That(evidence.ReasonCodes, Does.Contain("ValueNearAnchor"));
            Assert.That(evidence.ConfidenceAdjustment, Is.GreaterThan(0m));
        });
    }

    [Test]
    public void PriceStretchedInTradeDirection_AddsCautionaryEvidenceAndNegativeAdjustment()
    {
        // Long candidate, price is far ABOVE value (chasing an extended move).
        AnalysisSnapshot snapshot = Snapshot(open: 1.13m, high: 1.135m, low: 1.129m, close: 1.132m,
            valueReferences: [Anchor(1.10m, distanceAtr: 3.0m)]);

        ValueLocationEvidence evidence = ValueLocationEvidenceEvaluator.Evaluate(
            snapshot, isBuy: true, EnabledOptions());

        Assert.Multiple(() =>
        {
            Assert.That(evidence.ReasonCodes, Does.Contain("BreakoutStretchedFromValue"));
            Assert.That(evidence.ConfidenceAdjustment, Is.LessThan(0m));
        });
    }

    [Test]
    public void PriceStretchedAgainstTradeDirection_DoesNotTriggerBreakoutCaution()
    {
        // Long candidate, but price is far BELOW value - not a "chasing the breakout"
        // situation for a long, so BreakoutStretchedFromValue must not fire here.
        AnalysisSnapshot snapshot = Snapshot(open: 1.07m, high: 1.075m, low: 1.069m, close: 1.072m,
            valueReferences: [Anchor(1.10m, distanceAtr: -3.0m)]);

        ValueLocationEvidence evidence = ValueLocationEvidenceEvaluator.Evaluate(
            snapshot, isBuy: true, EnabledOptions());

        Assert.That(evidence.ReasonCodes, Does.Not.Contain("BreakoutStretchedFromValue"));
    }

    [Test]
    public void CandleWicksThroughValueAndClosesBeyondInTradeDirection_IsRejectionEvidence()
    {
        // Long candidate: low dips to/through value, close finishes above it.
        AnalysisSnapshot snapshot = Snapshot(open: 1.102m, high: 1.106m, low: 1.099m, close: 1.104m,
            valueReferences: [Anchor(1.10m, distanceAtr: 0.4m)]);

        ValueLocationEvidence evidence = ValueLocationEvidenceEvaluator.Evaluate(
            snapshot, isBuy: true, EnabledOptions());

        Assert.That(evidence.ReasonCodes, Does.Contain("PriceRejectsValueInTrendDirection"));
    }

    [Test]
    public void WrongSideOfValueDuringStructuralBreakAgainstTrade_IsCautionaryEvidence()
    {
        // Long candidate: close is BELOW value (wrong side, but not "near" it) while
        // structure just broke bearish - isolates the deterioration signal alone.
        AnalysisSnapshot snapshot = Snapshot(open: 1.089m, high: 1.090m, low: 1.085m, close: 1.086m,
            valueReferences: [Anchor(1.10m, distanceAtr: -1.0m)],
            structureBreak: MarketStructureBreak.Bearish);

        ValueLocationEvidence evidence = ValueLocationEvidenceEvaluator.Evaluate(
            snapshot, isBuy: true, EnabledOptions());

        Assert.Multiple(() =>
        {
            Assert.That(evidence.ReasonCodes, Does.Contain("PriceLosesValueDuringStructuralDeterioration"));
            Assert.That(evidence.ConfidenceAdjustment, Is.LessThan(0m));
        });
    }

    [Test]
    public void NoValueReferencesAvailable_ProducesNoEvidence()
    {
        AnalysisSnapshot snapshot = Snapshot(open: 1.10m, high: 1.11m, low: 1.09m, close: 1.105m, valueReferences: []);

        ValueLocationEvidence evidence = ValueLocationEvidenceEvaluator.Evaluate(
            snapshot, isBuy: true, EnabledOptions());

        Assert.That(evidence.ReasonCodes, Is.Empty);
    }

    private static ValueLocationEvidenceOptions EnabledOptions() => new()
    {
        Enabled = true,
        NearValueAtrThreshold = 0.5m,
        StretchedFromValueAtrThreshold = 2.5m,
        ConfidenceAdjustmentPerSignal = 3m
    };

    private static AnchoredValueReference Anchor(decimal value, decimal distanceAtr) => new()
    {
        AnchorId = "session:test",
        AnchorType = ValueAnchorType.SessionOpen,
        AnchoredAt = Time.AddHours(-2),
        Kind = ValueReferenceKind.AnchoredTwap,
        Value = value,
        DistanceAtr = distanceAtr
    };

    private static AnalysisSnapshot Snapshot(
        decimal open,
        decimal high,
        decimal low,
        decimal close,
        IReadOnlyList<AnchoredValueReference> valueReferences,
        MarketStructureBreak structureBreak = MarketStructureBreak.None) => new()
    {
        Instrument = Instrument,
        Interval = BarInterval.Minutes(5),
        AvailableAt = Time,
        Version = 1,
        LatestCandle = TestCandles.Create(Instrument, Time, BarInterval.Minutes(5), open, high, low, close),
        Indicators = new IndicatorSnapshot(),
        Swings = [],
        PriceZones = [],
        Trendlines = [],
        Channels = [],
        MarketStructure = MarketStructureSnapshot.Empty with { Break = structureBreak },
        ValueReferences = valueReferences,
        Confidence = new ConfidenceScore { Total = 0m, Contributions = [] }
    };
}
