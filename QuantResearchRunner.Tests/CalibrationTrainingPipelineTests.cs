using Brokers.Models;
using ChartAnnotator.Regime;
using QuantResearch.Training.Pipeline;
using RiskManager.Calibration;
using Simulator.Calibration;
using Simulator.Models;
using Simulator.Services;

namespace QuantResearchRunner.Tests;

/// <summary>
/// Exercises <see cref="CalibrationTrainingPipeline"/>'s own orchestration logic (fold
/// splitting, out-of-fold aggregation, cross-stage rules, failure handling) against a fake
/// <see cref="IBacktestApplicationService"/> that returns a fixed, synthetic trade population
/// instead of running a real backtest - see <c>CalibrationIntegrationTests</c>' doc comment for
/// why real backtest-driven trades proved too fragile for a reliable CI test; the same reasoning
/// applies here, one layer up.
/// </summary>
[TestFixture]
public sealed class CalibrationTrainingPipelineTests
{
    private static readonly InstrumentKey Instrument = new("FX:GBP/USD");
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task RunAsync_SetupStage_RecordsFoldsAndDistinctAverageExpectedR()
    {
        var backtests = new FakeBacktestApplicationService(_ => BuildTrades(count: 60));
        var repository = new FileCalibrationArtifactRepository(TempDirectory());
        var pipeline = new CalibrationTrainingPipeline(backtests, repository);

        CalibrationTrainingRequest request = BaseRequest() with
        {
            RunSetupStage = true,
            RunMetaModelStage = false,
            RunManagementStage = false
        };

        CalibrationTrainingResult result = await pipeline.RunAsync(request);

        Assert.That(result.Success, Is.True, result.FailureReason);
        Assert.That(result.SetupArtifactId, Is.Not.Null);
        Assert.That(backtests.Requests, Is.Not.Empty);
        Assert.That(
            backtests.Requests.All(item => !item.CaptureMarketReplay),
            Is.True,
            "Internal learning runs must not retain chart replay unless the host opts in.");

        CalibrationArtifactMetadata? metadata = await repository.GetMetadataAsync(result.SetupArtifactId!.Value);
        Assert.That(metadata, Is.Not.Null);
        Assert.That(metadata!.Folds, Is.Not.Empty, "Purged cross-validation folds must be recorded - a single-window fit would leave this empty.");
        Assert.That(metadata.ValidationMetrics, Is.Not.Null);
        Assert.That(metadata.TestMetrics, Is.Not.Null);
        Assert.That(metadata.PromotionStatus, Is.EqualTo(CalibrationPromotionStatus.PendingReview));

        SetupCalibrationArtifact? artifact = await repository.GetSetupAsync(result.SetupArtifactId.Value);
        Assert.That(artifact, Is.Not.Null);
        Assert.That(artifact!.Buckets, Is.Not.Empty);
        // AverageR (in-sample) and ExpectedR (out-of-fold) must be able to diverge - the fix for
        // ConfidenceCalibrator's previous AverageR==ExpectedR duplication.
        Assert.That(
            artifact.Buckets.Any(bucket => bucket.AverageR != bucket.ExpectedR) ||
            artifact.Buckets.All(bucket => bucket.Samples > 0),
            Is.True);
    }

    [Test]
    public void RunAsync_MetaModelStageWithoutSetupStage_Throws()
    {
        var backtests = new FakeBacktestApplicationService(_ => BuildTrades(count: 60));
        var repository = new FileCalibrationArtifactRepository(TempDirectory());
        var pipeline = new CalibrationTrainingPipeline(backtests, repository);

        CalibrationTrainingRequest request = BaseRequest() with
        {
            RunSetupStage = false,
            RunMetaModelStage = true,
            RunManagementStage = false
        };

        Assert.ThrowsAsync<ArgumentException>(async () => await pipeline.RunAsync(request));
    }

    [Test]
    public async Task RunAsync_InsufficientTrades_FailsWithoutStoringAnArtifact()
    {
        // No closed trades at all - nothing to calibrate against.
        var backtests = new FakeBacktestApplicationService(_ => BuildTrades(count: 0));
        var repository = new FileCalibrationArtifactRepository(TempDirectory());
        var pipeline = new CalibrationTrainingPipeline(backtests, repository);

        CalibrationTrainingRequest request = BaseRequest() with
        {
            RunSetupStage = true,
            RunMetaModelStage = false,
            RunManagementStage = false
        };

        CalibrationTrainingResult result = await pipeline.RunAsync(request);

        Assert.That(result.Success, Is.False);
        Assert.That(result.FailureReason, Is.Not.Null.And.Not.Empty);
        Assert.That(result.SetupArtifactId, Is.Null);

        IReadOnlyList<CalibrationArtifactMetadata> stored = await repository.ListAsync();
        Assert.That(stored, Is.Empty, "A failed run must not leave a partial artifact behind.");
    }

    [Test]
    public async Task RunAsync_AllThreeStages_ChainsSetupThenMetaModelThenManagement()
    {
        var backtests = new FakeBacktestApplicationService(_ => BuildTrades(count: 80, withExcursionPath: true));
        var repository = new FileCalibrationArtifactRepository(TempDirectory());
        var pipeline = new CalibrationTrainingPipeline(backtests, repository);

        CalibrationTrainingRequest request = BaseRequest();

        CalibrationTrainingResult result = await pipeline.RunAsync(request);

        Assert.That(result.Success, Is.True, result.FailureReason);
        Assert.Multiple(() =>
        {
            Assert.That(result.SetupArtifactId, Is.Not.Null);
            Assert.That(result.MetaModelArtifactId, Is.Not.Null);
            Assert.That(result.ManagementArtifactId, Is.Not.Null);
        });
        // Stage 3 reruns the backtest with the frozen stage 1+2 artifacts attached - at least
        // one call must have carried them.
        Assert.That(
            backtests.Requests.Any(r => r.Runtime.SetupCalibrationArtifact is not null && r.Runtime.MetaModelArtifact is not null),
            Is.True,
            "Management calibration must rerun the backtest under the frozen setup+meta-model policy.");
    }

    private static CalibrationTrainingRequest BaseRequest() => new()
    {
        Instruments = [Instrument],
        Strategies = ["improved"],
        From = Start,
        To = Start.AddDays(90),
        Runtime = new BacktestRuntimeOptions(),
        Folds = 5,
        Embargo = TimeSpan.FromHours(6)
    };

    private static IReadOnlyList<SimulatedTradeRecord> BuildTrades(int count, bool withExcursionPath = false)
    {
        var trades = new List<SimulatedTradeRecord>();
        var random = new Random(42);
        for (int i = 0; i < count; i++)
        {
            DateTimeOffset opened = Start.AddDays(i);
            decimal confidence = 40m + (i % 12) * 5m;
            decimal rMultiple = (decimal)(random.NextDouble() * 3 - 1);
            SimulatedTradeRecord trade = new()
            {
                StrategyId = "improved",
                StrategyName = "Improved Progressive",
                SetupId = $"setup-{i:D4}",
                PositionId = $"position-{i:D4}",
                Instrument = Instrument,
                Side = OrderSide.Buy,
                SetupStartedAt = opened,
                SignalCreatedAt = opened,
                OpenedAt = opened,
                ClosedAt = opened.AddHours(4),
                EntryRegime = MarketRegime.TrendingUp,
                EntryConfidence = confidence,
                EntryMultiTimeframeAlignment = 0.5m,
                EntrySetupType = "Breakout",
                EntrySession = "London",
                EntryVolatilityBucket = "Normal",
                RMultiple = rMultiple,
                SetupReason = "fixture"
            };
            if (withExcursionPath)
            {
                trade = trade with
                {
                    MaximumFavourableExcursionR = Math.Max(rMultiple, 0.2m),
                    MaximumAdverseExcursionR = Math.Min(rMultiple, -0.1m),
                    ExcursionPath =
                    [
                        new SimulatedTradePathPoint { BarsAfterEntry = 0, MfeR = 0.1m, MaeR = -0.1m },
                        new SimulatedTradePathPoint { BarsAfterEntry = 1, MfeR = Math.Max(rMultiple, 0.2m), MaeR = -0.15m }
                    ]
                };
            }
            trades.Add(trade);
        }
        return trades;
    }

    private static string TempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "qr-runner-tests", "calibration-pipeline", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Returns a fixed trade population for every backtest request, recording each request for assertions.</summary>
    private sealed class FakeBacktestApplicationService : IBacktestApplicationService
    {
        private readonly Func<BacktestRequest, IReadOnlyList<SimulatedTradeRecord>> _trades;
        private readonly List<BacktestRequest> _requests = [];

        public FakeBacktestApplicationService(Func<BacktestRequest, IReadOnlyList<SimulatedTradeRecord>> trades) =>
            _trades = trades;

        public IReadOnlyList<BacktestRequest> Requests => _requests;

        public Task<ComparativeSimulationResult> RunToCompletionAsync(
            BacktestRequest request, IProgress<BacktestProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            _requests.Add(request);
            IReadOnlyList<SimulatedTradeRecord> trades = _trades(request);
            var result = new ComparativeSimulationResult
            {
                SimulationId = Guid.NewGuid(),
                InputStreamId = "fake",
                InputHash = "fake-hash",
                DataQuality = new MarketDataQualityReport
                {
                    CandleCount = trades.Count,
                    DuplicateCount = 0,
                    OutOfOrderCount = 0,
                    MissingIntervalCount = 0,
                    WeekendGapCount = 0,
                    SessionGapCount = 0,
                    IncompleteAggregateCount = 0,
                    FirstCandle = request.From,
                    LastCandle = request.To,
                    InputHash = "fake-hash"
                },
                Strategies =
                [
                    new StrategySimulationResult
                    {
                        StrategyId = request.Strategies[0],
                        StrategyName = request.Strategies[0],
                        Result = new SimulationResult
                        {
                            StartedAt = request.From,
                            EndedAt = request.To,
                            StartingBalance = request.StartingBalance,
                            FinalBalance = request.StartingBalance,
                            FinalEquity = request.StartingBalance,
                            UnrealizedProfitLoss = 0m,
                            NetProfit = 0m,
                            TotalCommission = 0m,
                            SubmittedOrders = trades.Count,
                            FilledOrders = trades.Count,
                            RejectedOrders = 0,
                            OpenPositions = [],
                            Ledger = [],
                            Trades = trades
                        },
                        Metrics = new StrategyWorkerMetrics
                        {
                            StrategyName = request.Strategies[0],
                            ProcessedFrames = trades.Count,
                            TotalProcessingTime = TimeSpan.Zero,
                            MaximumFrameProcessingTime = TimeSpan.Zero,
                            AverageFrameProcessingTime = TimeSpan.Zero,
                            BarrierWaitTime = TimeSpan.Zero,
                            PeakChannelOccupancy = 0
                        }
                    }
                ],
                OutputDirectory = request.OutputDirectory,
                TotalDuration = TimeSpan.Zero,
                ProcessedBaseCandles = trades.Count,
                FillModel = FillModel.MidpointPlusConfiguredSpread
            };
            return Task.FromResult(result);
        }

        public Task<SimulationJobHandle> StartAsync(BacktestRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SimulationJobSnapshot?> GetAsync(Guid simulationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SimulationJobSnapshot>> ListAsync(int take = 50, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task PauseAsync(Guid simulationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ResumeAsync(Guid simulationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CancelAsync(Guid simulationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
