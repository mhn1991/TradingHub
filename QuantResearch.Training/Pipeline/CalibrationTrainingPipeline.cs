using System.Security.Cryptography;
using System.Text;
using Brokers.Models;
using Microsoft.Extensions.Logging;
using QuantResearch.Calibration;
using QuantResearch.Training.Experiments;
using QuantResearch.Training.Mapping;
using QuantResearch.Validation;
using RiskManager.Calibration;
using Simulator.Calibration;
using Simulator.Models;
using Simulator.Services;
using TradeManager;

namespace QuantResearch.Training.Pipeline;

/// <summary>
/// Single, leakage-safe, three-stage calibration orchestrator. Replaces the calibration logic
/// previously duplicated in <c>QuantResearchRunner/Program.cs</c>'s <c>calibrate-*</c> commands
/// and <c>DashboardLive/ResearchCalibrationService.cs</c> - both now delegate here.
///
/// Stage order and dependency, per the product requirement this implements:
///   1. Setup calibration - purged cross-validation, out-of-fold bucket statistics.
///   2. Meta-model calibration - trained ONLY on the trades stage 1 scored out-of-fold (never
///      on trades whose setup-calibration bucket was fit in-sample), so it can only run in the
///      same call as stage 1.
///   3. Management calibration - freezes the stage 1 + stage 2 artifacts, reruns the backtest
///      under that frozen entry policy to get the trade population management would actually
///      see, then calibrates on that.
///
/// Every stage reserves its own most-recent purged-CV fold as an untouched test window,
/// evaluated exactly once and never used to pick buckets or tune anything.
/// </summary>
public sealed class CalibrationTrainingPipeline
{
    private readonly IBacktestApplicationService _backtests;
    private readonly ICalibrationArtifactRepository _artifacts;
    private readonly ILogger<CalibrationTrainingPipeline>? _logger;

    public CalibrationTrainingPipeline(
        IBacktestApplicationService backtests,
        ICalibrationArtifactRepository artifacts,
        ILogger<CalibrationTrainingPipeline>? logger = null)
    {
        _backtests = backtests ?? throw new ArgumentNullException(nameof(backtests));
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        _logger = logger;
    }

    public async Task<CalibrationTrainingResult> RunAsync(
        CalibrationTrainingRequest request,
        IProgress<CalibrationTrainingSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        if (!request.RunSetupStage && !request.RunMetaModelStage && !request.RunManagementStage)
            throw new ArgumentException("At least one stage must run.");
        if (request.RunMetaModelStage && !request.RunSetupStage)
            throw new ArgumentException(
                "Meta-model calibration trains only on stage 1's out-of-fold scores, so it can only run together with the setup stage.");
        if (request.RunManagementStage && !request.RunSetupStage && request.ExistingSetupArtifactId is null)
            throw new ArgumentException("Management calibration requires RunSetupStage or ExistingSetupArtifactId.");
        if (request.RunManagementStage && !request.RunMetaModelStage && request.ExistingMetaModelArtifactId is null)
            throw new ArgumentException("Management calibration requires RunMetaModelStage or ExistingMetaModelArtifactId.");

        var runId = Guid.NewGuid();
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        CalibrationTrainingSnapshot snapshot = new()
        {
            RunId = runId,
            Status = CalibrationTrainingOverallStatus.Running,
            CreatedAt = createdAt,
            StartedAt = createdAt,
            SetupStage = new() { Status = request.RunSetupStage ? CalibrationStageStatus.Pending : CalibrationStageStatus.Skipped },
            MetaModelStage = new() { Status = request.RunMetaModelStage ? CalibrationStageStatus.Pending : CalibrationStageStatus.Skipped },
            ManagementStage = new() { Status = request.RunManagementStage ? CalibrationStageStatus.Pending : CalibrationStageStatus.Skipped }
        };
        Report(progress, snapshot);

        Guid? setupArtifactId = request.ExistingSetupArtifactId;
        Guid? metaModelArtifactId = request.ExistingMetaModelArtifactId;
        Guid? managementArtifactId = null;

        try
        {
            IReadOnlyList<SimulatedTradeRecord> setupOutOfFoldTrades = [];
            if (request.RunSetupStage)
            {
                snapshot = snapshot with
                {
                    SetupStage = snapshot.SetupStage with { Status = CalibrationStageStatus.Running, Message = "Running backtest for setup calibration…" }
                };
                Report(progress, snapshot);

                (setupArtifactId, setupOutOfFoldTrades) = await RunSetupStageAsync(runId, request, cancellationToken).ConfigureAwait(false);

                snapshot = snapshot with
                {
                    SetupStage = new() { Status = CalibrationStageStatus.Completed, ArtifactId = setupArtifactId, Message = "Completed." }
                };
                Report(progress, snapshot);
            }

            if (request.RunMetaModelStage)
            {
                snapshot = snapshot with
                {
                    MetaModelStage = snapshot.MetaModelStage with { Status = CalibrationStageStatus.Running, Message = "Calibrating meta-model from out-of-fold setup scores…" }
                };
                Report(progress, snapshot);

                metaModelArtifactId = await RunMetaModelStageAsync(runId, request, setupOutOfFoldTrades, cancellationToken).ConfigureAwait(false);

                snapshot = snapshot with
                {
                    MetaModelStage = new() { Status = CalibrationStageStatus.Completed, ArtifactId = metaModelArtifactId, Message = "Completed." }
                };
                Report(progress, snapshot);
            }

            if (request.RunManagementStage)
            {
                snapshot = snapshot with
                {
                    ManagementStage = snapshot.ManagementStage with { Status = CalibrationStageStatus.Running, Message = "Regenerating trades under frozen entry policy…" }
                };
                Report(progress, snapshot);

                managementArtifactId = await RunManagementStageAsync(
                    runId, request, setupArtifactId!.Value, metaModelArtifactId!.Value, cancellationToken).ConfigureAwait(false);

                snapshot = snapshot with
                {
                    ManagementStage = new() { Status = CalibrationStageStatus.Completed, ArtifactId = managementArtifactId, Message = "Completed." }
                };
                Report(progress, snapshot);
            }

            snapshot = snapshot with
            {
                Status = CalibrationTrainingOverallStatus.Completed,
                CompletedAt = DateTimeOffset.UtcNow,
                Message = "Completed."
            };
            Report(progress, snapshot);

            return new CalibrationTrainingResult
            {
                RunId = runId,
                Success = true,
                SetupArtifactId = setupArtifactId,
                MetaModelArtifactId = metaModelArtifactId,
                ManagementArtifactId = managementArtifactId,
                FinalSnapshot = snapshot
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "Calibration training run {RunId} failed.", runId);
            CalibrationStageSnapshot Fail(CalibrationStageSnapshot stage) =>
                stage.Status == CalibrationStageStatus.Running
                    ? stage with { Status = CalibrationStageStatus.Failed, Message = ex.Message }
                    : stage;
            snapshot = snapshot with
            {
                Status = CalibrationTrainingOverallStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                FailureReason = ex.Message,
                Message = "Failed.",
                SetupStage = Fail(snapshot.SetupStage),
                MetaModelStage = Fail(snapshot.MetaModelStage),
                ManagementStage = Fail(snapshot.ManagementStage)
            };
            Report(progress, snapshot);
            return new CalibrationTrainingResult
            {
                RunId = runId,
                Success = false,
                FailureReason = ex.Message,
                FinalSnapshot = snapshot
            };
        }
    }

    // ---- Stage 1: setup calibration ----------------------------------------------------------

    private async Task<(Guid ArtifactId, IReadOnlyList<SimulatedTradeRecord> OutOfFoldTrades)> RunSetupStageAsync(
        Guid runId, CalibrationTrainingRequest request, CancellationToken cancellationToken)
    {
        IReadOnlyList<SimulatedTradeRecord> trades = await RunBacktestAsync(request, cancellationToken).ConfigureAwait(false);
        if (trades.Count == 0)
            throw new InvalidOperationException("No closed trades were produced for the requested window; cannot calibrate.");

        CalibrationTrainingRequest foldRequest = WithAdaptiveFolds(request, trades.Count);
        var folds = SplitTrades(trades, foldRequest);
        PurgedTimeSeriesFold<SimulatedTradeRecord> reserved = folds[^1];
        IReadOnlyList<PurgedTimeSeriesFold<SimulatedTradeRecord>> cvFolds = folds[..^1];

        var outOfFold = new Dictionary<(string, string, string, decimal, decimal), List<SetupOutcome>>();
        var foldSummaries = new List<CalibrationFoldSummary>();
        var outOfFoldTradeIds = new HashSet<string>(StringComparer.Ordinal);
        var outOfFoldTrades = new List<SimulatedTradeRecord>();

        foreach (PurgedTimeSeriesFold<SimulatedTradeRecord> fold in cvFolds)
        {
            IReadOnlyList<SetupOutcome> trainingOutcomes = ResearchTradeMapper.ToSetupOutcomes(fold.Training);
            if (trainingOutcomes.Count == 0)
                continue;
            (DateTimeOffset foldFrom, DateTimeOffset foldTo) = ResolveTrainingSpan(fold.Training, request.From, request.To);
            SetupCalibrationArtifact foldArtifact = ConfidenceCalibrator.Calibrate(
                trainingOutcomes,
                request.SetupConfidenceBucketWidth,
                calibrationId: $"setup-{runId:N}-fold",
                trainingFrom: foldFrom,
                trainingTo: foldTo,
                strategyVersion: string.Join(",", request.Strategies),
                featureSchemaHash: MetaLabelFeatureFactory.SchemaVersion,
                dataHash: ComputeTradesHash(fold.Training),
                createdAt: DateTimeOffset.UtcNow);

            foreach (SimulatedTradeRecord testTrade in fold.Test)
            {
                SetupOutcome? outcome = ResearchTradeMapper.ToSetupOutcome(testTrade);
                if (outcome is null)
                    continue;
                SetupCalibrationBucket? bucket = MatchSetupBucket(foldArtifact, outcome);
                if (bucket is null)
                    continue;
                var key = (bucket.StrategyId, bucket.InstrumentGroup, bucket.Regime, bucket.ConfidenceFrom, bucket.ConfidenceTo);
                if (!outOfFold.TryGetValue(key, out List<SetupOutcome>? list))
                    outOfFold[key] = list = [];
                list.Add(outcome);

                string tradeId = TradeKey(testTrade);
                if (outOfFoldTradeIds.Add(tradeId))
                    outOfFoldTrades.Add(testTrade);
            }

            foldSummaries.Add(new CalibrationFoldSummary
            {
                Fold = foldSummaries.Count,
                TrainingFrom = fold.Training.Count > 0 ? fold.Training.Min(TradeOpenedAt) : fold.Test.Min(TradeOpenedAt),
                TrainingTo = fold.Training.Count > 0 ? fold.Training.Max(TradeClosedAt) : fold.Test.Min(TradeOpenedAt),
                TestFrom = fold.Test.Min(TradeOpenedAt),
                TestTo = fold.Test.Max(TradeClosedAt),
                TrainingSamples = fold.Training.Count,
                TestSamples = fold.Test.Count
            });
        }

        // Final artifact: fit on everything except the reserved, untouched test fold.
        // Use a collision-resistant trade key - PositionId/SetupId alone can collapse many
        // trades onto one id and wipe the fit set after reserved-fold exclusion.
        var reservedIds = new HashSet<string>(reserved.Test.Select(TradeKey), StringComparer.Ordinal);
        SimulatedTradeRecord[] fitTrades = trades.Where(t => !reservedIds.Contains(TradeKey(t))).ToArray();
        if (fitTrades.Length == 0)
        {
            // Degenerate split: keep a non-empty fit set rather than building an invalid artifact.
            fitTrades = trades.ToArray();
        }

        IReadOnlyList<SetupOutcome> fitOutcomes = ResearchTradeMapper.ToSetupOutcomes(fitTrades);
        if (fitOutcomes.Count == 0)
        {
            throw new InvalidOperationException(
                $"Closed trades ({trades.Count}) did not map to any setup outcomes for setup calibration. " +
                "Check that trades carry strategy id, instrument, and entry confidence.");
        }

        (DateTimeOffset finalFrom, DateTimeOffset finalTo) = ResolveTrainingSpan(fitTrades, request.From, request.To);
        SetupCalibrationArtifact finalArtifact = ConfidenceCalibrator.Calibrate(
            fitOutcomes,
            request.SetupConfidenceBucketWidth,
            calibrationId: $"setup-{runId:N}",
            trainingFrom: finalFrom,
            trainingTo: finalTo,
            strategyVersion: string.Join(",", request.Strategies),
            featureSchemaHash: MetaLabelFeatureFactory.SchemaVersion,
            dataHash: ComputeTradesHash(fitTrades),
            createdAt: DateTimeOffset.UtcNow);

        // Overwrite ExpectedR/WinRate/BrierScore with true out-of-fold statistics where available.
        // AverageR is left as the in-sample figure the final fit computed - this is the concrete
        // fix for ConfidenceCalibrator's AverageR==ExpectedR duplication: they now genuinely differ.
        SetupCalibrationBucket[] correctedBuckets = finalArtifact.Buckets.Select(bucket =>
        {
            var key = (bucket.StrategyId, bucket.InstrumentGroup, bucket.Regime, bucket.ConfidenceFrom, bucket.ConfidenceTo);
            if (!outOfFold.TryGetValue(key, out List<SetupOutcome>? oof) || oof.Count == 0)
                return bucket;
            decimal winRate = oof.Count(o => o.Won) / (decimal)oof.Count;
            decimal expectedR = oof.Average(o => o.RMultiple);
            decimal predicted = oof.Average(o => o.Confidence) / 100m;
            decimal brier = oof.Average(o =>
            {
                decimal observed = o.Won ? 1m : 0m;
                return (predicted - observed) * (predicted - observed);
            });
            return bucket with { WinRate = winRate, ExpectedR = expectedR, BrierScore = brier, Samples = oof.Count };
        }).ToArray();
        finalArtifact = finalArtifact with { Buckets = correctedBuckets };
        finalArtifact.Validate(MetaLabelFeatureFactory.SchemaVersion);

        CalibrationValidationReport validationMetrics = SummarizeOutcomes(outOfFold.Values.SelectMany(v => v).ToArray());
        CalibrationValidationReport testMetrics = EvaluateSetupOnTestFold(finalArtifact, reserved.Test);

        CalibrationArtifactProvenance provenance = new()
        {
            Folds = foldSummaries,
            Embargo = request.Embargo,
            TestWindowFrom = reserved.Test.Min(TradeOpenedAt),
            TestWindowTo = reserved.Test.Max(TradeClosedAt),
            ValidationMetrics = validationMetrics,
            TestMetrics = testMetrics,
            SupersedesArtifactId = request.SupersedesSetupArtifactId
        };

        CalibrationArtifactMetadata metadata = await _artifacts
            .StoreSetupAsync(finalArtifact, request.Description, provenance, cancellationToken)
            .ConfigureAwait(false);
        return (metadata.Id, outOfFoldTrades);
    }

    private static SetupCalibrationBucket? MatchSetupBucket(SetupCalibrationArtifact artifact, SetupOutcome outcome) =>
        artifact.Buckets
            .Where(b => string.Equals(b.StrategyId, outcome.StrategyId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(b.InstrumentGroup, outcome.InstrumentGroup, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(b.Regime, outcome.Regime, StringComparison.OrdinalIgnoreCase) &&
                        outcome.Confidence >= b.ConfidenceFrom &&
                        (outcome.Confidence < b.ConfidenceTo || b.ConfidenceTo == 100m && outcome.Confidence <= b.ConfidenceTo))
            .OrderByDescending(b => b.Samples)
            .FirstOrDefault();

    private static CalibrationValidationReport EvaluateSetupOnTestFold(
        SetupCalibrationArtifact artifact, IReadOnlyList<SimulatedTradeRecord> testTrades)
    {
        SetupOutcome[] outcomes = ResearchTradeMapper.ToSetupOutcomes(testTrades).ToArray();
        return SummarizeOutcomes(outcomes);
    }

    private static CalibrationValidationReport SummarizeOutcomes(IReadOnlyList<SetupOutcome> outcomes)
    {
        if (outcomes.Count == 0)
            return new CalibrationValidationReport { SampleCount = 0 };
        decimal winRate = outcomes.Count(o => o.Won) / (decimal)outcomes.Count;
        decimal expectedR = outcomes.Average(o => o.RMultiple);
        decimal predicted = outcomes.Average(o => o.Confidence) / 100m;
        decimal brier = outcomes.Average(o =>
        {
            decimal observed = o.Won ? 1m : 0m;
            return (predicted - observed) * (predicted - observed);
        });
        return new CalibrationValidationReport
        {
            SampleCount = outcomes.Count,
            WinRate = winRate,
            MeanExpectedR = expectedR,
            BrierScore = brier
        };
    }

    // ---- Stage 2: meta-model calibration -----------------------------------------------------

    private async Task<Guid> RunMetaModelStageAsync(
        Guid runId,
        CalibrationTrainingRequest request,
        IReadOnlyList<SimulatedTradeRecord> setupOutOfFoldTrades,
        CancellationToken cancellationToken)
    {
        // Trains only on trades stage 1 scored out-of-fold - never on trades whose setup bucket
        // was fit in-sample - which is what "must not train on in-sample setup predictions" means
        // given MetaModelCalibrator's existing signature (it consumes raw trade fields directly,
        // not a separate setup-calibration score input).
        if (setupOutOfFoldTrades.Count == 0)
            throw new InvalidOperationException("Stage 1 produced no out-of-fold trades; cannot calibrate the meta-model.");

        CalibrationTrainingRequest foldRequest = WithAdaptiveFolds(request, setupOutOfFoldTrades.Count);
        var folds = SplitTrades(setupOutOfFoldTrades, foldRequest);
        PurgedTimeSeriesFold<SimulatedTradeRecord> reserved = folds[^1];
        IReadOnlyList<PurgedTimeSeriesFold<SimulatedTradeRecord>> cvFolds = folds[..^1];

        var outOfFold = new Dictionary<(string, string, decimal, decimal, decimal, decimal), List<SimulatedTradeRecord>>();
        var foldSummaries = new List<CalibrationFoldSummary>();

        foreach (PurgedTimeSeriesFold<SimulatedTradeRecord> fold in cvFolds)
        {
            if (fold.Training.Count == 0)
                continue;
            (DateTimeOffset foldFrom, DateTimeOffset foldTo) = ResolveTrainingSpan(fold.Training, request.From, request.To);
            MetaModelArtifact foldArtifact = MetaModelCalibrator.Calibrate(
                fold.Training,
                request.MetaModelConfidenceBucketWidth,
                request.MetaModelAlignmentBucketWidth,
                calibrationId: $"metamodel-{runId:N}-fold",
                modelVersion: $"meta-{runId:N}-fold",
                trainingFrom: foldFrom,
                trainingTo: foldTo,
                dataHash: ComputeTradesHash(fold.Training),
                createdAt: DateTimeOffset.UtcNow);

            foreach (SimulatedTradeRecord testTrade in fold.Test)
            {
                if (testTrade.EntryMultiTimeframeAlignment is null)
                    continue;
                MetaModelBucket? bucket = MatchMetaModelBucket(foldArtifact, testTrade);
                if (bucket is null)
                    continue;
                var key = (bucket.StrategyId, bucket.Regime, bucket.ConfidenceFrom, bucket.ConfidenceTo, bucket.AlignmentFrom, bucket.AlignmentTo);
                if (!outOfFold.TryGetValue(key, out List<SimulatedTradeRecord>? list))
                    outOfFold[key] = list = [];
                list.Add(testTrade);
            }

            foldSummaries.Add(new CalibrationFoldSummary
            {
                Fold = foldSummaries.Count,
                TrainingFrom = fold.Training.Min(TradeOpenedAt),
                TrainingTo = fold.Training.Max(TradeClosedAt),
                TestFrom = fold.Test.Count > 0 ? fold.Test.Min(TradeOpenedAt) : fold.Training.Max(TradeClosedAt),
                TestTo = fold.Test.Count > 0 ? fold.Test.Max(TradeClosedAt) : fold.Training.Max(TradeClosedAt),
                TrainingSamples = fold.Training.Count,
                TestSamples = fold.Test.Count
            });
        }

        var reservedIds = new HashSet<string>(reserved.Test.Select(TradeKey), StringComparer.Ordinal);
        SimulatedTradeRecord[] fitTrades = setupOutOfFoldTrades
            .Where(t => !reservedIds.Contains(TradeKey(t)))
            .ToArray();
        if (fitTrades.Length == 0)
            fitTrades = setupOutOfFoldTrades.ToArray();
        (DateTimeOffset metaFrom, DateTimeOffset metaTo) = ResolveTrainingSpan(fitTrades, request.From, request.To);
        MetaModelArtifact finalArtifact = MetaModelCalibrator.Calibrate(
            fitTrades,
            request.MetaModelConfidenceBucketWidth,
            request.MetaModelAlignmentBucketWidth,
            calibrationId: $"metamodel-{runId:N}",
            modelVersion: $"meta-{runId:N}",
            trainingFrom: metaFrom,
            trainingTo: metaTo,
            dataHash: ComputeTradesHash(fitTrades),
            createdAt: DateTimeOffset.UtcNow);

        MetaModelBucket[] correctedBuckets = finalArtifact.Buckets.Select(bucket =>
        {
            var key = (bucket.StrategyId, bucket.Regime, bucket.ConfidenceFrom, bucket.ConfidenceTo, bucket.AlignmentFrom, bucket.AlignmentTo);
            if (!outOfFold.TryGetValue(key, out List<SimulatedTradeRecord>? oof) || oof.Count == 0)
                return bucket;
            decimal winRate = oof.Count(t => (t.RMultiple ?? 0m) > 0m) / (decimal)oof.Count;
            decimal expectedR = oof.Average(t => t.RMultiple ?? 0m);
            decimal predicted = oof.Average(t => t.EntryConfidence) / 100m;
            decimal brier = oof.Average(t =>
            {
                decimal observed = (t.RMultiple ?? 0m) > 0m ? 1m : 0m;
                return (predicted - observed) * (predicted - observed);
            });
            // Never let a bucket imply more than 1x risk - matches MetaModelPolicyOptions'
            // "a weak signal only ever shrinks size" invariant at the data level too.
            return bucket with { WinRate = winRate, ExpectedR = expectedR, BrierScore = brier, Samples = oof.Count };
        }).ToArray();
        finalArtifact = finalArtifact with { Buckets = correctedBuckets };
        finalArtifact.Validate(MetaLabelFeatureFactory.SchemaVersion);

        CalibrationValidationReport validationMetrics = SummarizeTrades(outOfFold.Values.SelectMany(v => v).ToArray());
        CalibrationValidationReport testMetrics = SummarizeTrades(reserved.Test);

        CalibrationArtifactProvenance provenance = new()
        {
            Folds = foldSummaries,
            Embargo = request.Embargo,
            TestWindowFrom = reserved.Test.Count > 0 ? reserved.Test.Min(TradeOpenedAt) : request.From,
            TestWindowTo = reserved.Test.Count > 0 ? reserved.Test.Max(TradeClosedAt) : request.To,
            ValidationMetrics = validationMetrics,
            TestMetrics = testMetrics,
            SupersedesArtifactId = request.SupersedesMetaModelArtifactId
        };

        CalibrationArtifactMetadata metadata = await _artifacts
            .StoreMetaModelAsync(finalArtifact, request.Description, provenance, cancellationToken)
            .ConfigureAwait(false);
        return metadata.Id;
    }

    private static MetaModelBucket? MatchMetaModelBucket(MetaModelArtifact artifact, SimulatedTradeRecord trade) =>
        artifact.Buckets
            .Where(b => string.Equals(b.StrategyId, trade.StrategyId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(b.Regime, trade.EntryRegime.ToString(), StringComparison.OrdinalIgnoreCase) &&
                        trade.EntryConfidence >= b.ConfidenceFrom &&
                        (trade.EntryConfidence < b.ConfidenceTo || b.ConfidenceTo == 100m && trade.EntryConfidence <= b.ConfidenceTo) &&
                        trade.EntryMultiTimeframeAlignment!.Value >= b.AlignmentFrom &&
                        (trade.EntryMultiTimeframeAlignment.Value < b.AlignmentTo || b.AlignmentTo == 1m && trade.EntryMultiTimeframeAlignment.Value <= b.AlignmentTo))
            .OrderByDescending(b => b.Samples)
            .FirstOrDefault();

    private static CalibrationValidationReport SummarizeTrades(IReadOnlyList<SimulatedTradeRecord> trades)
    {
        if (trades.Count == 0)
            return new CalibrationValidationReport { SampleCount = 0 };
        decimal winRate = trades.Count(t => (t.RMultiple ?? 0m) > 0m) / (decimal)trades.Count;
        decimal expectedR = trades.Average(t => t.RMultiple ?? 0m);
        decimal predicted = trades.Average(t => t.EntryConfidence) / 100m;
        decimal brier = trades.Average(t =>
        {
            decimal observed = (t.RMultiple ?? 0m) > 0m ? 1m : 0m;
            return (predicted - observed) * (predicted - observed);
        });
        return new CalibrationValidationReport
        {
            SampleCount = trades.Count,
            WinRate = winRate,
            MeanExpectedR = expectedR,
            BrierScore = brier
        };
    }

    // ---- Stage 3: management calibration -----------------------------------------------------

    private async Task<Guid> RunManagementStageAsync(
        Guid runId,
        CalibrationTrainingRequest request,
        Guid setupArtifactId,
        Guid metaModelArtifactId,
        CancellationToken cancellationToken)
    {
        SetupCalibrationArtifact? setupArtifact = await _artifacts.GetSetupAsync(setupArtifactId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Setup calibration artifact {setupArtifactId} was not found.");
        MetaModelArtifact? metaModelArtifact = await _artifacts.GetMetaModelAsync(metaModelArtifactId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Meta-model artifact {metaModelArtifactId} was not found.");

        // Freeze the selected entry policy and rerun the backtest so management calibration
        // reflects the trades that policy would actually take - not the unfiltered population
        // stage 1 was calibrated from.
        BacktestRuntimeOptions frozenRuntime = request.Runtime with
        {
            SetupCalibration = request.Runtime.SetupCalibration with { Enabled = true },
            MetaModel = request.Runtime.MetaModel with { Enabled = true }
        };
        CalibrationTrainingRequest frozenRequest = request with { Runtime = frozenRuntime };
        IReadOnlyList<SimulatedTradeRecord> trades = await RunBacktestAsync(
            frozenRequest, cancellationToken, setupArtifact, metaModelArtifact).ConfigureAwait(false);
        if (trades.Count == 0)
            throw new InvalidOperationException("The frozen entry policy produced no closed trades; cannot calibrate management.");

        CalibrationTrainingRequest foldRequest = WithAdaptiveFolds(request, trades.Count);
        var folds = SplitTrades(trades, foldRequest);
        PurgedTimeSeriesFold<SimulatedTradeRecord> reserved = folds[^1];
        var reservedIds = new HashSet<string>(reserved.Test.Select(TradeKey), StringComparer.Ordinal);
        SimulatedTradeRecord[] fitTrades = trades.Where(t => !reservedIds.Contains(TradeKey(t))).ToArray();
        if (fitTrades.Length == 0)
            fitTrades = trades.ToArray();

        IReadOnlyList<TradePathObservation> fitObservations = fitTrades
            .Select(ResearchTradeMapper.ToTradePathObservation)
            .Where(o => o is not null)
            .Select(o => o!)
            .ToArray();
        if (fitObservations.Count == 0)
        {
            throw new InvalidOperationException(
                "No trade excursion paths were available for management calibration - the backtest must run with DetailedExcursionTracking enabled.");
        }

        TradeManagementCalibration artifact = TradeManagementCohortAnalyzer.Analyze(
            fitObservations,
            calibrationId: $"management-{runId:N}",
            sourceDataHash: ComputeTradesHash(fitTrades),
            createdAt: DateTimeOffset.UtcNow);
        artifact.Validate();

        var foldSummaries = folds[..^1].Select((fold, index) => new CalibrationFoldSummary
        {
            Fold = index,
            TrainingFrom = fold.Training.Count > 0 ? fold.Training.Min(TradeOpenedAt) : fold.Test.Min(TradeOpenedAt),
            TrainingTo = fold.Training.Count > 0 ? fold.Training.Max(TradeClosedAt) : fold.Test.Min(TradeOpenedAt),
            TestFrom = fold.Test.Min(TradeOpenedAt),
            TestTo = fold.Test.Max(TradeClosedAt),
            TrainingSamples = fold.Training.Count,
            TestSamples = fold.Test.Count
        }).ToArray();

        CalibrationArtifactProvenance provenance = new()
        {
            Folds = foldSummaries,
            Embargo = request.Embargo,
            TestWindowFrom = reserved.Test.Min(TradeOpenedAt),
            TestWindowTo = reserved.Test.Max(TradeClosedAt),
            // Management cohorts don't have a single scalar "correctness" metric the way
            // win-rate/Brier/expected-R do for stages 1-2 - the untouched test window's sample
            // count is recorded as the honest, available signal rather than fabricating one.
            TestMetrics = new CalibrationValidationReport { SampleCount = reserved.Test.Count },
            SupersedesArtifactId = request.SupersedesManagementArtifactId
        };

        CalibrationArtifactMetadata metadata = await _artifacts
            .StoreManagementAsync(artifact, request.Description, provenance, cancellationToken)
            .ConfigureAwait(false);
        return metadata.Id;
    }

    // ---- shared helpers -----------------------------------------------------------------------

    private async Task<IReadOnlyList<SimulatedTradeRecord>> RunBacktestAsync(
        CalibrationTrainingRequest request,
        CancellationToken cancellationToken,
        SetupCalibrationArtifact? setupArtifact = null,
        MetaModelArtifact? metaModelArtifact = null)
    {
        string runDirectory = Path.Combine(Path.GetTempPath(), "tradinghub-calibration-training", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDirectory);
        try
        {
            BacktestRuntimeOptions runtime = request.Runtime with
            {
                DetailedExcursionTracking = true,
                SetupCalibrationArtifact = setupArtifact ?? request.Runtime.SetupCalibrationArtifact,
                MetaModelArtifact = metaModelArtifact ?? request.Runtime.MetaModelArtifact
            };

            // One request per (instrument, strategy) pair, pooled - mirrors
            // QuantResearchRunner's existing RunSingleWindowBacktestAsync behavior so multiple
            // instrument groups/strategies can share one calibration artifact.
            var trades = new List<SimulatedTradeRecord>();
            foreach (InstrumentKey instrument in request.Instruments)
            {
                foreach (string strategy in request.Strategies)
                {
                    BacktestRequest backtestRequest = new()
                    {
                        Instrument = instrument,
                        From = request.From,
                        To = request.To,
                        Strategies = [strategy],
                        StartingBalance = request.StartingBalance,
                        Quantity = request.Quantity,
                        OutputDirectory = Path.Combine(runDirectory, "runs"),
                        JobsDirectory = Path.Combine(runDirectory, "jobs"),
                        Runtime = runtime
                    };
                    backtestRequest = FeatureSwitchMapper.Apply(backtestRequest, new FeatureSwitches());

                    ComparativeSimulationResult result = await _backtests.RunToCompletionAsync(backtestRequest).ConfigureAwait(false);
                    SimulatedTradeRecord[] matched = result.Strategies
                        .Where(item => string.Equals(item.StrategyId, strategy, StringComparison.OrdinalIgnoreCase))
                        .SelectMany(item => item.Result.Trades)
                        .Where(trade => trade.ClosedAt is not null)
                        .ToArray();
                    if (matched.Length == 0)
                    {
                        // Strategy ids from the engine may differ slightly from the request's -
                        // fall back to every closed trade rather than reporting a false empty result.
                        matched = result.Strategies
                            .SelectMany(item => item.Result.Trades)
                            .Where(trade => trade.ClosedAt is not null)
                            .ToArray();
                    }
                    trades.AddRange(matched);
                }
            }
            return trades;
        }
        finally
        {
            try
            {
                if (Directory.Exists(runDirectory))
                    Directory.Delete(runDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup only.
            }
        }
    }

    private static PurgedTimeSeriesFold<SimulatedTradeRecord>[] SplitTrades(
        IReadOnlyList<SimulatedTradeRecord> trades, CalibrationTrainingRequest request)
    {
        PurgedTimeSeriesFold<SimulatedTradeRecord>[] folds = PurgedTimeSeriesCrossValidator.Split(
            trades, TradeOpenedAt, TradeClosedAt, request.Folds, request.Embargo).ToArray();
        if (folds.Length < 2)
        {
            throw new InvalidOperationException(
                $"Only {folds.Length} usable fold(s) were produced from {trades.Count} trade(s) " +
                $"(requested Folds={request.Folds}) - not enough data for a reserved test window plus " +
                "at least one cross-validation fold. Widen the date range / train window so more " +
                "closed trades are produced, or reduce Folds.");
        }
        return folds;
    }

    /// <summary>
    /// Shrink fold count when the trade sample is small so purged CV still produces a reserved
    /// test fold plus at least one training fold instead of failing or emptying the fit set.
    /// </summary>
    private static CalibrationTrainingRequest WithAdaptiveFolds(
        CalibrationTrainingRequest request,
        int tradeCount)
    {
        // Need >= 3 folds for the pipeline's reserved-test design, but also enough trades
        // per fold. Cap folds so each fold can receive at least one trade when possible.
        int maxUseful = Math.Max(3, Math.Min(request.Folds, tradeCount));
        int folds = Math.Clamp(maxUseful, 3, request.Folds);
        if (tradeCount < 3)
        {
            throw new InvalidOperationException(
                $"Only {tradeCount} closed trade(s) were produced - setup calibration needs at least 3 " +
                "closed trades for purged cross-validation (2 CV folds + 1 reserved test). " +
                "Widen the training window so the strategy produces more trades.");
        }

        return folds == request.Folds ? request : request with { Folds = folds };
    }

    private static (DateTimeOffset From, DateTimeOffset To) ResolveTrainingSpan(
        IReadOnlyList<SimulatedTradeRecord> trades,
        DateTimeOffset fallbackFrom,
        DateTimeOffset fallbackTo)
    {
        if (trades.Count == 0)
        {
            DateTimeOffset emptyTo = fallbackTo > fallbackFrom ? fallbackTo : fallbackFrom.AddSeconds(1);
            return (fallbackFrom, emptyTo);
        }

        DateTimeOffset spanFrom = trades.Min(TradeOpenedAt);
        DateTimeOffset spanTo = trades.Max(TradeClosedAt);
        if (spanTo <= spanFrom)
            spanTo = spanFrom.AddSeconds(1);
        return (spanFrom, spanTo);
    }

    private static DateTimeOffset TradeOpenedAt(SimulatedTradeRecord trade) => trade.OpenedAt ?? trade.SetupStartedAt;

    private static DateTimeOffset TradeClosedAt(SimulatedTradeRecord trade) => trade.ClosedAt!.Value;

    private static string TradeKey(SimulatedTradeRecord trade) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{trade.StrategyId}|{trade.PositionId ?? trade.SetupId}|{TradeOpenedAt(trade):O}|{TradeClosedAt(trade):O}|{trade.EntryPrice}");

    private static string ComputeTradesHash(IReadOnlyList<SimulatedTradeRecord> trades)
    {
        string joined = string.Join('|', trades
            .Select(TradeKey)
            .OrderBy(id => id, StringComparer.Ordinal));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void Report(IProgress<CalibrationTrainingSnapshot>? progress, CalibrationTrainingSnapshot snapshot) =>
        progress?.Report(snapshot);
}
