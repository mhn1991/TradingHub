using Agent.Configuration;
using Agent.Strategies;
using Brokers.Models;
using Simulator.Models;

namespace QuantResearch.Training.Pipeline;

/// <summary>
/// One staged, leakage-safe training run covering setup calibration, meta-model calibration,
/// and management calibration (in that order - see <see cref="CalibrationTrainingPipeline"/>).
/// </summary>
public sealed record CalibrationTrainingRequest
{
    /// <summary>
    /// One backtest is run per (instrument, strategy) pair and all resulting trades are pooled
    /// into the same calibration run - mirrors <c>QuantResearchPlan</c>'s Instruments/Strategies
    /// shape so multiple instrument groups/strategies can share one artifact, matching how
    /// calibration cohorts are already keyed (StrategyId, InstrumentGroup, Regime, ...).
    /// </summary>
    public required IReadOnlyList<InstrumentKey> Instruments { get; init; }
    public required IReadOnlyList<string> Strategies { get; init; }
    /// <summary>
    /// Exact definition used when a single experiment profile is calibrated. Null preserves
    /// pooled catalog-strategy training for existing research callers.
    /// </summary>
    public TradingAgentDefinition? AgentDefinition { get; init; }
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }
    public required BacktestRuntimeOptions Runtime { get; init; }
    public decimal StartingBalance { get; init; } = 100_000m;
    public decimal Quantity { get; init; } = 1_000m;

    /// <summary>
    /// AGENT-01: the remaining fields <see cref="Runtime"/>'s own
    /// <c>ResolveProgressiveStrategyOptions</c> needs beyond the runtime object itself, so a
    /// successful training run's promoted <c>AgentOptions</c> can be derived from these same
    /// settings instead of a caller-supplied value that can silently drift from what actually
    /// produced the training data. Defaults match <c>ProgressiveStrategyOptions</c>' own bare
    /// defaults, so a caller that doesn't set them gets identical behavior to before.
    /// </summary>
    public decimal MinimumRewardRisk { get; init; } = 1.5m;
    public PriceActionConfirmationMode PriceActionConfirmation { get; init; } = PriceActionConfirmationMode.Soft;
    public decimal MinimumPriceActionConfidence { get; init; } = 55m;
    public bool RejectStrongOpposingPriceAction { get; init; } = true;

    /// <summary>
    /// Total purged cross-validation folds. The last (most recent) fold's Test set is always
    /// reserved as the untouched final test window and excluded from bucket fitting; the
    /// remaining folds are used for out-of-fold validation. Must be &gt;= 3 (2 usable CV folds +
    /// 1 reserved test fold).
    /// </summary>
    public int Folds { get; init; } = 5;

    /// <summary>Purge/embargo applied around each fold's test boundary by <c>PurgedTimeSeriesCrossValidator</c>.</summary>
    public TimeSpan Embargo { get; init; } = TimeSpan.FromDays(1);

    public decimal SetupConfidenceBucketWidth { get; init; } = 5m;
    /// <summary>
    /// One full-range bucket by default (100 = confidence is not partitioned at all): raw
    /// EntryConfidence was measured to have ~zero correlation with realized R (Pearson r =
    /// -0.029, 2026-07-20 session), so partitioning cohorts by it just fragments sample size
    /// across MetaModelCalibrator's other, actually-discriminating dimensions
    /// (PlaybookId/MultiTimeframeAlignment/CciState/StructuralConfluenceState) for no benefit.
    /// Narrow this back down only once EntryConfidence itself is replaced with a signal that's
    /// been shown predictive.
    /// </summary>
    public decimal MetaModelConfidenceBucketWidth { get; init; } = 100m;
    public decimal MetaModelAlignmentBucketWidth { get; init; } = 0.25m;

    /// <summary>Minimum out-of-fold samples a bucket needs before its statistics are trusted; below this the run still succeeds but fewer buckets get non-trivial validation coverage.</summary>
    public int MinimumSamplesPerBucket { get; init; } = 30;

    /// <summary>Existing bundle candidate (if any) this run is meant to replace, recorded as lineage on the new artifacts.</summary>
    public Guid? SupersedesSetupArtifactId { get; init; }
    public Guid? SupersedesMetaModelArtifactId { get; init; }
    public Guid? SupersedesManagementArtifactId { get; init; }

    /// <summary>
    /// Which stages actually run in this call - all default true. A caller wanting today's
    /// single-command CLI behavior (e.g. just re-calibrate setup) sets the other two false.
    /// See <see cref="CalibrationTrainingPipeline"/> for the cross-stage rules this implies.
    /// </summary>
    public bool RunSetupStage { get; init; } = true;
    public bool RunMetaModelStage { get; init; } = true;
    public bool RunManagementStage { get; init; } = true;

    /// <summary>Required when <see cref="RunManagementStage"/> is true but <see cref="RunSetupStage"/> is false - the frozen setup artifact to build management trades against.</summary>
    public Guid? ExistingSetupArtifactId { get; init; }
    /// <summary>Required when <see cref="RunManagementStage"/> is true but <see cref="RunMetaModelStage"/> is false - the frozen meta-model artifact to build management trades against.</summary>
    public Guid? ExistingMetaModelArtifactId { get; init; }

    public string? Description { get; init; }

    public void Validate()
    {
        if (Instruments is null || Instruments.Count == 0)
            throw new ArgumentException("At least one instrument is required.");
        if (Strategies is null || Strategies.Count == 0 || Strategies.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("At least one non-empty strategy id is required.");
        if (AgentDefinition is not null)
        {
            AgentDefinition.Validate();
            if (Strategies.Count != 1 ||
                !TradingAgentTypeIds.TryParse(Strategies[0], out TradingAgentKind kind) ||
                kind != AgentDefinition.Kind)
            {
                throw new ArgumentException(
                    "AgentDefinition requires exactly one matching canonical strategy type.");
            }
        }
        if (From >= To)
            throw new ArgumentException("From must be earlier than To.");
        if (StartingBalance <= 0m || Quantity <= 0m)
            throw new ArgumentException("StartingBalance and Quantity must be positive.");
        if (Folds < 3)
            throw new ArgumentOutOfRangeException(nameof(Folds), "At least 3 folds are required (2 usable + 1 reserved test fold).");
        if (Embargo < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(Embargo));
        if (SetupConfidenceBucketWidth is <= 0m or > 100m || 100m % SetupConfidenceBucketWidth != 0m)
            throw new ArgumentOutOfRangeException(nameof(SetupConfidenceBucketWidth));
        if (MetaModelConfidenceBucketWidth is <= 0m or > 100m || 100m % MetaModelConfidenceBucketWidth != 0m)
            throw new ArgumentOutOfRangeException(nameof(MetaModelConfidenceBucketWidth));
        if (MetaModelAlignmentBucketWidth is <= 0m or > 1m || 1m % MetaModelAlignmentBucketWidth != 0m)
            throw new ArgumentOutOfRangeException(nameof(MetaModelAlignmentBucketWidth));
        if (MinimumSamplesPerBucket < 1)
            throw new ArgumentOutOfRangeException(nameof(MinimumSamplesPerBucket));
        ArgumentNullException.ThrowIfNull(Runtime);
    }
}
