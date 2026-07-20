using System.IO.Compression;
using System.Text.Json;
using Agent.Configuration;
using Agent.Strategies.StructuralConfluence;
using Brokers.Models;
using ChartAnnotator.MarketData;
using Simulator.Calibration;
using Simulator.Experiments.IndicatorCalibration;
using Simulator.Experiments.IndicatorCalibration.Strategies;
using Simulator.Jobs;
using Simulator.MarketData;
using Simulator.Models;
using Simulator.Services;

namespace Simulator.Tests;

/// <summary>
/// Blueprint §19 Phase 6 gate: "real cached-data run completes without changing baseline
/// behaviour." Every test here replays real, previously-cached EUR/USD 1-minute candles (the same
/// on-disk cache the simulator itself reads from) through the real
/// <see cref="BacktestApplicationService"/> engine - never a synthetic scoring stub - proving the
/// indicator-calibration vertical slice is wired into the actual engine, not just its own tests.
/// </summary>
[TestFixture]
public sealed class IndicatorConfluenceCalibrationEndToEndTests
{
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");
    private static readonly BarInterval BaseInterval = BarInterval.Minutes(1);

    private static readonly BacktestRuntimeOptions RuntimeTemplate = new()
    {
        BaseInterval = BaseInterval,
        AnalysisIntervals = [BarInterval.Minutes(5), BarInterval.Minutes(15), BarInterval.Hours(1)],
        MaximumParallelStrategies = 1,
        StrategyExecutionMode = StrategyExecutionMode.Sequential
    };

    private const string StrategyType = TradingAgentTypeIds.StructuralConfluence;

    private static IReadOnlyList<Candle> LoadRealCandles(DateTimeOffset from, DateTimeOffset to)
    {
        string path = FindCachedCandleFile(from, to);
        var result = new List<Candle>();
        using var stream = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using var reader = new StreamReader(stream);
        _ = reader.ReadLine(); // header line (schema metadata, not a candle)
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            RawCandle? raw = JsonSerializer.Deserialize<RawCandle>(line, options);
            if (raw is null || raw.CloseTime <= from) continue;
            if (raw.OpenTime >= to) break;
            result.Add(new Candle
            {
                Instrument = Instrument,
                Interval = BaseInterval,
                OpenTime = raw.OpenTime,
                CloseTime = raw.CloseTime,
                Prices = new Ohlc(raw.Open, raw.High, raw.Low, raw.Close),
                Volume = new MarketVolume(raw.Volume, VolumeKind.TickCount),
                IsComplete = true
            });
        }
        return result;
    }

    private sealed record RawCandle(
        DateTimeOffset OpenTime, DateTimeOffset CloseTime,
        decimal Open, decimal High, decimal Low, decimal Close, decimal Volume);

    /// <summary>
    /// Walks up from the test assembly's location to find the repo's <c>.cache/historical</c> real
    /// market-data cache, then - since several overlapping-but-different-range cache files can
    /// coexist there - picks the one whose own <c>FX_EUR_USD_1m_{from}_{to}_hash.jsonl.gz</c>
    /// filename range actually covers [<paramref name="from"/>, <paramref name="to"/>), rather than
    /// just the first file glob-matched (which could easily be a narrower-range file).
    /// </summary>
    private static string FindCachedCandleFile(DateTimeOffset from, DateTimeOffset to)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, ".cache", "historical");
            if (Directory.Exists(candidate))
            {
                string? file = Directory.GetFiles(candidate, "FX_EUR_USD_1m_*.jsonl.gz")
                    .Where(path => FileCoversRange(Path.GetFileName(path), from, to))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (file is not null)
                    return file;
            }
            directory = directory.Parent;
        }
        throw new FileNotFoundException(
            $"Could not find a real cached FX_EUR_USD_1m_*.jsonl.gz file covering [{from:yyyy-MM-dd},{to:yyyy-MM-dd}) " +
            "under any ancestor .cache/historical directory.");
    }

    private static bool FileCoversRange(string fileName, DateTimeOffset from, DateTimeOffset to)
    {
        // FX_EUR_USD_1m_{yyyyMMdd}_{yyyyMMdd}_{hash}.jsonl.gz
        string[] parts = fileName.Split('_');
        if (parts.Length < 7)
            return false;
        if (!DateTimeOffset.TryParseExact(parts[4], "yyyyMMdd", null,
                System.Globalization.DateTimeStyles.AssumeUniversal, out DateTimeOffset fileFrom))
        {
            return false;
        }
        if (!DateTimeOffset.TryParseExact(parts[5], "yyyyMMdd", null,
                System.Globalization.DateTimeStyles.AssumeUniversal, out DateTimeOffset fileTo))
        {
            return false;
        }
        return fileFrom <= from && fileTo >= to;
    }

    private static BacktestApplicationService CreateService(string tempRoot)
    {
        Directory.CreateDirectory(tempRoot);
        return new BacktestApplicationService(
            new FileSimulationJobRepository(Path.Combine(tempRoot, "jobs")),
            new BacktestApplicationServiceOptions { MaxConcurrentJobs = 1, QueueCapacity = 2 });
    }

    private static BacktestRequest BuildDirectRequest(
        IReadOnlyList<Candle> candles,
        DateTimeOffset from,
        DateTimeOffset to,
        StructuralConfluenceStrategyOptions options,
        string tempRoot) => new()
    {
        Instrument = Instrument,
        From = from,
        To = to,
        Strategies = [StrategyType],
        StrategyAssignments =
        [
            new StrategyInstrumentAssignment
            {
                Id = "direct",
                StrategyType = StrategyType,
                Instrument = Instrument,
                AgentDefinitionOverride = new TradingAgentDefinition
                {
                    Kind = TradingAgentKind.StructuralConfluence,
                    StructuralConfluence = options
                }
            }
        ],
        CaptureMarketReplay = false,
        InlineCandles = candles,
        Runtime = RuntimeTemplate with { SourceKind = HistoricalDataSourceKind.InlineTestData, WarmupDays = 0 },
        OutputDirectory = Path.Combine(tempRoot, "out"),
        JobsDirectory = Path.Combine(tempRoot, "jobs")
    };

    [Test]
    [Explicit("Runs real backtests over real cached market data (~5 minutes); on demand only, consistent with this project's other real-data tests.")]
    public async Task BaselineEquivalence_CalibrationAdapterAtUnchangedDefaults_MatchesADirectBacktest()
    {
        DateTimeOffset attributionFrom = new(2026, 2, 2, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset attributionTo = new(2026, 2, 16, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset streamFrom = attributionFrom.AddDays(-10);
        IReadOnlyList<Candle> candles = LoadRealCandles(streamFrom, attributionTo);
        Assert.That(candles, Is.Not.Empty, "The real cached-data file did not cover the requested test window.");

        var baselineStructuralOptions = new StructuralConfluenceStrategyOptions();
        string tempRoot = Path.Combine(Path.GetTempPath(), "th-indicator-calibration-e2e", Guid.NewGuid().ToString("N"));

        // Path 1: a direct backtest, with no calibration machinery involved at all - today's
        // existing behaviour, unchanged.
        await using BacktestApplicationService directService = CreateService(Path.Combine(tempRoot, "direct"));
        ComparativeSimulationResult directResult = await directService.RunToCompletionAsync(
            BuildDirectRequest(candles, streamFrom, attributionTo, baselineStructuralOptions, Path.Combine(tempRoot, "direct")));
        StrategySimulationResult directStrategy = directResult.Strategies.Single();

        // Path 2: the same window through BacktestCandidateEvaluator with the calibration
        // candidate left at the baseline's own effective values (no override applied).
        var manifest = new IndicatorConfluenceCalibrationManifest();
        await using BacktestApplicationService adapterService = CreateService(Path.Combine(tempRoot, "adapter"));
        var settings = new BacktestCandidateEvaluatorSettings
        {
            Instrument = Instrument,
            StrategyType = StrategyType,
            RuntimeTemplate = RuntimeTemplate,
            Candles = candles,
            WarmupDays = 10
        };
        var factory = new BacktestCandidateEvaluatorFactory<IndicatorConfluenceOptions>(
            adapterService,
            manifest,
            baselineStructuralOptions.IndicatorConfluence,
            indicatorOptions => new TradingAgentDefinition
            {
                Kind = TradingAgentKind.StructuralConfluence,
                StructuralConfluence = baselineStructuralOptions with { IndicatorConfluence = indicatorOptions }
            },
            settings);
        ICandidateEvaluator evaluator = factory.CreateEvaluator(attributionFrom, attributionTo);
        CalibrationCandidate baselineCandidate = CalibrationCandidate.FromEffectiveOptions(
            baselineStructuralOptions.IndicatorConfluence, manifest);
        BacktestEvaluationResult adapterResult = await evaluator.EvaluateAsync(baselineCandidate);

        StrategyPerformanceSnapshot directPerformance = StrategyPerformanceSnapshot.FromTrades(
            directStrategy.Result.Trades
                .Where(trade => trade.OpenedAt is not null &&
                    trade.OpenedAt.Value >= attributionFrom && trade.OpenedAt.Value < attributionTo)
                .ToArray());

        Assert.Multiple(() =>
        {
            Assert.That(adapterResult.DataQualityValid, Is.True);
            Assert.That(adapterResult.TradeCount, Is.EqualTo(directPerformance.TradeCount),
                "Going through the calibration adapter at unchanged baseline values must not change how many trades the strategy takes.");
            Assert.That(adapterResult.MedianExpectancyR, Is.EqualTo(directPerformance.MedianR ?? 0m),
                "Going through the calibration adapter at unchanged baseline values must not change trade outcomes.");
        });
    }

    [Test]
    [Explicit("Runs real backtests over real cached market data (~2-3 minutes); on demand only, consistent with this project's other real-data tests.")]
    public async Task BacktestCandidateEvaluator_OnRealData_ProducesAWellFormedResultForAPerturbedCandidate()
    {
        DateTimeOffset attributionFrom = new(2026, 2, 2, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset attributionTo = new(2026, 2, 16, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset streamFrom = attributionFrom.AddDays(-10);
        IReadOnlyList<Candle> candles = LoadRealCandles(streamFrom, attributionTo);
        Assert.That(candles, Is.Not.Empty);

        var baselineStructuralOptions = new StructuralConfluenceStrategyOptions();
        var manifest = new IndicatorConfluenceCalibrationManifest();
        string tempRoot = Path.Combine(Path.GetTempPath(), "th-indicator-calibration-e2e", Guid.NewGuid().ToString("N"));
        await using BacktestApplicationService service = CreateService(tempRoot);

        var settings = new BacktestCandidateEvaluatorSettings
        {
            Instrument = Instrument,
            StrategyType = StrategyType,
            RuntimeTemplate = RuntimeTemplate,
            Candles = candles,
            WarmupDays = 10
        };
        var factory = new BacktestCandidateEvaluatorFactory<IndicatorConfluenceOptions>(
            service,
            manifest,
            baselineStructuralOptions.IndicatorConfluence,
            indicatorOptions => new TradingAgentDefinition
            {
                Kind = TradingAgentKind.StructuralConfluence,
                StructuralConfluence = baselineStructuralOptions with { IndicatorConfluence = indicatorOptions }
            },
            settings);
        ICandidateEvaluator evaluator = factory.CreateEvaluator(attributionFrom, attributionTo);

        CalibrationCandidate perturbed = CalibrationCandidate
            .FromEffectiveOptions(baselineStructuralOptions.IndicatorConfluence, manifest)
            .WithNumericValue("minimum-adx", 15m)
            .WithNumericValue("stop-atr", 1.25m)
            .WithNumericValue("target-atr", 2.5m);

        BacktestEvaluationResult result = await evaluator.EvaluateAsync(perturbed);

        Assert.Multiple(() =>
        {
            Assert.That(result.DataQualityValid, Is.True);
            Assert.That(result.TradeCount, Is.GreaterThanOrEqualTo(0));
            Assert.DoesNotThrow(() => result.Validate());
        });
    }

    /// <summary>
    /// The full leakage-safe orchestrator (Phases 0-7 of the search algorithm) run to completion
    /// against real cached data. Uses a deliberately small 2-parameter test-only manifest (not the
    /// checked-in 6-parameter production <see cref="IndicatorConfluenceCalibrationManifest"/>) so
    /// the real-backtest-call count stays in the low hundreds rather than the thousands a full
    /// production manifest would need - this proves the vertical slice (manifest to search to
    /// walk-forward to holdout to artifact) genuinely works against the real engine and real
    /// market data, without making the default test run pay for a full-scale search. "No
    /// improvement" / "insufficient evidence" are valid, expected outcomes here (blueprint §9.6) -
    /// this test asserts completion and artifact validity, not that a winning candidate was found.
    /// </summary>
    [Test]
    [Explicit(
        "Runs ~150-250 real backtest evaluations against cached market data. Measured cost of a single " +
        "real evaluation over a similar window is ~90-110 seconds (see the other two real-data tests in " +
        "this file), so a full run here can take multiple hours - on demand only, never as part of routine " +
        "regression runs.")]
    public async Task FullOrchestrator_OnRealCachedData_CompletesAndProducesAValidArtifact()
    {
        var timeline = new CalibrationTimeline
        {
            LearningFrom = new DateTimeOffset(2026, 1, 12, 0, 0, 0, TimeSpan.Zero),
            LearningTo = new DateTimeOffset(2026, 3, 9, 0, 0, 0, TimeSpan.Zero),
            ExternalHoldoutFrom = new DateTimeOffset(2026, 3, 11, 0, 0, 0, TimeSpan.Zero),
            ExternalHoldoutTo = new DateTimeOffset(2026, 3, 25, 0, 0, 0, TimeSpan.Zero),
            EmbargoDays = 2,
            WarmupDays = 10
        };
        DateTimeOffset streamFrom = timeline.LearningFrom.AddDays(-timeline.WarmupDays);
        IReadOnlyList<Candle> candles = LoadRealCandles(streamFrom, timeline.ExternalHoldoutTo);
        Assert.That(candles, Is.Not.Empty);

        var baselineStructuralOptions = new StructuralConfluenceStrategyOptions();
        var manifest = new SmallTestManifest();
        string tempRoot = Path.Combine(Path.GetTempPath(), "th-indicator-calibration-e2e", Guid.NewGuid().ToString("N"));
        await using BacktestApplicationService service = CreateService(tempRoot);

        var settings = new BacktestCandidateEvaluatorSettings
        {
            Instrument = Instrument,
            StrategyType = StrategyType,
            RuntimeTemplate = RuntimeTemplate,
            Candles = candles,
            WarmupDays = timeline.WarmupDays
        };
        var factory = new BacktestCandidateEvaluatorFactory<IndicatorConfluenceOptions>(
            service,
            manifest,
            baselineStructuralOptions.IndicatorConfluence,
            indicatorOptions => new TradingAgentDefinition
            {
                Kind = TradingAgentKind.StructuralConfluence,
                StructuralConfluence = baselineStructuralOptions with { IndicatorConfluence = indicatorOptions }
            },
            settings);

        var request = new IndicatorCalibrationRequest
        {
            StrategyId = manifest.StrategyId,
            ManifestVersion = manifest.ManifestVersion,
            Instrument = Instrument,
            TimeframeTopology = new TimeframeTopology
            {
                ExecutionInterval = BarInterval.Minutes(1),
                AnalysisBaseInterval = BarInterval.Minutes(1),
                SetupInterval = BarInterval.Minutes(15),
                ConfirmationIntervals = [],
                TrendIntervals = [BarInterval.Hours(1)],
                ManagementIntervals = [],
                AlignmentPolicy = BaseCandleGapPolicy.ResetIncompleteBuckets,
                WarmupMinimumDays = timeline.WarmupDays
            },
            Timeline = timeline,
            InternalFoldCount = 3,
            RandomSeed = 7,
            Budget = new CalibrationEvaluationBudget
            {
                WarningEvaluationCount = 500,
                MaximumEvaluationCount = 1000,
                MaximumEvaluationsPerFold = 400,
                MaximumInteractionCombinationsPerGroup = 6,
                HardRuntimeLimit = TimeSpan.FromMinutes(10),
                OverflowPolicy = CalibrationBudgetOverflowPolicy.Reject
            },
            BaselineConfigurationHash = IndicatorCalibrationHash.ComputeOfObject(baselineStructuralOptions.IndicatorConfluence)
        };

        IndicatorCalibrationOrchestrationResult result = await IndicatorCalibrationOrchestrator.RunAsync(
            request, baselineStructuralOptions.IndicatorConfluence, manifest, factory, maximumCoordinatePasses: 1);

        Assert.That(Enum.IsDefined(result.Outcome), Is.True);
        Assert.That(result.FoldResults, Has.Count.EqualTo(3));

        CalibrationCompatibilityIdentity compatibility = IndicatorConfluenceCalibrationCompatibility.Instance.Describe(
            request.TimeframeTopology);
        IndicatorCalibrationArtifact artifact = IndicatorCalibrationArtifactFactory.BuildArtifact(
            result, manifest, baselineStructuralOptions.IndicatorConfluence, compatibility,
            calibrationId: Guid.NewGuid().ToString("N"),
            instrument: Instrument.Value,
            candleDataIdentityHash: "test-candle-data-hash",
            baselineConfigurationHash: request.BaselineConfigurationHash,
            experimentLedgerId: Guid.NewGuid().ToString("N"),
            experimentLedgerChecksum: "test-ledger-checksum",
            createdAt: DateTimeOffset.UtcNow);

        Assert.DoesNotThrow(() => artifact.Validate());
        Assert.That(artifact.PromotionStatus, Is.EqualTo(CalibrationPromotionStatus.PendingReview));
    }

    /// <summary>Deliberately small 2-parameter manifest (StopAtr x TargetAtr only) so the full-orchestrator test above stays cheap.</summary>
    private sealed class SmallTestManifest : IIndicatorCalibrationManifest<IndicatorConfluenceOptions>
    {
        public int SchemaVersion => 1;
        public string ManifestVersion => "indicator-confluence-e2e-test-manifest-v1";
        public string StrategyId => IndicatorConfluenceCalibrationCompatibility.Instance.StrategyId;
        public string OptionsSchemaVersion => IndicatorConfluenceCalibrationCompatibility.Instance.OptionsSchemaVersion;

        public IReadOnlyList<ICalibrationParameterDescriptor<IndicatorConfluenceOptions>> Parameters { get; } =
        [
            new CalibrationParameterDescriptor<IndicatorConfluenceOptions>(
                "stop-atr", "IndicatorConfluence.StopAtr",
                CalibrationParameterCategory.Stop, CalibrationValueKind.AtrMultiple, CalibrationSpacing.Linear,
                defaultValue: 1.5m, hardMinimum: 0.5m, hardMaximum: 4m,
                coarseGrid: [1.0m, 1.5m, 2.0m], refinementStep: 0.25m,
                conservativeStartingValue: 1.0m, permissiveStartingValue: 2.0m, declaredSearchOrder: 0,
                read: o => o.StopAtr, apply: (o, v) => o with { StopAtr = v }),
            new CalibrationParameterDescriptor<IndicatorConfluenceOptions>(
                "target-atr", "IndicatorConfluence.TargetAtr",
                CalibrationParameterCategory.Target, CalibrationValueKind.AtrMultiple, CalibrationSpacing.Linear,
                defaultValue: 3.0m, hardMinimum: 1m, hardMaximum: 8m,
                coarseGrid: [2.0m, 3.0m, 4.0m], refinementStep: 0.5m,
                conservativeStartingValue: 2.0m, permissiveStartingValue: 4.0m, declaredSearchOrder: 1,
                read: o => o.TargetAtr, apply: (o, v) => o with { TargetAtr = v })
        ];
        public IReadOnlyList<ICalibrationAblationDescriptor<IndicatorConfluenceOptions>> Ablations { get; } = [];
        public IReadOnlyList<CalibrationInteractionGroup> InteractionGroups { get; } =
        [
            new CalibrationInteractionGroup { GroupId = "stop-atr-x-target-atr", ParameterIds = ["stop-atr", "target-atr"] }
        ];
        public IReadOnlyList<ICalibrationConstraint<IndicatorConfluenceOptions>> Constraints { get; } =
        [
            new CalibrationConstraint<IndicatorConfluenceOptions>(
                "minimum-reward-risk", "TargetAtr/StopAtr must meet the minimum reward-risk ratio.",
                options => options.TargetAtr / options.StopAtr >= IndicatorConfluenceCalibrationManifest.MinimumRewardRisk
                    ? CalibrationConstraintResult.Valid
                    : CalibrationConstraintResult.Invalid("Reward-risk ratio too low."))
        ];
        public CalibrationScoringPolicy ScoringPolicy { get; } = new()
        {
            PolicyVersion = "e2e-test-scoring-v1",
            ObjectiveId = CandidateScorer.MedianExpectancyDrawdownPenalizedObjective,
            MinimumTradesPerFold = 1,
            MaximumDrawdownR = 50m,
            MinimumMedianExpectancyR = -50m,
            MinimumProfitFactor = 0m,
            DrawdownPenaltyWeight = 0.1m,
            TurnoverPenaltyWeight = 0.02m
        };
        public CalibrationAcceptancePolicy AcceptancePolicy { get; } = new()
        {
            PolicyVersion = "e2e-test-acceptance-v1",
            MinimumImprovementOverBaseline = 0.001m,
            MinimumAcceptableFoldPercent = 0m,
            MaximumTrainValidationDegradation = 50m,
            MinimumExternalHoldoutExpectancyR = -50m,
            MaximumExternalHoldoutDrawdownR = 50m,
            MinimumExternalHoldoutTrades = 1,
            MinimumPlateauSupport = 0m
        };
        public void Validate() => IndicatorCalibrationManifestValidation.Validate(this);
    }
}
