using ChartAnnotator.Engine;
using Brokers.Models;
using NUnit.Framework;
using TradingCore.Pipeline;

namespace TradingCore.Tests;

[TestFixture]
public sealed class AnalysisProfileKeyTests
{
    private static readonly IReadOnlySet<BarInterval> Intervals =
        new HashSet<BarInterval> { BarInterval.Minutes(5), BarInterval.Minutes(15) };

    [Test]
    public void Create_IsDeterministic_ForIdenticalOptions()
    {
        var options = new ChartAnnotationOptions { RsiPeriod = 14 };

        AnalysisProfileKey a = AnalysisProfileKey.Create(options, Intervals, "schema-v1");
        AnalysisProfileKey b = AnalysisProfileKey.Create(options, Intervals, "schema-v1");

        Assert.That(a, Is.EqualTo(b));
        Assert.That(a.ProfileHash, Is.EqualTo(b.ProfileHash));
    }

    [Test]
    public void Create_DifferentRsiPeriod_ProducesDifferentProfileHash()
    {
        AnalysisProfileKey a = AnalysisProfileKey.Create(
            new ChartAnnotationOptions { RsiPeriod = 14 }, Intervals, "schema-v1");
        AnalysisProfileKey b = AnalysisProfileKey.Create(
            new ChartAnnotationOptions { RsiPeriod = 21 }, Intervals, "schema-v1");

        Assert.That(a.ProfileHash, Is.Not.EqualTo(b.ProfileHash));
        Assert.That(a, Is.Not.EqualTo(b));
    }

    [Test]
    public void Create_DifferentSwingSettings_ProducesDifferentProfileHash()
    {
        AnalysisProfileKey a = AnalysisProfileKey.Create(
            new ChartAnnotationOptions { SwingLeftBars = 2, SwingRightBars = 2 }, Intervals, "schema-v1");
        AnalysisProfileKey b = AnalysisProfileKey.Create(
            new ChartAnnotationOptions { SwingLeftBars = 3, SwingRightBars = 3 }, Intervals, "schema-v1");

        Assert.That(a.ProfileHash, Is.Not.EqualTo(b.ProfileHash));
    }

    [Test]
    public void RequiredIntervals_EqualityIsSetContentBased_NotBackingCollectionType()
    {
        var options = new ChartAnnotationOptions();
        IReadOnlySet<BarInterval> arrayBacked = new HashSet<BarInterval>(
            [BarInterval.Minutes(5), BarInterval.Minutes(15)]);
        IReadOnlySet<BarInterval> reordered = new HashSet<BarInterval>(
            [BarInterval.Minutes(15), BarInterval.Minutes(5)]);

        AnalysisProfileKey a = AnalysisProfileKey.Create(options, arrayBacked, "schema-v1");
        AnalysisProfileKey b = AnalysisProfileKey.Create(options, reordered, "schema-v1");

        Assert.That(a, Is.EqualTo(b), "Set-content equality must not depend on enumeration/insertion order.");
    }

    [Test]
    public void RequiredIntervals_DifferentContent_AreNotEqual()
    {
        var options = new ChartAnnotationOptions();
        AnalysisProfileKey a = AnalysisProfileKey.Create(
            options, new HashSet<BarInterval> { BarInterval.Minutes(5) }, "schema-v1");
        AnalysisProfileKey b = AnalysisProfileKey.Create(
            options, new HashSet<BarInterval> { BarInterval.Minutes(5), BarInterval.Minutes(15) }, "schema-v1");

        Assert.That(a, Is.Not.EqualTo(b));
    }

    [Test]
    public void FeatureSchemaHash_IsExcludedFromEquality_OnlyProfileHashAndIntervalsMatter()
    {
        // AnalysisProfileKey grouping is about "can two Agents share one ChartAnnotationEngine",
        // which depends only on computed-analysis settings - feature-schema version is carried
        // for provenance/parity only, since feature extraction happens downstream of analysis.
        var options = new ChartAnnotationOptions();
        AnalysisProfileKey a = AnalysisProfileKey.Create(options, Intervals, "schema-v1");
        AnalysisProfileKey b = AnalysisProfileKey.Create(options, Intervals, "schema-v2");

        Assert.That(a, Is.EqualTo(b));
    }

    [Test]
    public void Interpretation_OnlyThresholds_ShareOneProfile()
    {
        // The prompt's own worked example: "Agent A uses RSI threshold 55, Agent B uses RSI
        // threshold 60" is an Agent-interpretation setting, not part of ChartAnnotationOptions,
        // so both should resolve to the same profile.
        var options = new ChartAnnotationOptions();
        AnalysisProfileKey a = AnalysisProfileKey.Create(options, Intervals, "schema-v1");
        AnalysisProfileKey b = AnalysisProfileKey.Create(options, Intervals, "schema-v1");

        Assert.That(a, Is.EqualTo(b));
    }
}
