using QuantResearch.Analysis;
using QuantResearch.Models;

namespace QuantResearchRunner.Tests;

[TestFixture]
public sealed class NeoWaveAttributionTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public void Analyze_SeparatesEvidenceAndBuildsDeterministicCohorts()
    {
        ResearchTrade[] trades =
        [
            Trade("1", 1.5m, "ImpulseCandidate", 82m, 12m, 0.8m),
            Trade("2", -0.5m, "ImpulseCandidate", 88m, 18m, 0.9m),
            Trade("3", 0.5m, "TrendSequence", 65m, 35m, 1m),
            Trade("4", -1m, null, null, null, 1m)
        ];

        NeoWaveAttributionReport report = NeoWaveAttribution.Analyze(
            trades,
            new NeoWaveAttributionOptions { MinimumCohortSamples = 2 },
            Start.AddDays(10));

        Assert.Multiple(() =>
        {
            Assert.That(report.TotalTrades, Is.EqualTo(4));
            Assert.That(report.TradesWithEvidence, Is.EqualTo(3));
            Assert.That(report.TradesWithoutEvidence, Is.EqualTo(1));
            Assert.That(report.Cohorts, Has.Count.EqualTo(2));
            NeoWaveAttributionCohort impulse = report.Cohorts.Single(
                item => item.PatternType == "ImpulseCandidate");
            Assert.That(impulse.StructuralScoreBucket, Is.EqualTo("80-90"));
            Assert.That(impulse.ConflictScoreBucket, Is.EqualTo("10-20"));
            Assert.That(impulse.TradeCount, Is.EqualTo(2));
            Assert.That(impulse.WinRate, Is.EqualTo(50m));
            Assert.That(impulse.AverageR, Is.EqualTo(0.5m));
            Assert.That(impulse.ProfitFactor, Is.EqualTo(3m));
            Assert.That(impulse.AverageRiskMultiplier, Is.EqualTo(0.85m));
            Assert.That(impulse.MeetsMinimumSamples, Is.True);
        });
    }

    [Test]
    public void Analyze_DoesNotTreatPartialEvidenceAsTrusted()
    {
        ResearchTrade trade = Trade("1", 1m, "ImpulseCandidate", null, 10m, 1m);

        NeoWaveAttributionReport report = NeoWaveAttribution.Analyze([trade]);

        Assert.Multiple(() =>
        {
            Assert.That(report.TradesWithEvidence, Is.Zero);
            Assert.That(report.TradesWithoutEvidence, Is.EqualTo(1));
            Assert.That(report.Cohorts, Is.Empty);
        });
    }

    private static ResearchTrade Trade(
        string id,
        decimal r,
        string? pattern,
        decimal? structural,
        decimal? conflict,
        decimal riskMultiplier) => new()
    {
        TradeId = id,
        StrategyId = "improved",
        Instrument = "FX:EUR/USD",
        InstrumentGroup = "FX",
        Regime = "TrendingUp",
        SetupType = "Breakout",
        Direction = "Buy",
        Session = "London",
        VolatilityBucket = "Normal",
        Confidence = 70m,
        OpenedAt = Start.AddHours(int.Parse(id)),
        ClosedAt = Start.AddHours(int.Parse(id) + 1),
        RMultiple = r,
        MaximumFavourableExcursionR = Math.Max(r, 1m),
        MaximumAdverseExcursionR = -0.4m,
        StopDistance = 0.005m,
        NeoWaveHypothesisId = pattern is null ? null : $"wave-{id}",
        NeoWavePatternType = pattern,
        NeoWaveStructuralScore = structural,
        NeoWaveConflictScore = conflict,
        NeoWaveRiskMultiplier = riskMultiplier
    };
}
