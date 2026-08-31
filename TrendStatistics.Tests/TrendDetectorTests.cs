using TrendStatistics.Data;
using TrendStatistics.Detection;
using TrendStatistics.Segmentation;

namespace TrendStatistics.Tests;

[TestFixture]
public sealed class TrendDetectorTests
{
    [Test]
    public void BullishSequence_TransitionsCausallyAndProducesCompletedRecord()
    {
        Candle[] candles = BullishTrendWithReversal();
        var detector = new TrendDetector(TestConfig());
        var updates = candles.Select(detector.Apply).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(updates[3].State.Phase, Is.EqualTo(TrendPhase.Neutral));
            Assert.That(updates[4].State.Phase, Is.EqualTo(TrendPhase.Candidate));
            Assert.That(updates[4].State.Direction, Is.EqualTo(TrendDirection.Bullish));
            Assert.That(updates[4].State.ConfirmationTime, Is.Null);
            Assert.That(updates[5].State.Phase, Is.EqualTo(TrendPhase.Confirmed));
            Assert.That(updates[5].State.ConfirmationTime, Is.EqualTo(CloseTime(candles[5])));
            Assert.That(updates[8].State.Phase, Is.EqualTo(TrendPhase.Neutral));
        });

        TrendRecord record = updates[8].CompletedTrend!;
        Assert.Multiple(() =>
        {
            Assert.That(record, Is.Not.Null);
            Assert.That(record.Symbol, Is.EqualTo("XAUUSD"));
            Assert.That(record.Direction, Is.EqualTo(TrendDirection.Bullish));
            Assert.That(record.StructuralStartTime, Is.EqualTo(candles[0].OpenTime));
            Assert.That(record.StructuralStartPrice, Is.EqualTo(candles[0].Low));
            Assert.That(record.ConfirmationTime, Is.EqualTo(CloseTime(candles[5])));
            Assert.That(record.ConfirmationPrice, Is.EqualTo(candles[5].Close));
            Assert.That(record.EndTime, Is.EqualTo(CloseTime(candles[8])));
            Assert.That(record.FavorableExtremePrice, Is.EqualTo(candles[8].High));
            Assert.That(record.DurationBars, Is.EqualTo(9));
            Assert.That(record.DurationHours, Is.EqualTo(36m));
            Assert.That(record.ConfirmationDelayBars, Is.EqualTo(6));
            Assert.That(record.TotalMovePct, Is.GreaterThan(0m));
            Assert.That(record.MoveAfterConfirmationPct, Is.GreaterThan(0m));
            Assert.That(record.AtrNormalizedMove, Is.GreaterThan(0m));
            Assert.That(record.MaximumRetracementPct, Is.GreaterThan(0m));
        });
    }

    [Test]
    public void BearishSequence_ProducesPositiveMagnitudeStatistics()
    {
        Candle[] candles = BearishTrendWithReversal();
        TrendRecord[] records = new TrendSegmenter(TestConfig())
            .Segment(candles)
            .ToArray();

        Assert.That(records, Has.Length.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(records[0].Direction, Is.EqualTo(TrendDirection.Bearish));
            Assert.That(records[0].TotalMovePct, Is.GreaterThan(0m));
            Assert.That(records[0].MoveAfterConfirmationPct, Is.GreaterThan(0m));
            // Adverse excursion is >= 0 by construction and is NOT a duplicate of the move after
            // confirmation - the field it replaced was.
            Assert.That(records[0].MaximumAdverseExcursionPct, Is.GreaterThanOrEqualTo(0m));
            Assert.That(records[0].ConfirmationDelayPct, Is.GreaterThan(0m));
            Assert.That(records[0].EndPrice, Is.LessThan(records[0].StructuralStartPrice));
        });
    }

    [Test]
    public void PrefixState_DoesNotChangeWhenFutureCandlesAreAppended()
    {
        Candle[] candles = BullishTrendWithReversal();
        var prefixDetector = new TrendDetector(TestConfig());
        TrendDetectorUpdate prefix = candles.Take(6).Select(prefixDetector.Apply).ToArray()[^1];

        var fullDetector = new TrendDetector(TestConfig());
        TrendDetectorUpdate samePoint = candles.Take(6).Select(fullDetector.Apply).ToArray()[^1];
        _ = candles.Skip(6).Select(fullDetector.Apply).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(samePoint, Is.EqualTo(prefix));
            Assert.That(prefix.State.Phase, Is.EqualTo(TrendPhase.Confirmed));
            Assert.That(prefix.CompletedTrend, Is.Null);
        });
    }

    [Test]
    public void Segmenter_DoesNotForceCloseTrendAtDatasetBoundary()
    {
        Candle[] stillActive = BullishTrendWithReversal()[..8];

        IReadOnlyList<TrendRecord> records = new TrendSegmenter(TestConfig()).Segment(stillActive);

        Assert.That(records, Is.Empty);
    }

    [Test]
    public void Detector_RejectsIncompleteOutOfOrderAndMixedSymbolCandles()
    {
        Candle first = CreateCandle(0, 100m, 100.5m, 99.5m, 100m);

        Assert.That(
            () => new TrendDetector(TestConfig()).Apply(first with { IsComplete = false }),
            Throws.ArgumentException.With.Message.Contains("completed"));

        var detector = new TrendDetector(TestConfig());
        detector.Apply(first);
        Assert.Multiple(() =>
        {
            Assert.That(
                () => detector.Apply(first),
                Throws.ArgumentException.With.Message.Contains("increasing"));
            Assert.That(
                () => detector.Apply(CreateCandle(1, 100m, 100.5m, 99.5m, 100m) with
                {
                    Symbol = "EURUSD"
                }),
                Throws.ArgumentException.With.Message.Contains("one symbol"));
        });
    }

    [Test]
    public void Config_RejectsThresholdsThatReverseLifecycleOrder()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                () => new TrendDetector(new TrendDetectorConfig
                {
                    CandidateMinDisplacementAtr = 2m,
                    ConfirmationMinDisplacementAtr = 1m
                }),
                Throws.ArgumentException);
            Assert.That(
                () => new TrendDetector(new TrendDetectorConfig
                {
                    ExhaustionRetracementAtr = 2m,
                    EndRetracementAtr = 1m
                }),
                Throws.ArgumentException);
        });
    }

    private static TrendDetectorConfig TestConfig() => new()
    {
        EmaPeriod = 3,
        EmaSlopeLookbackBars = 1,
        AtrPeriod = 3,
        ExtremeLookbackBars = 4,
        StructureLookbackBars = 2,
        CandidateTimeoutBars = 4,
        MatureAfterBars = 3,
        CandidateMinDisplacementAtr = 0.5m,
        ConfirmationMinDisplacementAtr = 0.8m,
        ExhaustionRetracementAtr = 0.25m,
        EndRetracementAtr = 0.5m
    };

    private static Candle[] BullishTrendWithReversal() =>
    [
        CreateCandle(0, 100m, 100.4m, 99.8m, 100m),
        CreateCandle(1, 100m, 100.5m, 99.9m, 100.2m),
        CreateCandle(2, 100.2m, 100.7m, 100.1m, 100.5m),
        CreateCandle(3, 100.5m, 100.9m, 100.4m, 100.8m),
        CreateCandle(4, 100.8m, 101.3m, 100.7m, 101.1m),
        CreateCandle(5, 101.1m, 102.3m, 101m, 102.1m),
        CreateCandle(6, 102.1m, 103.3m, 102m, 103.1m),
        CreateCandle(7, 103.1m, 104.3m, 103m, 104.1m),
        CreateCandle(8, 104.1m, 104.4m, 101.8m, 101.9m)
    ];

    private static Candle[] BearishTrendWithReversal() =>
    [
        CreateCandle(0, 100m, 100.3m, 99.7m, 100m),
        CreateCandle(1, 100m, 100.1m, 99.5m, 99.8m),
        CreateCandle(2, 99.8m, 99.9m, 99.2m, 99.5m),
        CreateCandle(3, 99.5m, 99.6m, 98.9m, 99.2m),
        CreateCandle(4, 99.2m, 99.3m, 98.6m, 98.9m),
        CreateCandle(5, 98.9m, 99m, 97.7m, 97.9m),
        CreateCandle(6, 97.9m, 98m, 96.7m, 96.9m),
        CreateCandle(7, 96.9m, 97m, 95.7m, 95.9m),
        CreateCandle(8, 95.9m, 98.4m, 95.8m, 98.3m)
    ];

    private static Candle CreateCandle(
        int index,
        decimal open,
        decimal high,
        decimal low,
        decimal close) => new()
        {
            Symbol = "XAUUSD",
            OpenTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                .AddHours(index * 4),
            Open = open,
            High = high,
            Low = low,
            Close = close
        };

    private static DateTimeOffset CloseTime(Candle candle) => candle.OpenTime.AddHours(4);

    [Test]
    public void MaximumAdverseExcursion_IsNotADuplicateOfMoveAfterConfirmation()
    {
        // The field this replaced was assigned the identical value as MoveAfterConfirmationPct.
        // Drive a trend that pulls back below its confirmation price and then recovers: adverse
        // excursion must be strictly positive while the move after confirmation is also positive,
        // and the two must differ.
        var config = new TrendDetectorConfig
        {
            Timeframe = TimeSpan.FromHours(4),
            EmaPeriod = 5,
            AtrPeriod = 5,
            ExtremeLookbackBars = 4,
            StructureLookbackBars = 2,
            CandidateTimeoutBars = 12,
            MatureAfterBars = 6,
            CandidateMinDisplacementAtr = 0.5m,
            ConfirmationMinDisplacementAtr = 0.9m,
            ExhaustionRetracementAtr = 0.75m,
            EndRetracementAtr = 1.0m
        };

        var candles = new List<Candle>();
        DateTimeOffset time = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        decimal price = 100m;

        void Add(decimal open, decimal high, decimal low, decimal close)
        {
            candles.Add(new Candle
            {
                Symbol = "TEST", OpenTime = time, Open = open, High = high, Low = low, Close = close
            });
            time = time.AddHours(4);
        }

        for (int i = 0; i < 10; i++) { Add(price, price + 0.3m, price - 0.3m, price); }        // base
        for (int i = 0; i < 10; i++) { Add(price, price + 2.2m, price - 0.2m, price + 2m); price += 2m; }  // rally
        for (int i = 0; i < 4; i++) { Add(price, price + 0.2m, price - 2.2m, price - 2m); price -= 2m; }   // dip below entry
        for (int i = 0; i < 10; i++) { Add(price, price + 2.2m, price - 0.2m, price + 2m); price += 2m; }  // recover higher
        for (int i = 0; i < 12; i++) { Add(price, price + 0.2m, price - 3.2m, price - 3m); price -= 3m; }  // end

        IReadOnlyList<TrendRecord> records = new TrendSegmenter(config).Segment(candles);

        Assert.That(records, Is.Not.Empty, "the fixture should produce at least one completed trend");
        TrendRecord bull = records.First(r => r.Direction == TrendDirection.Bullish);

        Assert.Multiple(() =>
        {
            Assert.That(bull.MaximumAdverseExcursionPct, Is.GreaterThan(0m),
                "price traded below the confirmation price, so adverse excursion must be positive");
            Assert.That(bull.MaximumAdverseExcursionPct, Is.Not.EqualTo(bull.MoveAfterConfirmationPct),
                "the replaced field was a duplicate of MoveAfterConfirmationPct");
        });
    }
}
