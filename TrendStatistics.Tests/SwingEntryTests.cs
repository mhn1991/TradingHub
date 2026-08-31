using NUnit.Framework;
using TrendStatistics.Detection;
using TrendStatistics.Evaluation;
using TrendStatistics.Profiles;
using TrendStatistics.Runtime;
using TrendStatistics.Segmentation;
using TrendStatistics.Statistics;
using TrendStatistics.Trading;

namespace TrendStatistics.Tests;

[TestFixture]
public sealed class SwingEntryTests
{
    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static TrendRecord Record(TrendDirection direction, decimal movePct, int bars, int endDay) => new()
    {
        Symbol = "TEST",
        Direction = direction,
        StructuralStartTime = Origin.AddMonths(endDay - 1),
        StructuralStartPrice = 100m,
        ConfirmationTime = Origin.AddMonths(endDay - 1).AddHours(8),
        ConfirmationPrice = 101m,
        EndTime = Origin.AddMonths(endDay),
        EndPrice = 100m + movePct,
        FavorableExtremeTime = Origin.AddMonths(endDay),
        FavorableExtremePrice = 100m + movePct,
        DurationBars = bars,
        DurationHours = bars * 4m,
        TotalMovePct = movePct,
        MoveAfterConfirmationPct = movePct - 1m,
        AtrNormalizedMove = movePct,
        MaximumAdverseExcursionPct = 0.5m,
        MaximumRetracementPct = 1m,
        ConfirmationDelayBars = 3,
        ConfirmationDelayPct = 1m
    };

    /// <summary>25 bull trends with moves 1..25% and durations 10..34 bars.</summary>
    private static IReadOnlyList<TrendRecord> Sample(TrendDirection direction = TrendDirection.Bullish) =>
        [.. Enumerable.Range(1, 25).Select(i => Record(direction, i, 9 + i, i))];

    private static TrendState Confirmed(TrendDirection direction, decimal movePct, int bars) => new()
    {
        Symbol = "TEST",
        Phase = TrendPhase.Confirmed,
        Direction = direction,
        StructuralStartTime = Origin,
        StructuralStartPrice = 100m,
        ConfirmationTime = Origin.AddHours(12),
        ConfirmationPrice = 101m,
        CurrentMovePct = movePct,
        DurationBars = bars,
        DurationHours = bars * 4m
    };

    [Test]
    public void Quantile_AndPercentile_AreInverses()
    {
        decimal[] sorted = QuantileCalculator.Sorted([1m, 2m, 3m, 4m, 5m]);

        Assert.Multiple(() =>
        {
            Assert.That(QuantileCalculator.Quantile(sorted, 0.5m), Is.EqualTo(3m));
            Assert.That(QuantileCalculator.Quantile(sorted, 0m), Is.EqualTo(1m));
            Assert.That(QuantileCalculator.Quantile(sorted, 1m), Is.EqualTo(5m));
            // Ties are credited at their midpoint, so 3 does not claim to beat itself.
            Assert.That(QuantileCalculator.PercentileOf(sorted, 3m), Is.EqualTo(0.5m));
            // Section 33: exceeding the sample is expected for tail trends, not impossible.
            Assert.That(QuantileCalculator.PercentileOf(sorted, 99m), Is.EqualTo(1m));
            Assert.That(QuantileCalculator.PercentileOf(sorted, 0m), Is.EqualTo(0m));
        });
    }

    [Test]
    public void Profile_SeparatesDirectionsAndNeverPoolsThem()
    {
        List<TrendRecord> mixed = [.. Sample(TrendDirection.Bullish), .. Sample(TrendDirection.Bearish)];
        SymbolTrendProfile profile = SymbolTrendProfile.Build("TEST", mixed);

        Assert.Multiple(() =>
        {
            Assert.That(profile.Bull.SampleCount, Is.EqualTo(25));
            Assert.That(profile.Bear.SampleCount, Is.EqualTo(25));
            // Section 5: 50 pooled samples would be the bug this asserts against.
            Assert.That(profile.Bull.SortedMovePct, Has.Count.EqualTo(25));
        });
    }

    [Test]
    public void Profile_RejectsWrongSymbolsAndPseudoReplication()
    {
        Assert.That(
            () => SymbolTrendProfile.Build("OTHER", Sample()),
            Throws.ArgumentException.With.Message.Contains("cannot be included"));

        TrendRecord[] clustered = [.. Enumerable.Range(1, 25)
            .Select(i => Record(TrendDirection.Bullish, i, 9 + i, i) with
            {
                StructuralStartTime = Origin,
                ConfirmationTime = Origin.AddHours(4),
                EndTime = Origin.AddDays(1),
                FavorableExtremeTime = Origin.AddDays(1)
            })];
        DirectionTrendProfile profile = SymbolTrendProfile.Build("TEST", clustered).Bull;

        Assert.Multiple(() =>
        {
            Assert.That(profile.SampleCount, Is.EqualTo(25));
            Assert.That(profile.IndependentBlockCount, Is.EqualTo(1));
            Assert.That(profile.IsReliable(20), Is.False,
                "many correlated observations in one time block are not independent evidence");
        });
    }

    [Test]
    public void BuildAsOf_ExcludesTrendsThatHadNotYetEnded()
    {
        // Section 49: a profile used at time T may only know trends that ENDED at or before T.
        IReadOnlyList<TrendRecord> trends = Sample();
        SymbolTrendProfile profile = SymbolTrendProfile.BuildAsOf("TEST", trends, Origin.AddMonths(10));

        Assert.That(profile.Bull.SampleCount, Is.EqualTo(10));
        Assert.That(profile.Bull.BuiltThrough, Is.LessThanOrEqualTo(Origin.AddMonths(10)));
    }

    [Test]
    public void Estimator_RanksAgainstItsOwnDirectionAndRefusesTheOther()
    {
        SymbolTrendProfile profile = SymbolTrendProfile.Build("TEST", Sample());
        TrendProgressEstimator estimator = new(minimumSamples: 20);

        // A 13% move sits above 12 of the 25 samples (1..25), so roughly the middle.
        TrendProgress progress = estimator.Estimate(Confirmed(TrendDirection.Bullish, 13m, 22), profile.Bull);

        Assert.Multiple(() =>
        {
            Assert.That(progress.PricePercentile, Is.EqualTo(0.5m).Within(0.02m));
            Assert.That(progress.IsReliable, Is.True);
        });

        Assert.That(() => estimator.Estimate(Confirmed(TrendDirection.Bullish, 13m, 22), profile.Bear),
            Throws.ArgumentException.With.Message.Contains("forbids"));
        Assert.That(() => estimator.Estimate(
                Confirmed(TrendDirection.Bullish, 13m, 22) with { Symbol = "OTHER" }, profile.Bull),
            Throws.ArgumentException.With.Message.Contains("cannot be ranked"));
    }

    [Test]
    public void Entry_RequiresATradableTrendPhase()
    {
        SymbolTrendProfile profile = SymbolTrendProfile.Build("TEST", Sample());
        SwingSignalGenerator generator = new(new SwingEntryOptions { EntryPercentile = 0.05m });

        foreach (TrendPhase phase in (TrendPhase[])[TrendPhase.Neutral, TrendPhase.Candidate, TrendPhase.Exhaustion])
        {
            TrendState state = Confirmed(TrendDirection.Bullish, 5m, 15) with { Phase = phase };
            Assert.That(generator.Evaluate(state, profile.Bull).Action, Is.EqualTo(SwingSignalAction.None),
                $"section 28 forbids entry in {phase}");
        }

        Assert.That(generator.Evaluate(
            Confirmed(TrendDirection.Bullish, 8m, 18) with { Phase = TrendPhase.Mature },
            profile.Bull).Action, Is.EqualTo(SwingSignalAction.Buy));
    }

    [Test]
    public void Entry_FiresOnceThePercentileThresholdIsCleared()
    {
        SymbolTrendProfile profile = SymbolTrendProfile.Build("TEST", Sample());
        SwingSignalGenerator generator = new(new SwingEntryOptions { EntryPercentile = 0.25m });

        // 2% move ranks at ~0.06 - below the P25 gate.
        Assert.That(generator.Evaluate(Confirmed(TrendDirection.Bullish, 2m, 12), profile.Bull).Action,
            Is.EqualTo(SwingSignalAction.None));

        // 8% move ranks at ~0.30 - clears it.
        SwingSignal fired = generator.Evaluate(Confirmed(TrendDirection.Bullish, 8m, 18), profile.Bull);
        Assert.That(fired.Action, Is.EqualTo(SwingSignalAction.Buy));
        Assert.That(fired.Progress.PricePercentile, Is.GreaterThanOrEqualTo(0.25m));
    }

    [Test]
    public void Entry_RefusesTrendsAlreadyInTheExhaustionZone()
    {
        SymbolTrendProfile profile = SymbolTrendProfile.Build("TEST", Sample());
        SwingSignalGenerator generator = new(new SwingEntryOptions
        {
            EntryPercentile = 0.05m,
            MaximumEntryPercentile = 0.75m
        });

        // A 24% move is near the top of the sample: entry threshold cleared, but section 32 says
        // this is a protect-profit trend, not a fresh one.
        SwingSignal signal = generator.Evaluate(Confirmed(TrendDirection.Bullish, 24m, 32), profile.Bull);

        Assert.That(signal.Action, Is.EqualTo(SwingSignalAction.None));
        Assert.That(signal.Reason, Does.Contain("exhaustion-zone"));
    }

    [Test]
    public void Entry_SellsOnConfirmedBearTrends()
    {
        SymbolTrendProfile profile = SymbolTrendProfile.Build("TEST", Sample(TrendDirection.Bearish));
        SwingSignalGenerator generator = new(new SwingEntryOptions { EntryPercentile = 0.05m });

        Assert.That(generator.Evaluate(Confirmed(TrendDirection.Bearish, 8m, 18), profile.Bear).Action,
            Is.EqualTo(SwingSignalAction.Sell));
    }

    [Test]
    public void Entry_ModesDifferOnlyByWhichDimensionMustClear()
    {
        SymbolTrendProfile profile = SymbolTrendProfile.Build("TEST", Sample());

        // Large move, short duration: price clears P25, time does not.
        TrendState state = Confirmed(TrendDirection.Bullish, 20m, 11);

        SwingSignal price = new SwingSignalGenerator(new SwingEntryOptions
        { EntryPercentile = 0.25m, Mode = SwingEntryMode.PriceOnly, MaximumEntryPercentile = 0.95m })
            .Evaluate(state, profile.Bull);
        SwingSignal time = new SwingSignalGenerator(new SwingEntryOptions
        { EntryPercentile = 0.25m, Mode = SwingEntryMode.TimeOnly, MaximumEntryPercentile = 0.95m })
            .Evaluate(state, profile.Bull);
        SwingSignal both = new SwingSignalGenerator(new SwingEntryOptions
        { EntryPercentile = 0.25m, Mode = SwingEntryMode.PriceAndTime, MaximumEntryPercentile = 0.95m })
            .Evaluate(state, profile.Bull);
        SwingSignal either = new SwingSignalGenerator(new SwingEntryOptions
        { EntryPercentile = 0.25m, Mode = SwingEntryMode.PriceOrTime, MaximumEntryPercentile = 0.95m })
            .Evaluate(state, profile.Bull);

        Assert.Multiple(() =>
        {
            Assert.That(price.Action, Is.EqualTo(SwingSignalAction.Buy));
            Assert.That(time.Action, Is.EqualTo(SwingSignalAction.None));
            Assert.That(both.Action, Is.EqualTo(SwingSignalAction.None));
            Assert.That(either.Action, Is.EqualTo(SwingSignalAction.Buy));
        });
    }

    [Test]
    public void Entry_RefusesAProfileWithTooFewSamples()
    {
        // Section 22: five trends cannot support a P05 estimate.
        SymbolTrendProfile thin = SymbolTrendProfile.Build(
            "TEST", [.. Sample().Take(5)]);
        SwingSignalGenerator generator = new(new SwingEntryOptions { MinimumTrendSamples = 20 });

        SwingSignal signal = generator.Evaluate(Confirmed(TrendDirection.Bullish, 8m, 18), thin.Bull);

        Assert.That(signal.Action, Is.EqualTo(SwingSignalAction.None));
        Assert.That(signal.Reason, Does.Contain("not yet trustworthy"));
    }
}

[TestFixture]
public sealed class BootstrapAndExhaustionTests
{
    [Test]
    public void Bootstrap_IsReproducibleForAGivenSeed()
    {
        // Section 20: every experiment must be reproducible.
        decimal[] sample = [.. Enumerable.Range(1, 60).Select(i => (decimal)i)];
        BootstrapResult first = new BootstrapEngine(seed: 42).BuildDistribution(sample, 500);
        BootstrapResult second = new BootstrapEngine(seed: 42).BuildDistribution(sample, 500);
        BootstrapResult different = new BootstrapEngine(seed: 43).BuildDistribution(sample, 500);

        Assert.Multiple(() =>
        {
            Assert.That(second.For(0.95m), Is.EqualTo(first.For(0.95m)));
            Assert.That(different.For(0.95m), Is.Not.EqualTo(first.For(0.95m)));
        });
    }

    [Test]
    public void Bootstrap_BracketsThePointEstimateAndNarrowsWithSampleSize()
    {
        // Section 17: a bigger sample should estimate its own quantiles more tightly.
        decimal[] small = [.. Enumerable.Range(1, 25).Select(i => (decimal)i)];
        decimal[] large = [.. Enumerable.Range(0, 500).Select(i => 1m + (24m * i / 499m))];

        BootstrapEngine engine = new(seed: 7);
        QuantileEstimate smallP95 = engine.BuildDistribution(small, 1_000).For(0.95m);
        QuantileEstimate largeP95 = engine.BuildDistribution(large, 1_000).For(0.95m);

        Assert.Multiple(() =>
        {
            Assert.That(smallP95.LowerBound, Is.LessThanOrEqualTo(smallP95.PointEstimate));
            Assert.That(smallP95.UpperBound, Is.GreaterThanOrEqualTo(smallP95.PointEstimate));
            Assert.That(largeP95.IntervalWidth, Is.LessThan(smallP95.IntervalWidth),
                "a 20x larger sample must estimate P95 more tightly");
        });
    }

    [Test]
    public void BlockBootstrap_ReportsIndependentTimeBlocks()
    {
        TimedObservation[] observations = [.. Enumerable.Range(0, 12).Select(index =>
            new TimedObservation(
                DateTimeOffset.MinValue.AddDays(index * 30),
                index + 1m))];

        BootstrapResult result = new BootstrapEngine([0.50m], seed: 9)
            .BuildBlockDistribution(observations, TimeSpan.FromDays(90), 200);

        Assert.Multiple(() =>
        {
            Assert.That(result.SampleSize, Is.EqualTo(12));
            Assert.That(result.ResamplingUnitCount, Is.EqualTo(4));
            Assert.That(result.BlockSpan, Is.EqualTo(TimeSpan.FromDays(90)));
        });
    }

    [Test]
    public void JointRarity_SeparatesTheTwoAsymmetricExhaustionCases()
    {
        // Section 34's two examples: far in time but not price, and far in price but not time.
        // Both are exhaustion signals and an average would flatten both to unremarkable.
        var pairs = Enumerable.Range(0, 100)
            .Select(i => ((decimal)i / 100m, (decimal)i / 100m))
            .ToArray();
        JointDistribution joint = new(pairs);

        decimal balanced = joint.Rarity(0.60m, 0.60m);
        decimal timeExtreme = joint.Rarity(0.67m, 0.97m);
        decimal priceExtreme = joint.Rarity(0.96m, 0.38m);

        Assert.Multiple(() =>
        {
            Assert.That(timeExtreme, Is.GreaterThan(balanced));
            Assert.That(priceExtreme, Is.GreaterThan(balanced));
            Assert.That(JointDistribution.Imbalance(0.67m, 0.97m), Is.EqualTo(0.30m).Within(0.001m));
        });
    }

    [Test]
    public void Exhaustion_UsesTheMaximumAxisNotTheMean()
    {
        JointDistribution joint = new(Enumerable.Range(0, 100)
            .Select(i => ((decimal)i / 100m, (decimal)i / 100m)));
        ExhaustionEstimator estimator = new();

        TrendProgress lopsided = new()
        { PricePercentile = 0.60m, TimePercentile = 0.98m, MovePct = 5m, DurationBars = 40, IsReliable = true };
        TrendProgress balanced = new()
        { PricePercentile = 0.79m, TimePercentile = 0.79m, MovePct = 5m, DurationBars = 40, IsReliable = true };

        TrendExhaustionState lop = estimator.Estimate(lopsided, joint, structuralWeakness: false);
        TrendExhaustionState bal = estimator.Estimate(balanced, joint, structuralWeakness: false);

        // Same mean percentile (0.79); the lopsided one must score as MORE exhausted.
        Assert.That(lop.ExhaustionScore, Is.GreaterThan(bal.ExhaustionScore));
        Assert.That(lop.Imbalance, Is.GreaterThan(bal.Imbalance));
    }

    [Test]
    public void PositionManager_ExitsOnStructuralInvalidationBeforeAnyProfitLogic()
    {
        // Section 37: the detector leaving the direction invalidates the thesis.
        SwingPositionManager manager = new();
        TrendState flipped = new()
        {
            Phase = TrendPhase.Confirmed,
            Direction = TrendDirection.Bearish,
            CurrentMovePct = 3m,
            DurationBars = 20
        };
        TrendExhaustionState calm = new()
        {
            PricePercentile = 0.2m, TimePercentile = 0.2m, JointRarity = 0.1m,
            Imbalance = 0m, StructuralWeakness = false, ExhaustionScore = 0.2m, Level = ExhaustionLevel.Low
        };

        SwingPositionUpdate update = manager.Update(TrendDirection.Bullish, 100m, 105m, flipped, calm);

        Assert.That(update.ExitReason, Is.EqualTo(SwingExitReason.StructuralInvalidation));
    }

    [Test]
    public void PositionManager_DoesNotHardExitAtP95ButTrailsInstead()
    {
        // Section 33: P95 is an exhaustion ZONE, not an automatic close - the tail trends may be
        // where most of the profit is.
        SwingPositionManager manager = new();
        TrendState running = new()
        {
            Phase = TrendPhase.Confirmed,
            Direction = TrendDirection.Bullish,
            CurrentMovePct = 9m,
            DurationBars = 50
        };
        TrendExhaustionState extreme = new()
        {
            PricePercentile = 0.97m, TimePercentile = 0.80m, JointRarity = 0.95m,
            Imbalance = 0.17m, StructuralWeakness = false, ExhaustionScore = 0.96m, Level = ExhaustionLevel.Extreme
        };

        // Still running strongly: trail, do not exit.
        SwingPositionUpdate held = manager.Update(TrendDirection.Bullish, 100m, 110m, running, extreme);
        Assert.Multiple(() =>
        {
            Assert.That(held.ShouldExit, Is.False, "P95 must not force a close");
            Assert.That(held.Stage, Is.EqualTo(SwingPositionStage.Trail));
        });

        // Now gives back more than the allowed fraction of the best excursion.
        SwingPositionUpdate exited = manager.Update(TrendDirection.Bullish, 100m, 104m, running, extreme);
        Assert.That(exited.ExitReason, Is.EqualTo(SwingExitReason.ExhaustionTrail));
    }

    [Test]
    public void PositionManager_UsesIntrabarMfeAndNeverRegressesOrReopens()
    {
        SwingPositionManager manager = new();
        TrendState running = new()
        {
            Phase = TrendPhase.Confirmed,
            Direction = TrendDirection.Bullish,
            CurrentMovePct = 9m,
            DurationBars = 50
        };
        TrendExhaustionState extreme = new()
        {
            PricePercentile = 0.97m, TimePercentile = 0.80m, JointRarity = 0.95m,
            Imbalance = 0.17m, StructuralWeakness = false, ExhaustionScore = 0.96m,
            Level = ExhaustionLevel.Extreme
        };
        TrendExhaustionState calm = extreme with
        {
            PricePercentile = 0.20m, TimePercentile = 0.20m, JointRarity = 0.10m,
            ExhaustionScore = 0.20m, Level = ExhaustionLevel.Low
        };

        SwingPositionUpdate trail = manager.Update(
            TrendDirection.Bullish, 100m, 110m, 115m, 99m, running, extreme);
        SwingPositionUpdate exited = manager.Update(
            TrendDirection.Bullish, 100m, 106m, 107m, 105m, running, calm);
        SwingPositionUpdate repeated = manager.Update(
            TrendDirection.Bullish, 100m, 120m, 121m, 119m, running, calm);

        Assert.Multiple(() =>
        {
            Assert.That(trail.BestExcursionPct, Is.EqualTo(15m));
            Assert.That(trail.Stage, Is.EqualTo(SwingPositionStage.Trail));
            Assert.That(exited.ExitReason, Is.EqualTo(SwingExitReason.ExhaustionTrail),
                "the trail must remain armed even after the score falls");
            Assert.That(repeated.Stage, Is.EqualTo(SwingPositionStage.Exit));
            Assert.That(repeated.ExitReason, Is.EqualTo(SwingExitReason.ExhaustionTrail));
        });
    }

    [Test]
    public void Metrics_ComputeCaptureEntryDelayAndGivebackInConsistentUnits()
    {
        // Trend runs 100 -> 120. Entered at 105, exited at 115.
        SwingTrade trade = new(
            TrendDirection.Bullish,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), 105m,
            new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero), 115m,
            TrendStructuralStartPrice: 100m, TrendFavorableExtremePrice: 120m, BarsHeld: 24);

        Assert.Multiple(() =>
        {
            Assert.That(trade.TrendCaptureRatio, Is.EqualTo(2m / 3m).Within(0.001m)); // 10 of 15 available
            Assert.That(trade.EntryDelay, Is.EqualTo(0.25m).Within(0.001m));          // 5 of 20 elapsed
            Assert.That(trade.ExitGiveback, Is.EqualTo(4.7619m).Within(0.001m));      // percentage points
        });
    }

    [Test]
    public void Metrics_PreserveNegativeCaptureAndCompoundReturns()
    {
        DateTimeOffset origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        SwingTrade loss = new(
            TrendDirection.Bullish,
            origin, 100m,
            origin.AddDays(1), 95m,
            TrendStructuralStartPrice: 90m,
            TrendFavorableExtremePrice: 110m,
            BarsHeld: 6,
            PostEntryFavorableExtremePrice: 110m);
        SwingTrade win = loss with
        {
            EntryTime = origin.AddDays(2), ExitTime = origin.AddDays(3),
            EntryPrice = 100m, ExitPrice = 110m
        };

        TrendMetricsReport report = TrendMetricsReport.From([loss, win]);

        Assert.Multiple(() =>
        {
            Assert.That(loss.TrendCaptureRatio, Is.EqualTo(-0.5m));
            Assert.That(report.NetPct, Is.EqualTo(4.5m).Within(0.001m));
            Assert.That(report.MaximumDrawdownPct, Is.EqualTo(5m).Within(0.001m));
        });
    }

    [Test]
    public void CandleAggregator_FoldsAndDropsThePartialTail()
    {
        List<TrendStatistics.Data.Candle> source = [];
        DateTimeOffset time = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (int i = 0; i < 10; i++)
        {
            source.Add(new TrendStatistics.Data.Candle
            {
                Symbol = "T", OpenTime = time, Open = 100 + i, High = 105 + i, Low = 95 + i, Close = 102 + i
            });
            time = time.AddHours(1);
        }

        // 10 hourly candles -> two complete 4H buckets; the trailing 2 hours are dropped.
        IReadOnlyList<TrendStatistics.Data.Candle> folded =
            TrendStatistics.Data.CandleAggregator.Aggregate(source, TimeSpan.FromHours(4));

        Assert.That(folded, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(folded[0].Open, Is.EqualTo(100m));
            Assert.That(folded[0].High, Is.EqualTo(108m));
            Assert.That(folded[0].Close, Is.EqualTo(105m));
        });
    }

    [Test]
    public void CandleAggregator_DropsAnIncompleteMiddleBucket()
    {
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        TrendStatistics.Data.Candle[] source = [.. Enumerable.Range(0, 12)
            .Where(index => index != 6)
            .Select(index => new TrendStatistics.Data.Candle
            {
                Symbol = "T", OpenTime = start.AddHours(index),
                Open = 100m, High = 101m, Low = 99m, Close = 100m
            })];

        IReadOnlyList<TrendStatistics.Data.Candle> folded =
            TrendStatistics.Data.CandleAggregator.Aggregate(source, TimeSpan.FromHours(4));

        Assert.That(folded.Select(candle => candle.OpenTime), Is.EqualTo(new[]
        {
            start,
            start.AddHours(8)
        }));
    }
}

[TestFixture]
public sealed class WalkForwardAndRegimeTests
{
    private static IReadOnlyList<TrendStatistics.Data.Candle> Series(int bars, int seed = 5)
    {
        Random random = new(seed);
        List<TrendStatistics.Data.Candle> candles = [];
        DateTimeOffset time = new(2015, 1, 1, 0, 0, 0, TimeSpan.Zero);
        decimal price = 1000m;

        for (int i = 0; i < bars; i++)
        {
            // Alternating 120-bar regimes so the series contains genuine trends in both directions.
            decimal drift = (i / 120) % 2 == 0 ? 1.2m : -1.0m;
            decimal step = drift + (decimal)((random.NextDouble() - 0.5) * 6);
            decimal close = Math.Max(50m, price + step);
            candles.Add(new TrendStatistics.Data.Candle
            {
                Symbol = "TEST",
                OpenTime = time,
                Open = price,
                High = Math.Max(price, close) + 1m,
                Low = Math.Min(price, close) - 1m,
                Close = close
            });
            price = close;
            time = time.AddHours(4);
        }

        return candles;
    }

    [Test]
    public void RegimeClassifier_SplitsIntoTercilesAndIsCausal()
    {
        List<TrendRecord> trends = [];
        for (int i = 1; i <= 30; i++)
        {
            trends.Add(new TrendRecord
            {
                Symbol = "T", Direction = TrendDirection.Bullish,
                StructuralStartTime = DateTimeOffset.UnixEpoch.AddMonths(i - 1), StructuralStartPrice = 100m,
                ConfirmationTime = DateTimeOffset.UnixEpoch.AddMonths(i - 1), ConfirmationPrice = 101m,
                EndTime = DateTimeOffset.UnixEpoch.AddMonths(i), EndPrice = 102m,
                FavorableExtremeTime = DateTimeOffset.UnixEpoch.AddMonths(i), FavorableExtremePrice = 103m,
                DurationBars = 10, DurationHours = 40m, TotalMovePct = 2m,
                MoveAfterConfirmationPct = 1m, AtrNormalizedMove = i,
                VolatilityPctAtConfirmation = i,
                MaximumAdverseExcursionPct = 0.5m, MaximumRetracementPct = 1m,
                ConfirmationDelayBars = 2, ConfirmationDelayPct = 0.5m
            });
        }

        RegimeClassifier classifier = RegimeClassifier.Fit(trends);

        Assert.Multiple(() =>
        {
            Assert.That(classifier.Classify(1m), Is.EqualTo(VolatilityRegime.Low));
            Assert.That(classifier.Classify(15m), Is.EqualTo(VolatilityRegime.Normal));
            Assert.That(classifier.Classify(30m), Is.EqualTo(VolatilityRegime.High));
            Assert.That(classifier.Classify(0m), Is.EqualTo(VolatilityRegime.Unknown));
            // An unfitted classifier must not silently claim a regime.
            Assert.That(RegimeClassifier.Fit([]).Classify(5m), Is.EqualTo(VolatilityRegime.Unknown));
        });
    }

    [Test]
    public void ProfileRepository_FallsBackWhenTheConditionedProfileIsTooThin()
    {
        // Section 60 fragments the population; the fallback is what stops that being harmful.
        List<TrendRecord> trends = [];
        for (int i = 1; i <= 60; i++)
        {
            trends.Add(new TrendRecord
            {
                Symbol = "T", Direction = TrendDirection.Bullish,
                StructuralStartTime = DateTimeOffset.UnixEpoch.AddMonths(i - 1), StructuralStartPrice = 100m,
                ConfirmationTime = DateTimeOffset.UnixEpoch.AddMonths(i - 1), ConfirmationPrice = 101m,
                EndTime = DateTimeOffset.UnixEpoch.AddMonths(i), EndPrice = 102m,
                FavorableExtremeTime = DateTimeOffset.UnixEpoch.AddMonths(i), FavorableExtremePrice = 103m,
                DurationBars = 10, DurationHours = 40m, TotalMovePct = i,
                MoveAfterConfirmationPct = 1m, AtrNormalizedMove = i,
                VolatilityPctAtConfirmation = i,
                MaximumAdverseExcursionPct = 0.5m, MaximumRetracementPct = 1m,
                ConfirmationDelayBars = 2, ConfirmationDelayPct = 0.5m
            });
        }

        RegimeClassifier classifier = RegimeClassifier.Fit(trends);
        ProfileRepository repository = ProfileRepository.Build("T", trends, classifier);

        ProfileKey high = new("T", TrendDirection.Bullish, VolatilityRegime.High);

        // 20 conditioned samples: served when the bar is 20, refused (falls back) when it is 40.
        Assert.That(repository.GetWithFallback(high, 20)!.SampleCount, Is.EqualTo(20));
        Assert.That(repository.GetWithFallback(high, 40)!.SampleCount, Is.EqualTo(60),
            "too-thin conditioned profile must fall back to the unconditional one");
    }

    [Test]
    public void Backtester_OnlyTradesInsideItsTestWindow()
    {
        IReadOnlyList<TrendStatistics.Data.Candle> candles = Series(3000);
        TrendDetectorConfig config = new();
        IReadOnlyList<TrendRecord> trends = new TrendSegmenter(config).Segment(candles);
        Assume.That(trends.Count, Is.GreaterThan(10), "fixture must produce trends");

        ProfileRepository frozen = ProfileRepository.Build("TEST", trends);
        DateTimeOffset from = candles[1500].OpenTime;
        DateTimeOffset to = candles[2400].OpenTime;

        IReadOnlyList<SwingTrade> trades = new TrendBacktester(config).Run(
            candles, frozen, null,
            new SwingEntryOptions { EntryPercentile = 0.05m, MinimumTrendSamples = 5 },
            from, to, minimumSamples: 5);

        Assert.Multiple(() =>
        {
            Assert.That(trades, Is.Not.Empty, "fixture must exercise at least one entry");
            foreach (SwingTrade trade in trades)
                Assert.That(trade.EntryTime, Is.InRange(from, to), "entries must respect the test window");
        });
    }

    [Test]
    public void Backtester_CostsReduceReturns()
    {
        IReadOnlyList<TrendStatistics.Data.Candle> candles = Series(3000);
        TrendDetectorConfig config = new();
        ProfileRepository frozen = ProfileRepository.Build(
            "TEST", new TrendSegmenter(config).Segment(candles));
        SwingEntryOptions options = new() { EntryPercentile = 0.05m, MinimumTrendSamples = 5 };
        DateTimeOffset from = candles[0].OpenTime;
        DateTimeOffset to = candles[^1].OpenTime;

        var free = TrendMetricsReport.From(new TrendBacktester(config)
            .Run(candles, frozen, null, options, from, to, 5));
        var costed = TrendMetricsReport.From(new TrendBacktester(config, new TrendCostModel { RoundTripPct = 0.5m })
            .Run(candles, frozen, null, options, from, to, 5));

        Assert.That(free.Trades, Is.GreaterThan(0), "fixture must exercise the cost model");
        Assert.That(costed.NetPct, Is.LessThan(free.NetPct), "costs must reduce net return");
    }

    [Test]
    public void WalkForward_SelectsThresholdsWithoutSeeingItsTestWindow()
    {
        IReadOnlyList<TrendStatistics.Data.Candle> candles = Series(3000);
        WalkForwardRunner runner = new(new TrendDetectorConfig(), minimumSamples: 10);

        WalkForwardReport report = runner.Run(
            candles, "TEST",
            testSpan: TimeSpan.FromDays(120),
            validationSpan: TimeSpan.FromDays(120),
            minimumTrainSpan: TimeSpan.FromDays(240));

        Assert.That(report.Windows, Is.Not.Empty, "the series should support several windows");
        Assert.Multiple(() =>
        {
            for (int i = 1; i < report.Windows.Count; i++)
            {
                // Section 48: test periods tile forward and never overlap.
                Assert.That(report.Windows[i].TestFrom, Is.GreaterThanOrEqualTo(report.Windows[i - 1].TestTo));
            }
            // The training library must grow monotonically as windows advance.
            for (int i = 1; i < report.Windows.Count; i++)
                Assert.That(report.Windows[i].TrainingTrends,
                    Is.GreaterThanOrEqualTo(report.Windows[i - 1].TrainingTrends));
        });
    }

    [Test]
    public void WalkForward_SuccessBarRequiresMoreThanAPositiveTotal()
    {
        // Section 69 opens by rejecting "net > 0" as the test. A report carried entirely by one
        // window must not pass.
        TrendMetricsReport strong = TrendMetricsReport.From([
            new SwingTrade(TrendDirection.Bullish, DateTimeOffset.UnixEpoch, 100m,
                DateTimeOffset.UnixEpoch.AddDays(1), 150m, 100m, 150m, 10)]);
        TrendMetricsReport weak = TrendMetricsReport.From([
            new SwingTrade(TrendDirection.Bullish, DateTimeOffset.UnixEpoch, 100m,
                DateTimeOffset.UnixEpoch.AddDays(1), 99m, 100m, 105m, 10)]);

        WalkForwardReport concentrated = new()
        {
            Symbol = "T",
            Windows = [
                new WalkForwardWindow { Index = 1, TrainThrough = DateTimeOffset.UnixEpoch,
                    TestFrom = DateTimeOffset.UnixEpoch, TestTo = DateTimeOffset.UnixEpoch,
                    TrainingTrends = 50, SelectedEntryPercentile = 0.1m,
                    SelectedMode = SwingEntryMode.TimeOnly, Test = strong },
                new WalkForwardWindow { Index = 2, TrainThrough = DateTimeOffset.UnixEpoch,
                    TestFrom = DateTimeOffset.UnixEpoch, TestTo = DateTimeOffset.UnixEpoch,
                    TrainingTrends = 60, SelectedEntryPercentile = 0.1m,
                    SelectedMode = SwingEntryMode.TimeOnly, Test = weak }
            ],
            Pooled = strong,
            RegimeConditioned = false
        };

        Assert.Multiple(() =>
        {
            Assert.That(concentrated.IsConcentrated, Is.True);
            Assert.That(concentrated.MeetsSuccessBar, Is.False, "one window carrying everything must fail");
        });
    }
}
