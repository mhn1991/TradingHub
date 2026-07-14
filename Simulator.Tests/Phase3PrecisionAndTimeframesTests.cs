using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Brokers.Models;
using ChartAnnotator.MarketData;
using Simulator.Abstractions;
using Simulator.Engine;
using Simulator.MarketData;
using Simulator.Models;

namespace Simulator.Tests;

[TestFixture]
public sealed class Phase3PrecisionAndTimeframesTests
{
    [Test]
    public void BarIntervalParser_ParsesSecondsAndFormats()
    {
        Assert.That(BarIntervalParser.Parse("1s"), Is.EqualTo(BarInterval.Seconds(1)));
        Assert.That(BarIntervalParser.Parse("5s"), Is.EqualTo(BarInterval.Seconds(5)));
        Assert.That(BarIntervalParser.Parse("3m"), Is.EqualTo(BarInterval.Minutes(3)));
        Assert.That(BarIntervalParser.Parse("2h"), Is.EqualTo(BarInterval.Hours(2)));
        Assert.That(BarIntervalParser.Format(BarInterval.Seconds(5)), Is.EqualTo("5s"));
    }

    [Test]
    public void Oanda_Rejects_1s_Execution()
    {
        var ex = Assert.Throws<HistoricalGranularityNotSupportedException>(() =>
            HistoricalCapabilityRegistry.ValidateExecutionInterval(
                HistoricalDataSourceKind.OandaCandles,
                BarInterval.Seconds(1)));
        Assert.That(ex!.ErrorCode, Is.EqualTo("HistoricalGranularityNotSupported"));
        Assert.That(ex.Message, Does.Contain("5s"));
    }

    [Test]
    public void Oanda_Accepts_5s_Execution()
    {
        Assert.DoesNotThrow(() =>
            HistoricalCapabilityRegistry.ValidateExecutionInterval(
                HistoricalDataSourceKind.OandaCandles,
                BarInterval.Seconds(5)));
    }

    [Test]
    public void Binance_Accepts_1s_AndRejectsUnsupported_5s_Execution()
    {
        Assert.DoesNotThrow(() =>
            HistoricalCapabilityRegistry.ValidateExecutionInterval(
                HistoricalDataSourceKind.BinanceCandles,
                BarInterval.Seconds(1)));
        Assert.Throws<HistoricalGranularityNotSupportedException>(() =>
            HistoricalCapabilityRegistry.ValidateExecutionInterval(
                HistoricalDataSourceKind.BinanceCandles,
                BarInterval.Seconds(5)));
    }

    [Test]
    public void Inline_Accepts_1s_Execution()
    {
        Assert.DoesNotThrow(() =>
            HistoricalCapabilityRegistry.ValidateExecutionInterval(
                HistoricalDataSourceKind.InlineTestData,
                BarInterval.Seconds(1)));
    }

    [Test]
    public void ImportedMidpointCsv_DoesNotAdvertiseBidAsk_AndRecordedQuotesRemainUnavailable()
    {
        Assert.That(ImportedSecondCandleCapabilities.Instance.SupportsHistoricalBidAsk, Is.False);
        Assert.Throws<HistoricalGranularityNotSupportedException>(() =>
            HistoricalCapabilityRegistry.ValidateExecutionInterval(
                HistoricalDataSourceKind.RecordedQuotes,
                BarInterval.Seconds(1)));
    }

    [Test]
    public void Runtime_Oanda_1s_Fails_Validate()
    {
        var runtime = new BacktestRuntimeOptions
        {
            ExecutionInterval = BarInterval.Seconds(1),
            AnalysisBaseInterval = BarInterval.Minutes(1),
            AnalysisIntervals = [BarInterval.Minutes(5)],
            SourceKind = HistoricalDataSourceKind.OandaCandles,
            PrefetchCapacity = 20_000,
            PrefetchLowWatermark = 5_000,
            SourcePageSize = 5_000
        };
        Assert.Throws<HistoricalGranularityNotSupportedException>(() => runtime.Validate(1));
    }

    [Test]
    public void Timeframes_Reject_Entry_Coarser_Than_Confirmation()
    {
        var tf = new ProgressiveStrategyTimeframes
        {
            TrendInterval = BarInterval.Hours(1),
            ConfirmationInterval = BarInterval.Minutes(15),
            EntryInterval = BarInterval.Minutes(30)
        };
        Assert.Throws<ArgumentException>(() => tf.Validate());
    }

    [Test]
    public void EffectiveAnalysisIntervals_Unions_Strategy_Requirements()
    {
        var options = new SimulationTimeframeOptions
        {
            ExecutionInterval = BarInterval.Minutes(1),
            AnalysisBaseInterval = BarInterval.Minutes(1),
            AnalysisIntervals = [BarInterval.Minutes(5), BarInterval.Hours(1)]
        };
        IReadOnlyList<BarInterval> effective = options.EffectiveAnalysisIntervals(
        [
            BarInterval.Minutes(3),
            BarInterval.Minutes(30),
            BarInterval.Hours(2)
        ]);
        Assert.That(effective, Does.Contain(BarInterval.Minutes(1)));
        Assert.That(effective, Does.Contain(BarInterval.Minutes(3)));
        Assert.That(effective, Does.Contain(BarInterval.Minutes(30)));
        Assert.That(effective, Does.Contain(BarInterval.Hours(2)));
    }

    [Test]
    public void AnalysisBaseAggregator_Aggregates_12x5s_To_1m_Exact_Ohlcv()
    {
        InstrumentKey instrument = new("FX:EUR/USD");
        DateTimeOffset start = new(2025, 6, 2, 10, 0, 0, TimeSpan.Zero);
        var agg = new AnalysisBaseAggregator(
            instrument,
            BarInterval.Seconds(5),
            BarInterval.Minutes(1),
            BaseCandleGapPolicy.ResetIncompleteBuckets);

        Candle? completed = null;
        for (int i = 0; i < 12; i++)
        {
            DateTimeOffset open = start.AddSeconds(i * 5);
            decimal o = 1.0000m + i * 0.0001m;
            decimal c = o + 0.00005m;
            var candle = new Candle
            {
                Instrument = instrument,
                Interval = BarInterval.Seconds(5),
                OpenTime = open,
                CloseTime = open.AddSeconds(5),
                Prices = new Ohlc(o, o + 0.0002m, o - 0.0001m, c),
                Volume = new MarketVolume(10 + i, VolumeKind.Unknown),
                IsComplete = true
            };
            IReadOnlyList<Candle> closed = agg.ApplyExecutionCandle(candle);
            if (closed.Count > 0)
                completed = closed[0];
        }

        Assert.That(completed, Is.Not.Null);
        Assert.That(completed!.OpenTime, Is.EqualTo(start));
        Assert.That(completed.Prices.Open, Is.EqualTo(1.0000m));
        Assert.That(completed.Prices.Close, Is.EqualTo(1.0000m + 11 * 0.0001m + 0.00005m));
        Assert.That(completed.Prices.High, Is.EqualTo(1.0000m + 11 * 0.0001m + 0.0002m));
        Assert.That(completed.Prices.Low, Is.EqualTo(1.0000m - 0.0001m));
        Assert.That(completed.Volume!.Value, Is.EqualTo(Enumerable.Range(10, 12).Sum()));
    }

    [Test]
    public void AnalysisBaseAggregator_Aggregates_60x1s_To_1m()
    {
        InstrumentKey instrument = new("FX:EUR/USD");
        DateTimeOffset start = new(2025, 6, 2, 10, 0, 0, TimeSpan.Zero);
        var agg = new AnalysisBaseAggregator(
            instrument,
            BarInterval.Seconds(1),
            BarInterval.Minutes(1));

        int completedCount = 0;
        Candle? last = null;
        for (int i = 0; i < 60; i++)
        {
            DateTimeOffset open = start.AddSeconds(i);
            var candle = new Candle
            {
                Instrument = instrument,
                Interval = BarInterval.Seconds(1),
                OpenTime = open,
                CloseTime = open.AddSeconds(1),
                Prices = new Ohlc(1m + i * 0.00001m, 1.1m, 0.9m, 1m + i * 0.00001m),
                Volume = new MarketVolume(1m, VolumeKind.Unknown),
                IsComplete = true
            };
            IReadOnlyList<Candle> closed = agg.ApplyExecutionCandle(candle);
            completedCount += closed.Count;
            if (closed.Count > 0)
                last = closed[0];
        }

        Assert.That(completedCount, Is.EqualTo(1));
        Assert.That(last!.Prices.Open, Is.EqualTo(1m));
        Assert.That(last.Prices.Close, Is.EqualTo(1m + 59 * 0.00001m));
        Assert.That(last.Volume!.Value, Is.EqualTo(60m));
    }

    [Test]
    public void AnalysisBaseAggregator_DiscardsMinuteWithMissingExecutionMember()
    {
        InstrumentKey instrument = new("FX:EUR/USD");
        DateTimeOffset start = new(2025, 6, 2, 10, 0, 0, TimeSpan.Zero);
        var aggregator = new AnalysisBaseAggregator(
            instrument,
            BarInterval.Seconds(5),
            BarInterval.Minutes(1),
            BaseCandleGapPolicy.ResetIncompleteBuckets);

        var emitted = new List<Candle>();
        for (int index = 0; index < 24; index++)
        {
            if (index == 4)
                continue;
            DateTimeOffset open = start.AddSeconds(index * 5);
            emitted.AddRange(aggregator.ApplyExecutionCandle(new Candle
            {
                Instrument = instrument,
                Interval = BarInterval.Seconds(5),
                OpenTime = open,
                CloseTime = open.AddSeconds(5),
                Prices = new Ohlc(1m, 1.1m, 0.9m, 1m),
                IsComplete = true
            }));
        }

        Assert.Multiple(() =>
        {
            Assert.That(emitted, Has.Count.EqualTo(1));
            Assert.That(emitted[0].OpenTime, Is.EqualTo(start.AddMinutes(1)));
            Assert.That(emitted[0].IsComplete, Is.True);
            Assert.That(aggregator.IncompleteAggregateCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ImportedCsvValidation_RejectsDuplicateAndInvalidRows()
    {
        string path = Path.Combine(Path.GetTempPath(), $"tradinghub-import-{Guid.NewGuid():N}.csv");
        try
        {
            await File.WriteAllTextAsync(path, """
                timestamp,open,high,low,close,volume
                2025-06-02T10:00:00Z,1.0,1.1,0.9,1.05,10
                2025-06-02T10:00:00Z,1.0,1.1,0.9,1.05,10
                """);

            InvalidDataException exception = Assert.ThrowsAsync<InvalidDataException>(async () =>
                await ImportedSecondCandleSource.ValidateFileAsync(path, BarInterval.Seconds(1)))!;
            Assert.That(exception.Message, Does.Contain("duplicate"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void No_Synthetic_1s_From_1m_In_Capabilities()
    {
        // OANDA capabilities must not list 1s — proves we never advertise fabricated 1s candles.
        Assert.That(
            OandaCandleCapabilities.Instance.SupportsInterval(BarInterval.Seconds(1)),
            Is.False);
        Assert.That(
            OandaCandleCapabilities.Instance.SupportsInterval(BarInterval.Seconds(5)),
            Is.True);
    }

    [Test]
    public void PrecisionMode_Presets()
    {
        SimulationTimeframeOptions fast = SimulationTimeframeOptions.FromPrecision(SimulationPrecisionMode.Fast);
        Assert.That(fast.ExecutionInterval, Is.EqualTo(BarInterval.Minutes(1)));
        Assert.That(fast.AnalysisBaseInterval, Is.EqualTo(BarInterval.Minutes(1)));

        SimulationTimeframeOptions oanda = SimulationTimeframeOptions.FromPrecision(
            SimulationPrecisionMode.BrokerNativePrecision);
        Assert.That(oanda.ExecutionInterval, Is.EqualTo(BarInterval.Seconds(5)));
        Assert.That(oanda.AnalysisBaseInterval, Is.EqualTo(BarInterval.Minutes(1)));

        SimulationTimeframeOptions high = SimulationTimeframeOptions.FromPrecision(
            SimulationPrecisionMode.HighPrecision);
        Assert.That(high.ExecutionInterval, Is.EqualTo(BarInterval.Seconds(1)));
    }

    [Test]
    public async Task Full5sEngineRun_BatchesWorkersAndWritesOnlyOneMinuteChartRows()
    {
        InstrumentKey instrument = new("FX:EUR/USD");
        DateTimeOffset start = new(2025, 6, 2, 10, 0, 0, TimeSpan.Zero);
        Candle[] candles = Enumerable.Range(0, 24).Select(index =>
        {
            DateTimeOffset open = start.AddSeconds(index * 5);
            return new Candle
            {
                Instrument = instrument,
                Interval = BarInterval.Seconds(5),
                OpenTime = open,
                CloseTime = open.AddSeconds(5),
                Prices = new Ohlc(1m, 1.1m, 0.9m, 1m),
                IsComplete = true
            };
        }).ToArray();
        var agent = new RecordingTradingAgent(
            [BarInterval.Minutes(1)],
            BarInterval.Minutes(1));
        string output = Path.Combine(Path.GetTempPath(), $"tradinghub-5s-{Guid.NewGuid():N}");
        var runtime = new BacktestRuntimeOptions
        {
            ExecutionInterval = BarInterval.Seconds(5),
            AnalysisBaseInterval = BarInterval.Minutes(1),
            AnalysisIntervals = [BarInterval.Minutes(5)],
            SourceKind = HistoricalDataSourceKind.InlineTestData,
            WarmupDays = 0,
            SourcePageSize = 10,
            PrefetchCapacity = 100,
            PrefetchLowWatermark = 20,
            ReplayChunkSize = 100,
            StrategyExecutionMode = StrategyExecutionMode.ParallelWorkers
        };

        try
        {
            var engine = new StreamingComparativeEngine(
                new EnumerableMarketCandleStream(candles),
                [("observe", agent)]);
            ComparativeSimulationResult result = await engine.RunAsync(
                new StreamingComparativeEngineOptions
                {
                    SimulationId = Guid.NewGuid(),
                    Instrument = instrument,
                    EvaluationFrom = start,
                    EvaluationTo = start.AddMinutes(2),
                    StreamFrom = start,
                    AnalysisIntervals = runtime.AnalysisIntervals,
                    Runtime = runtime,
                    SimulationOptions = new SimulationOptions
                    {
                        BaseCurrency = "USD",
                        BaseCandleGapPolicy = BaseCandleGapPolicy.ResetIncompleteBuckets
                    },
                    OutputDirectory = output,
                    InputStreamId = "5s-test"
                },
                new HistoricalCandleRequest(
                    instrument,
                    BarInterval.Seconds(5),
                    start,
                    start.AddMinutes(2),
                    UseHistoricalBidAsk: false));

            string chunkPath = Directory
                .EnumerateFiles(Path.Combine(output, "market"), "chunk-*.json.gz")
                .Single();
            await using FileStream file = File.OpenRead(chunkPath);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            JsonElement rows = await JsonSerializer.DeserializeAsync<JsonElement>(gzip);

            Assert.Multiple(() =>
            {
                Assert.That(result.ProcessedBaseCandles, Is.EqualTo(24));
                Assert.That(result.Strategies[0].Metrics.ProcessedFrames, Is.EqualTo(24));
                Assert.That(rows.GetArrayLength(), Is.EqualTo(2));
                Assert.That(rows[0].GetProperty("closedIntervals").EnumerateArray()
                    .Select(value => value.GetString()), Does.Contain("1m"));
                Assert.That(rows[0].GetProperty("analysis").ValueKind, Is.EqualTo(JsonValueKind.Object));
                Assert.That(result.FillModel, Is.EqualTo(FillModel.MidpointPlusConfiguredSpread));
            });
        }
        finally
        {
            if (Directory.Exists(output))
                Directory.Delete(output, recursive: true);
        }
    }

    [Test]
    public async Task FullImported1sEngineRun_ConsumesGenuineRowsAndClosesOneMinute()
    {
        InstrumentKey instrument = new("FX:EUR/USD");
        DateTimeOffset start = new(2025, 6, 2, 10, 0, 0, TimeSpan.Zero);
        string root = Path.Combine(Path.GetTempPath(), $"tradinghub-1s-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string csv = Path.Combine(root, "candles.csv");
        var content = new StringBuilder("timestamp,open,high,low,close,volume\n");
        for (int index = 0; index < 60; index++)
        {
            content.Append(start.AddSeconds(index).ToString("O"))
                .Append(",1.0,1.1,0.9,1.0,1\n");
        }
        await File.WriteAllTextAsync(csv, content.ToString());

        var agent = new RecordingTradingAgent(
            [BarInterval.Minutes(1)],
            BarInterval.Minutes(1));
        var runtime = new BacktestRuntimeOptions
        {
            ExecutionInterval = BarInterval.Seconds(1),
            AnalysisBaseInterval = BarInterval.Minutes(1),
            AnalysisIntervals = [BarInterval.Minutes(5)],
            PrecisionMode = SimulationPrecisionMode.HighPrecision,
            SourceKind = HistoricalDataSourceKind.ImportedSecondCandles,
            ImportedCandlePath = csv,
            WarmupDays = 0,
            SourcePageSize = 10,
            PrefetchCapacity = 100,
            PrefetchLowWatermark = 20,
            ReplayChunkSize = 100
        };

        try
        {
            var engine = new StreamingComparativeEngine(
                new ImportedSecondCandleSource(csv, instrument, BarInterval.Seconds(1)),
                [("observe", agent)]);
            ComparativeSimulationResult result = await engine.RunAsync(
                new StreamingComparativeEngineOptions
                {
                    SimulationId = Guid.NewGuid(),
                    Instrument = instrument,
                    EvaluationFrom = start,
                    EvaluationTo = start.AddMinutes(1),
                    StreamFrom = start,
                    AnalysisIntervals = runtime.AnalysisIntervals,
                    Runtime = runtime,
                    SimulationOptions = new SimulationOptions
                    {
                        BaseCurrency = "USD",
                        BaseCandleGapPolicy = BaseCandleGapPolicy.ResetIncompleteBuckets
                    },
                    OutputDirectory = Path.Combine(root, "out"),
                    InputStreamId = "1s-import-test"
                },
                new HistoricalCandleRequest(
                    instrument,
                    BarInterval.Seconds(1),
                    start,
                    start.AddMinutes(1),
                    UseHistoricalBidAsk: false));

            string chunkPath = Directory
                .EnumerateFiles(Path.Combine(root, "out", "market"), "chunk-*.json.gz")
                .Single();
            await using FileStream file = File.OpenRead(chunkPath);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            JsonElement rows = await JsonSerializer.DeserializeAsync<JsonElement>(gzip);

            Assert.Multiple(() =>
            {
                Assert.That(result.ProcessedBaseCandles, Is.EqualTo(60));
                Assert.That(result.DataQuality.CandleCount, Is.EqualTo(60));
                Assert.That(result.DataQuality.IncompleteAggregateCount, Is.Zero);
                Assert.That(rows.GetArrayLength(), Is.EqualTo(1));
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
