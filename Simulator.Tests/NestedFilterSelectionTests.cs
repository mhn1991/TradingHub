using NUnit.Framework;
using TradingClassifier.Configuration;
using TradingClassifier.Dataset;
using TradingClassifier.Features;
using TradingClassifier.ML.Experiments;
using TradingClassifier.ML.Training;
using TradingClassifier.Models;

namespace Simulator.Tests;

/// <summary>
/// V2 Phase 0b: `MetaFilter.SelectNested` is the difference between a gate and a sweep, so the
/// properties that make it a gate are pinned here.
/// <para>
/// The one thing it must never do is let the test period influence what rule is chosen. That is
/// hard to assert directly without controlling the model's output, so it is pinned from the other
/// side: raise the validation guard beyond what any validation window can satisfy and the accepted
/// set must collapse to empty. If selection were peeking at the test period, a rule would still be
/// found.
/// </para>
/// </summary>
[TestFixture]
public sealed class NestedFilterSelectionTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public void SelectNested_PartitionsCandidatesAndNeverAcceptsOutsideTheBaseline()
    {
        NestedFilterResult result = Run(minimumValidationKept: 1);

        int[] baselineNumbers = [.. result.Baseline.Select(item => item.Trade.Number)];
        int[] acceptedNumbers = [.. result.Accepted.Select(item => item.Trade.Number)];

        Assert.Multiple(() =>
        {
            Assert.That(result.Baseline, Is.Not.Empty, "The fixture must cover at least one fold.");

            // Every accepted candidate is one the baseline also counted: same denominator, so the
            // two columns are comparable.
            Assert.That(acceptedNumbers, Is.SubsetOf(baselineNumbers));

            // A candidate belongs to exactly one fold - otherwise pooled PF double-counts.
            Assert.That(baselineNumbers, Is.Unique);
            Assert.That(acceptedNumbers, Is.Unique);

            // Coverage accounting must close.
            Assert.That(result.Uncovered, Is.EqualTo(TradeCount - result.Baseline.Count));

            foreach (FoldSelection fold in result.Folds)
            {
                Assert.That(fold.TestKept, Is.LessThanOrEqualTo(fold.TestCandidates));
                Assert.That(fold.ValidationKept, Is.GreaterThanOrEqualTo(0));
            }
        });
    }

    [Test]
    public void SelectNested_WhenNoRuleClearsTheValidationGuard_AcceptsNothing()
    {
        // No validation window holds this many candidates, so no rule is eligible anywhere.
        NestedFilterResult result = Run(minimumValidationKept: 100_000);

        Assert.Multiple(() =>
        {
            Assert.That(result.Baseline, Is.Not.Empty,
                "The baseline must still be populated - the guard governs selection, not coverage.");
            Assert.That(result.Accepted, Is.Empty,
                "With no eligible validation rule the fold must take nothing; a non-empty accepted " +
                "set would mean the test period selected the rule.");
            Assert.That(result.Folds.Select(fold => fold.Rule), Has.All.Null);
            Assert.That(result.AcceptedReport.Trades, Is.Zero);
        });
    }

    // ---- fixture --------------------------------------------------------------------------------

    private const int TradeCount = 60;

    private static NestedFilterResult Run(int minimumValidationKept)
    {
        ClassifierOptions options = new() { PredictionHorizon = 5, EnabledGroups = FeatureGroups.Experiment1 };
        IReadOnlyList<ClassifierCandle> candles = Series(count: 6_000);

        ClassifierDataset dataset = new DatasetBuilder(options with { EnabledGroups = FeatureGroups.Experiment1 })
            .Build(candles);

        return MetaFilter.SelectNested(
            Trades(candles),
            [("ohlc", dataset)],
            options,
            () => new LogisticRegressionBaselineTrainer(),
            trainSpan: TimeSpan.FromHours(20),
            validationSpan: TimeSpan.FromHours(6),
            testSpan: TimeSpan.FromHours(6),
            minimumValidationKept: minimumValidationKept);
    }

    /// <summary>A wandering 1m series with enough movement to produce all three labels.</summary>
    private static IReadOnlyList<ClassifierCandle> Series(int count)
    {
        List<ClassifierCandle> candles = [];
        Random random = new(20260830);
        decimal close = 2_000m;
        for (int index = 0; index < count; index++)
        {
            decimal step = (decimal)((random.NextDouble() - 0.5) * 2.0);
            decimal open = close;
            close = Math.Round(open + step, 3);
            decimal high = Math.Max(open, close) + 0.4m;
            decimal low = Math.Min(open, close) - 0.4m;
            candles.Add(new ClassifierCandle(Start.AddMinutes(index), open, high, low, close));
        }
        return candles;
    }

    /// <summary>Evenly spaced primary trades so several land in every window.</summary>
    private static IReadOnlyList<PrimaryTrade> Trades(IReadOnlyList<ClassifierCandle> candles)
    {
        List<PrimaryTrade> trades = [];
        int stride = candles.Count / (TradeCount + 1);
        for (int index = 0; index < TradeCount; index++)
        {
            ClassifierCandle candle = candles[(index + 1) * stride];
            trades.Add(new PrimaryTrade(
                Number: index + 1,
                Side: index % 2 == 0 ? TradeLabel.Buy : TradeLabel.Sell,
                SignalAt: candle.Timestamp,
                Opened: candle.Timestamp.AddMinutes(1),
                Closed: candle.Timestamp.AddMinutes(30),
                R: index % 3 == 0 ? 1.5m : -1m,
                Net: index % 3 == 0 ? 150m : -100m));
        }
        return trades;
    }
}
