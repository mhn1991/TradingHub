using Agent.Strategies;
using Brokers.Models;
using ChartAnnotator.Models;
using ChartAnnotator.SupplyDemand;
using RiskManager;

namespace Simulator.Tests;

[TestFixture]
public sealed class StructuralEvidencePolicyTests
{
    private static readonly InstrumentKey Instrument = new("EURUSD");
    private static readonly BarInterval Interval = BarInterval.Minutes(15);
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-01-05T12:00:00Z");

    [Test]
    public void Defaults_AreObservableButBehaviorallyNeutral()
    {
        var options = new ProgressiveStrategyOptions();

        Assert.Multiple(() =>
        {
            Assert.That(options.SupplyDemandEnabled, Is.True);
            Assert.That(options.SupplyDemandEvidenceMode, Is.EqualTo(StructuralEvidenceMode.RecordOnly));
            Assert.That(options.LiquidityEnabled, Is.True);
            Assert.That(options.LiquidityEvidenceMode, Is.EqualTo(StructuralEvidenceMode.RecordOnly));
            Assert.That(options.SupplyDemandStructuralStopsEnabled, Is.False);
            Assert.That(options.SupplyDemandTargetsEnabled, Is.False);
            Assert.That(options.LiquidityTargetsEnabled, Is.False);
            Assert.That(options.LiquidityStopAvoidanceEnabled, Is.False);
            Assert.That(options.SupplyDemandManagementEnabled, Is.False);
            Assert.That(options.LiquidityManagementEnabled, Is.False);
            Assert.That(options.SupplyDemandLiquidityConfluenceEnabled, Is.False);
        });
    }

    [Test]
    public void RecordOnly_RecordsOppositionWithoutChangingConfidenceOrRisk()
    {
        AnalysisSnapshot snapshot = Snapshot(SupplyDemand([Zone(SupplyDemandZoneType.Supply, 100m)]));

        SupplyDemandDecisionEvidence result = StructuralEvidenceEvaluator.EvaluateSupplyDemand(
            snapshot,
            buy: true,
            enabled: true,
            StructuralEvidenceMode.RecordOnly,
            new StructuralEvidenceOptions());

        Assert.Multiple(() =>
        {
            Assert.That(result.DirectionOpposed, Is.True);
            Assert.That(result.ConfidenceAdjustment, Is.Zero);
            Assert.That(result.RiskMultiplier, Is.EqualTo(1m));
            Assert.That(result.ReasonCodes, Does.Contain("SupplyDemandOpposed"));
        });
    }

    [Test]
    public void MissingAnalysis_IsNeutral()
    {
        SupplyDemandDecisionEvidence result = StructuralEvidenceEvaluator.EvaluateSupplyDemand(
            Snapshot(SupplyDemandAnalysisSnapshot.Disabled),
            buy: true,
            enabled: true,
            StructuralEvidenceMode.SoftConfidenceAndRisk,
            new StructuralEvidenceOptions());

        Assert.Multiple(() =>
        {
            Assert.That(result.ConfidenceAdjustment, Is.Zero);
            Assert.That(result.RiskMultiplier, Is.EqualTo(1m));
            Assert.That(result.DirectionAligned, Is.False);
            Assert.That(result.DirectionOpposed, Is.False);
        });
    }

    [Test]
    public void SoftRiskReduction_AndRiskBudget_CanNeverIncreaseRisk()
    {
        SupplyDemandDecisionEvidence evidence = StructuralEvidenceEvaluator.EvaluateSupplyDemand(
            Snapshot(SupplyDemand([Zone(SupplyDemandZoneType.Supply, 100m)])),
            buy: true,
            enabled: true,
            StructuralEvidenceMode.SoftRiskReduction,
            new StructuralEvidenceOptions
            {
                MinimumRiskMultiplier = 0.7m,
                MaximumConflictRiskReduction = 0.25m
            });
        RiskBudgetDecision budget = new RiskBudgetPolicy(new AdaptiveRiskOptions { Enabled = false }).Evaluate(
            new RiskBudgetContext
            {
                AccountEquity = 10_000m,
                StructuralEvidenceMultiplier = evidence.RiskMultiplier
            });

        Assert.Multiple(() =>
        {
            Assert.That(evidence.RiskMultiplier, Is.InRange(0.7m, 1m));
            Assert.That(evidence.RiskMultiplier, Is.LessThan(1m));
            Assert.That(budget.StructuralEvidenceMultiplier, Is.EqualTo(evidence.RiskMultiplier));
            Assert.That(budget.CombinedMultiplier, Is.LessThanOrEqualTo(1m));
        });
    }

    private static AnalysisSnapshot Snapshot(SupplyDemandAnalysisSnapshot supplyDemand) => new()
    {
        Instrument = Instrument,
        Interval = Interval,
        AvailableAt = At,
        Version = 1,
        LatestCandle = TestCandles.Create(Instrument, At.AddMinutes(-15), Interval, 100m, 100.5m, 99.5m, 100m),
        Indicators = new IndicatorSnapshot { Atr = 1m },
        Swings = [],
        PriceZones = [],
        Trendlines = [],
        Channels = [],
        SupplyDemand = supplyDemand,
        Confidence = new ConfidenceScore { Total = 0m, Contributions = [] }
    };

    private static SupplyDemandAnalysisSnapshot SupplyDemand(IReadOnlyList<SupplyDemandZone> zones) => new()
    {
        IsEnabled = true,
        ProfileHash = "sd",
        SnapshotVersion = 1,
        AvailableAt = At,
        Zones = zones,
        ActiveZones = zones,
        RecentEvents = [],
        Quality = new SupplyDemandAnalysisQuality
        {
            AtrReady = true,
            ActiveZoneCount = zones.Count,
            SuppressedCandidateCount = 0,
            LastEvaluatedAt = At
        }
    };

    private static SupplyDemandZone Zone(SupplyDemandZoneType type, decimal centre) => new()
    {
        ZoneId = Guid.NewGuid(),
        Instrument = Instrument,
        Interval = Interval,
        Type = type,
        Pattern = type == SupplyDemandZoneType.Supply
            ? SupplyDemandPattern.RallyBaseDrop
            : SupplyDemandPattern.DropBaseRally,
        ProximalPrice = centre - 0.1m,
        DistalPrice = centre + 0.1m,
        BaseStartedAt = At.AddHours(-1),
        BaseEndedAt = At.AddMinutes(-45),
        DepartureStartedAt = At.AddMinutes(-30),
        ConfirmedAt = At.AddMinutes(-15),
        AvailableAt = At.AddMinutes(-15),
        State = SupplyDemandZoneState.ConfirmedFresh,
        BaseCandleCount = 2,
        TouchCount = 0,
        DepartureAtr = 2m,
        DepartureEfficiency = 0.8m,
        BaseCompactness = 0.8m,
        ImbalanceRatio = 0.8m,
        PenetrationRatio = 0m,
        FreshnessScore = 1m,
        QualityScore = 0.8m,
        BrokeStructure = true,
        HasFairValueGap = false,
        BoundaryMode = ZoneBoundaryMode.FullWickRange,
        SourceZoneIds = [],
        SnapshotVersion = 1,
        ProfileHash = "sd"
    };
}
