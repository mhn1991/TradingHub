using System.Text.Json.Serialization;
using Agent.Configuration;
using Agent.Strategies;
using Agent.Strategies.DivergenceReversal;
using Agent.Strategies.StructuralConfluence;
using Agent.Strategies.StructuralConfluence.Playbooks;
using Brokers.Abstractions;
using Brokers.Models;
using ChartAnnotator.CurrencyStrength;
using ChartAnnotator.Engine;
using ChartAnnotator.MarketData;
using ChartAnnotator.NeoWave;
using ChartAnnotator.Value;
using RiskManager;
using RiskManager.Safety;
using RiskManager.Conditions;
using Simulator.MarketData;
using TradeManager;
using PortfolioManager.Correlation;
using PortfolioManager.CrossMarket;
using PortfolioManager.Risk;
using Simulator.Execution;
using Simulator.Financing;
using RiskManager.Calibration;
using TradingCore.Pipeline;

namespace Simulator.Models;

public enum StrategyExecutionMode
{
    Sequential,
    ParallelWorkers
}

public enum AnalysisSharingMode
{
    SharedImmutableSnapshots,
    IndependentPerStrategy
}

public enum StrategyFailurePolicy
{
    StopEntireComparison,
    StopFailedStrategyOnly
}

public enum AmbiguousIntrabarPolicy
{
    ConservativeStopFirst,
    OptimisticTargetFirst,
    NearestToOpenFirst
}

public enum FillModel
{
    HistoricalBidAsk,
    MidpointPlusConfiguredSpread,
    VariableSyntheticSpread,
    StressExecution
}

public enum SimulationAccountMode
{
    IndependentStrategyAccounts,
    SharedPortfolioAccount
}

public enum SimulationJobStatus
{
    Queued,
    PreparingData,
    DownloadingData,
    LoadingCache,
    WarmingUp,
    Running,
    Paused,
    Cancelling,
    Cancelled,
    Exporting,
    Completed,
    Failed
}

/// <summary>Validated runtime configuration for streamed comparative backtests.</summary>
public sealed record BacktestRuntimeOptions
{
    public SimulationAccountMode AccountMode { get; init; } = SimulationAccountMode.IndependentStrategyAccounts;
    /// <summary>Legacy alias for <see cref="ExecutionInterval"/>.</summary>
    public BarInterval BaseInterval
    {
        get => ExecutionInterval;
        init => ExecutionInterval = value;
    }

    /// <summary>Finest candle used for fills/stops/OCO/MFE.</summary>
    public BarInterval ExecutionInterval { get; init; } = BarInterval.Minutes(1);

    /// <summary>Smallest candle delivered to ChartAnnotator (default 1m).</summary>
    public BarInterval AnalysisBaseInterval { get; init; } = BarInterval.Minutes(1);

    public IReadOnlyList<BarInterval> AnalysisIntervals { get; init; } =
        RecommendedSimulationDefaults.AnalysisIntervals;

    /// <summary>
    /// Structural-confluence's own trigger/setup/context timeframe, used only when
    /// <see cref="BacktestRequest.ResolveAgentDefinition"/> builds a default (no explicit
    /// <c>AgentDefinitionOverride</c>) <c>StructuralConfluence</c> agent - setup/context mirror
    /// <c>Simulator.Calibration.StandardTimeframeTopologyFactory</c>'s own defaults so a plain
    /// backtest and a calibration request agree on the same 15m/1h default. Independent of
    /// <see cref="ExecutionInterval"/>, which only governs simulation fill precision and raw-candle
    /// fetch/aggregation base - it no longer doubles as the agent's trigger interval.
    /// </summary>
    public BarInterval StructuralTriggerInterval { get; init; } = BarInterval.Minutes(5);

    public BarInterval StructuralSetupInterval { get; init; } = Simulator.Calibration.StandardTimeframeTopologyFactory.DefaultSetupInterval;

    public BarInterval StructuralContextInterval { get; init; } = Simulator.Calibration.StandardTimeframeTopologyFactory.DefaultContextInterval;

    public SimulationPrecisionMode PrecisionMode { get; init; } = SimulationPrecisionMode.Fast;
    public HistoricalDataSourceKind SourceKind { get; init; } = HistoricalDataSourceKind.OandaCandles;
    public string? ImportedCandlePath { get; init; }

    public ProgressiveStrategyTimeframes StrategyTimeframes { get; init; } =
        RecommendedSimulationDefaults.StrategyTimeframes;

    public int SourcePageSize { get; init; } = 5_000;
    public int PrefetchCapacity { get; init; } = 20_000;
    public int PrefetchLowWatermark { get; init; } = 5_000;

    public int WarmupDays { get; init; } = RecommendedSimulationDefaults.WarmupDays;
    public int? WarmupBaseCandleCount { get; init; }

    public StrategyExecutionMode StrategyExecutionMode { get; init; } = StrategyExecutionMode.ParallelWorkers;
    public int StrategyChannelCapacity { get; init; } = 4;
    public int MaximumParallelStrategies { get; init; } = 4;
    public StrategyFailurePolicy StrategyFailurePolicy { get; init; } = StrategyFailurePolicy.StopEntireComparison;

    public AnalysisSharingMode AnalysisSharingMode { get; init; } = AnalysisSharingMode.SharedImmutableSnapshots;
    public AmbiguousIntrabarPolicy AmbiguousIntrabarPolicy { get; init; } = AmbiguousIntrabarPolicy.ConservativeStopFirst;

    /// <summary>
    /// Historical bid/ask fill model is not yet wired to OANDA price components.
    /// Keep false so results do not claim HistoricalBidAsk incorrectly.
    /// </summary>
    public bool UseHistoricalBidAsk { get; init; }
    public bool RefreshCache { get; init; }
    public bool NoCache { get; init; }
    public int ReplayChunkSize { get; init; } = 5_000;
    public int ExecutionDetailPreEntryFrames { get; init; } = 120;
    public int ExecutionDetailPostExitFrames { get; init; } = 120;
    public int ProgressPublishIntervalMilliseconds { get; init; } = 500;

    public int CandleCapacity { get; init; } = 5_000;
    public int LedgerCapacity { get; init; } = 100_000;
    public int OrderEventCapacity { get; init; } = 16_384;

    public BaseCandleGapPolicy BaseCandleGapPolicy { get; init; } = BaseCandleGapPolicy.ResetIncompleteBuckets;

    public PositionManagementOptions LegacyPositionManagement { get; init; } =
        PositionManagementOptions.LegacyDefaults;

    public PositionManagementOptions ImprovedPositionManagement { get; init; } =
        PositionManagementOptions.ImprovedDefaults;

    public PositionManagementOptions StructuralPositionManagement { get; init; } =
        PositionManagementOptions.StructuralDefaults;

    /// <summary>
    /// Account-level safety and daily equity-profit protection. Null thresholds leave
    /// the corresponding rule disabled.
    /// </summary>
    public TradingSafetyOptions SafetyOptions { get; init; } = new();

    /// <summary>Order-size calculation and capital/margin reservation policy.</summary>
    public PositionSizingOptions PositionSizing { get; init; } =
        RecommendedSimulationDefaults.PositionSizing;

    /// <summary>
    /// ChartAnnotator indicator/regime-classification configuration (Efficiency
    /// Ratio, Donchian, MarketRegimeClassifier, etc.). Previously never reached the
    /// live engine - see StreamingComparativeEngineOptions.AnnotationOptions wiring.
    /// </summary>
    public ChartAnnotationOptions AnnotationOptions { get; init; } = new();

    /// <summary>
    /// Regime-based strategy routing (allow/block entries, risk multiplier) applied
    /// to both Legacy and Improved. Disabled by default.
    /// </summary>
    public MarketRegimePolicyOptions MarketRegimeRouting { get; init; } = new();
    /// <summary>
    /// Optional, independent value-location evidence (spec §13.3), applied to both
    /// Legacy and Improved. Disabled by default.
    /// </summary>
    public ValueLocationEvidenceOptions ValueLocationEvidence { get; init; } = new();
    /// <summary>
    /// Optional currency-strength confluence/opposition evidence (2026-07-16 agent
    /// decision-quality pass), applied to both Legacy and Improved. Disabled by default -
    /// also depends on <see cref="CurrencyStrength"/> being separately enabled with
    /// baskets configured, or the strategy's context never receives a snapshot to evaluate.
    /// </summary>
    public CurrencyStrengthEvidenceOptions CurrencyStrengthEvidence { get; init; } = new();
    public NeoWaveEvidenceOptions NeoWaveEvidence { get; init; } = new();
    public BarInterval? NeoWaveEvidenceInterval { get; init; }
    /// <summary>
    /// RSI-relationship and Bollinger-context signal evidence, applied to both Legacy
    /// and Improved. Previously unreachable from any Runtime/CLI/Dashboard path -
    /// <see cref="Agent.Strategies.ProgressiveStrategyOptions.RsiBollingerSignals"/> was
    /// always hard-defaulted, regardless of what a request asked for.
    /// </summary>
    public RsiBollingerSignalOptions RsiBollingerSignals { get; init; } = new();
    /// <summary>
    /// Enables ADX/DMI directional confirmation. Enabled by default (existing behavior);
    /// disabling removes the DMI opinion entirely rather than inverting it.
    /// </summary>
    public bool DmiConfirmationEnabled { get; init; } = true;
    public TradingConditionOptions TradingConditions { get; init; } = new();
    public PortfolioRiskOptions PortfolioRisk { get; init; } = new();
    public CorrelationRiskOptions CorrelationRisk { get; init; } = new();
    public CurrencyStrengthOptions CurrencyStrength { get; init; } = new();
    public AdaptiveRiskOptions AdaptiveRisk { get; init; } = new();
    public SetupCalibrationPolicyOptions SetupCalibration { get; init; } = new();
    public SetupCalibrationArtifact? SetupCalibrationArtifact { get; init; }
    public RegimeManagementOptions RegimeManagement { get; init; } = new();
    public TradeManagementCalibrationOptions ManagementCalibration { get; init; } = new();
    public TradeManagementCalibration? ManagementCalibrationArtifact { get; init; }
    /// <summary>
    /// Statistical meta-model policy (audit §17). <c>BacktestApplicationService</c>
    /// uses this to populate <c>StreamingComparativeEngineOptions.MetaLabelModel</c> - before
    /// the 2026-07-16 pass this was never set from any Runtime field, so meta-labeling was
    /// 100% dead in every production run even when other plumbing suggested it was wired.
    /// </summary>
    public Simulator.Calibration.MetaModelPolicyOptions MetaModel { get; init; } = new();
    public Simulator.Calibration.MetaModelArtifact? MetaModelArtifact { get; init; }
    public ExecutionModelOptions Execution { get; init; } = new();
    public FinancingOptions Financing { get; init; } = new();
    /// <summary>
    /// Records a bar-by-bar MFE/MAE excursion path per trade (<see cref="SimulatedTradeRecord.ExcursionPath"/>),
    /// needed for management-calibration research but not by default trading - off by
    /// default, zero size/behavior change for every run that doesn't opt in.
    /// </summary>
    public bool DetailedExcursionTracking { get; init; }

    public SimulationTimeframeOptions ToTimeframeOptions() => new()
    {
        ExecutionInterval = ExecutionInterval,
        AnalysisBaseInterval = AnalysisBaseInterval,
        AnalysisIntervals = AnalysisIntervals
    };

    /// <summary>
    /// AGENT-01: the shared derivation body behind <see cref="BacktestRequest.ResolveProgressiveStrategyOptions"/>,
    /// extracted so any caller with a <see cref="BacktestRuntimeOptions"/> - not only a full
    /// <see cref="BacktestRequest"/> - can derive the exact environment-neutral Agent options
    /// that runtime implies, instead of falling back to a bare <c>new ProgressiveStrategyOptions()</c>
    /// that silently drifts from what a backtest driven by this same runtime would actually use.
    /// </summary>
    public ProgressiveStrategyOptions ResolveProgressiveStrategyOptions(
        decimal quantity,
        decimal minimumRewardRisk,
        PriceActionConfirmationMode priceActionConfirmation,
        decimal minimumPriceActionConfidence,
        bool rejectStrongOpposingPriceAction)
    {
        ProgressiveStrategyTimeframes tf = StrategyTimeframes;
        var options = new ProgressiveStrategyOptions
        {
            TrendInterval = tf.TrendInterval,
            SecondaryTrendIntervals = tf.SecondaryTrendIntervals,
            SetupIntervals = tf.SetupIntervals,
            ConfirmationInterval = tf.ConfirmationInterval,
            AdditionalConfirmationIntervals = tf.AdditionalConfirmationIntervals,
            EntryInterval = tf.EntryInterval,
            MinimumSecondaryTrendAlignments = tf.MinimumSecondaryTrendAlignments,
            MinimumSetupAlignments = tf.MinimumSetupAlignments,
            MinimumConfirmationAlignments = tf.MinimumConfirmationAlignments,
            StrongOppositionVeto = tf.StrongOppositionVeto,
            Quantity = quantity,
            MinimumRewardRisk = minimumRewardRisk,
            PriceActionConfirmation = priceActionConfirmation,
            MinimumPriceActionConfidence = minimumPriceActionConfidence,
            RejectStrongOpposingPriceAction = rejectStrongOpposingPriceAction,
            MarketRegime = MarketRegimeRouting,
            ValueLocationEvidence = ValueLocationEvidence,
            CurrencyStrengthEvidence = CurrencyStrengthEvidence,
            NeoWaveEvidence = NeoWaveEvidence,
            NeoWaveInterval = NeoWaveEvidenceInterval,
            RsiBollingerSignals = RsiBollingerSignals,
            EnableDmiConfirmation = DmiConfirmationEnabled
        };
        options.Validate();
        return options;
    }

    public void Validate(int selectedStrategyCount = 1)
    {
        if (!ExecutionInterval.IsValid)
            throw new ArgumentException("ExecutionInterval must be valid.");
        if (!AnalysisBaseInterval.IsValid)
            throw new ArgumentException("AnalysisBaseInterval must be valid.");
        if (!StructuralTriggerInterval.IsValid)
            throw new ArgumentException("StructuralTriggerInterval must be valid.");
        if (AnalysisIntervals is null || AnalysisIntervals.Count == 0)
            throw new ArgumentException("At least one analysis interval is required.");
        if (AnalysisIntervals.Any(interval => !interval.IsValid))
            throw new ArgumentException("Every analysis interval must be valid.");

        ToTimeframeOptions().Validate();
        StrategyTimeframes.Validate();
        LegacyPositionManagement.Validate();
        ImprovedPositionManagement.Validate();
        StructuralPositionManagement.Validate();
        SafetyOptions.Validate();
        PositionSizing.Validate();
        AnnotationOptions.Validate();
        MarketRegimeRouting.Validate();
        ValueLocationEvidence.Validate();
        CurrencyStrengthEvidence.Validate();
        NeoWaveEvidence.Validate();
        if (NeoWaveEvidence.Enabled && !AnnotationOptions.NeoWave.Enabled)
        {
            throw new ArgumentException(
                "NEoWave evidence requires AnnotationOptions.NeoWave.Enabled.",
                nameof(NeoWaveEvidence));
        }
        RsiBollingerSignals.Validate();
        TradingConditions.Validate();
        PortfolioRisk.Validate();
        CorrelationRisk.Validate();
        CurrencyStrength.Validate();
        AdaptiveRisk.Validate();
        SetupCalibration.Validate();
        if (SetupCalibration.Enabled)
        {
            if (SetupCalibrationArtifact is null)
                throw new ArgumentException("Enabled setup calibration requires a versioned artifact.");
            SetupCalibrationArtifact.Validate();
        }
        RegimeManagement.Validate();
        ManagementCalibration.Validate();
        if (ManagementCalibration.Enabled)
        {
            if (ManagementCalibrationArtifact is null)
                throw new ArgumentException("Enabled trade-management calibration requires a versioned artifact.");
            ManagementCalibrationArtifact.Validate();
        }
        MetaModel.Validate();
        if (MetaModel.Enabled)
        {
            if (MetaModelArtifact is null)
                throw new ArgumentException("Enabled meta-model policy requires a versioned artifact.");
            MetaModelArtifact.Validate();
        }
        Execution.Validate();
        Financing.Validate();

        BarInterval[] derivedAnalysisIntervals = StrategyTimeframes.RequiredIntervals
            .Concat(ResolveManagementIntervals("legacy"))
            .Concat(ResolveManagementIntervals("improved"))
            .Concat(ResolveManagementIntervals(TradingAgentTypeIds.StructuralConfluence))
            .Distinct()
            .ToArray();
        foreach (BarInterval interval in derivedAnalysisIntervals)
        {
            if (!interval.IsValid ||
                BarIntervalParser.CompareDuration(interval, AnalysisBaseInterval) < 0)
            {
                throw new ArgumentException(
                    $"Derived analysis interval {BarIntervalParser.Format(interval)} must be " +
                    $">= analysis base {BarIntervalParser.Format(AnalysisBaseInterval)}.");
            }

            if (interval != AnalysisBaseInterval &&
                interval.Unit is BarUnit.Second or BarUnit.Minute or BarUnit.Hour &&
                !BarIntervalParser.IsDivisible(interval, AnalysisBaseInterval))
            {
                throw new ArgumentException(
                    $"Derived analysis interval {BarIntervalParser.Format(interval)} is not aligned with " +
                    $"analysis base {BarIntervalParser.Format(AnalysisBaseInterval)}.");
            }
        }

        HistoricalCapabilityRegistry.ValidateExecutionInterval(SourceKind, ExecutionInterval);

        if (UseHistoricalBidAsk &&
            !HistoricalCapabilityRegistry.Get(SourceKind).SupportsHistoricalBidAsk)
        {
            throw new ArgumentException(
                $"Historical bid/ask is not available for {HistoricalCapabilityRegistry.Get(SourceKind).SourceName}.");
        }

        if (SourceKind == HistoricalDataSourceKind.ImportedSecondCandles)
        {
            if (string.IsNullOrWhiteSpace(ImportedCandlePath) || !File.Exists(ImportedCandlePath))
            {
                throw new ArgumentException(
                    "ImportedCandlePath must point to an existing file for recorded/imported second sources.");
            }
        }

        if (SourcePageSize < 1)
            throw new ArgumentOutOfRangeException(nameof(SourcePageSize), "SourcePageSize must be >= 1.");
        if (PrefetchCapacity <= SourcePageSize)
            throw new ArgumentOutOfRangeException(nameof(PrefetchCapacity), "PrefetchCapacity must be greater than SourcePageSize.");
        if (PrefetchLowWatermark < 1)
            throw new ArgumentOutOfRangeException(nameof(PrefetchLowWatermark), "PrefetchLowWatermark must be >= 1.");
        if (PrefetchLowWatermark >= PrefetchCapacity)
            throw new ArgumentOutOfRangeException(nameof(PrefetchLowWatermark), "PrefetchLowWatermark must be < PrefetchCapacity.");
        if (StrategyChannelCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(StrategyChannelCapacity));
        if (MaximumParallelStrategies < 1)
            throw new ArgumentOutOfRangeException(nameof(MaximumParallelStrategies));
        if (selectedStrategyCount < 1)
            throw new ArgumentOutOfRangeException(nameof(selectedStrategyCount));
        if (selectedStrategyCount > MaximumParallelStrategies)
            throw new ArgumentException(
                $"Selected strategy count ({selectedStrategyCount}) exceeds MaximumParallelStrategies ({MaximumParallelStrategies}).");
        if (ReplayChunkSize < 1)
            throw new ArgumentOutOfRangeException(nameof(ReplayChunkSize));
        if (ExecutionDetailPreEntryFrames < 0 || ExecutionDetailPostExitFrames < 0)
            throw new ArgumentOutOfRangeException(nameof(ExecutionDetailPreEntryFrames));
        if (ProgressPublishIntervalMilliseconds < 50)
            throw new ArgumentOutOfRangeException(nameof(ProgressPublishIntervalMilliseconds), "Progress interval must be >= 50ms.");
        if (WarmupDays < 0)
            throw new ArgumentOutOfRangeException(nameof(WarmupDays));
        if (WarmupBaseCandleCount is < 0)
            throw new ArgumentOutOfRangeException(nameof(WarmupBaseCandleCount));
        if (CandleCapacity < 1 || LedgerCapacity < 1 || OrderEventCapacity < 1)
            throw new ArgumentException("Capacities must be positive.");
        if (!Enum.IsDefined(StrategyExecutionMode) ||
            !Enum.IsDefined(AnalysisSharingMode) ||
            !Enum.IsDefined(StrategyFailurePolicy) ||
            !Enum.IsDefined(AmbiguousIntrabarPolicy) ||
            !Enum.IsDefined(AccountMode))
        {
            throw new ArgumentException("One or more enumeration values are invalid.");
        }
    }

    public PositionManagementOptions GetPositionManagement(string strategyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        if (IsStructuralConfluence(strategyId))
            return StructuralPositionManagement;

        return strategyId.Contains("legacy", StringComparison.OrdinalIgnoreCase)
            ? LegacyPositionManagement
            : ImprovedPositionManagement;
    }

    /// <summary>
    /// Per-playbook position-management overrides for the structural-confluence agent - see
    /// PlaybookAwareTradeManager. IndicatorConfluencePlaybook's trades have no zone/pool to anchor
    /// risk to (plain ATR-multiple stop/target), so StructuralPositionManagement's zone-anchored
    /// 0.3R breakeven/trail activation (appropriate for the other three playbooks) cuts its
    /// winners far short of their planned target; this routes its trades to
    /// PositionManagementOptions.IndicatorConfluenceDefaults instead. Null for every other
    /// strategy id (Legacy/Improved Progressive never set AgentDecision.PlaybookId, so the
    /// wrapper would be a permanent no-op for them anyway, but skip constructing it at all).
    /// </summary>
    public IReadOnlyDictionary<string, PositionManagementOptions>? GetPlaybookManagementOverrides(string strategyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        return IsStructuralConfluence(strategyId)
            ? new Dictionary<string, PositionManagementOptions>(StringComparer.Ordinal)
            {
                [IndicatorConfluencePlaybook.StableId] = PositionManagementOptions.IndicatorConfluenceDefaults
            }
            : null;
    }

    /// <summary>
    /// True for the bare strategy type ("structural-confluence") and for a §7 multi-instrument
    /// portfolio assignment id ("structural-confluence:FX:EUR/USD", the default
    /// StrategyInstrumentAssignment.Id shape) alike - matching only the bare type, as this method
    /// did before, silently misclassified every structural-confluence assignment in a portfolio
    /// run as Improved instead (StrategyInstrumentAssignment.Id defaults to
    /// "{StrategyType}:{Instrument.Value}", which never equals the bare type exactly). Still
    /// heuristic like the legacy/improved Contains check below it - an explicit custom
    /// StrategyInstrumentAssignment.Id that doesn't start with the type id won't match.
    /// </summary>
    private static bool IsStructuralConfluence(string strategyId) =>
        string.Equals(strategyId.Trim(), TradingAgentTypeIds.StructuralConfluence, StringComparison.OrdinalIgnoreCase) ||
        strategyId.Trim().StartsWith(
            TradingAgentTypeIds.StructuralConfluence + ":", StringComparison.OrdinalIgnoreCase);

    public BarInterval ResolveManagementInterval(string strategyId)
    {
        PositionManagementOptions options = GetPositionManagement(strategyId);
        return options.MainStructureInterval ?? options.ManagementInterval ??
            StrategyTimeframes.ConfirmationInterval;
    }

    public IReadOnlyList<BarInterval> ResolveManagementIntervals(string strategyId)
    {
        PositionManagementOptions options = GetPositionManagement(strategyId);
        BarInterval fast = options.FastStructureInterval ?? StrategyTimeframes.EntryInterval;
        BarInterval main = options.MainStructureInterval ?? options.ManagementInterval ??
            StrategyTimeframes.ConfirmationInterval;
        BarInterval thesis = options.ThesisInterval ?? StrategyTimeframes.TrendInterval;
        return [fast, main, thesis];
    }

    public OcoFillPolicy ToOcoFillPolicy() => AmbiguousIntrabarPolicy switch
    {
        AmbiguousIntrabarPolicy.OptimisticTargetFirst => OcoFillPolicy.TakeProfitFirst,
        AmbiguousIntrabarPolicy.NearestToOpenFirst => OcoFillPolicy.NearestToOpenFirst,
        _ => OcoFillPolicy.StopLossFirst
    };

    public DateTimeOffset ResolveWarmupFrom(DateTimeOffset evaluationFrom)
    {
        DateTimeOffset byDays = WarmupDays <= 0
            ? evaluationFrom
            : evaluationFrom.AddDays(-WarmupDays);
        if (WarmupBaseCandleCount is null or <= 0)
            return byDays;

        // Rough calendar estimate for execution-candle count (FX weekdays ≈ 5/7).
        double stepMinutes = ExecutionInterval.Unit switch
        {
            BarUnit.Second => ExecutionInterval.Value / 60.0,
            BarUnit.Minute => ExecutionInterval.Value,
            BarUnit.Hour => ExecutionInterval.Value * 60.0,
            _ => 1.0
        };
        double calendarMinutes = WarmupBaseCandleCount.Value * stepMinutes * 7.0 / 5.0;
        DateTimeOffset byCount = evaluationFrom.AddMinutes(-calendarMinutes);
        return byCount < byDays ? byCount : byDays;
    }
}

/// <summary>
/// Assigns one strategy type to one instrument for the §7 multi-instrument portfolio
/// clock. When a request supplies these, they replace <see cref="BacktestRequest.Strategies"/>
/// entirely rather than combining with it - each strategy's instrument must be explicit,
/// not inferred, to avoid silently mixing single-instrument and multi-instrument semantics.
/// </summary>
public sealed record StrategyInstrumentAssignment
{
    /// <summary>Canonical agent type - the same catalog <see cref="BacktestRequest.Strategies"/> uses.</summary>
    public required string StrategyType { get; init; }
    public required InstrumentKey Instrument { get; init; }
    /// <summary>Optional override; defaults to "{StrategyType}:{Instrument.Value}".</summary>
    public string? Id { get; init; }

    /// <summary>
    /// Multi-agent architecture Phase 5: per-assignment Agent-interpretation override. Null
    /// (the default) reuses <see cref="BacktestRequest.ResolveProgressiveStrategyOptions"/>
    /// exactly as before this phase - only assignments that explicitly diverge pay for their own
    /// options resolution.
    /// </summary>
    public ProgressiveStrategyOptions? AgentOptionsOverride { get; init; }

    /// <summary>
    /// Complete agent definition override. This is the preferred schema; the progressive
    /// options property remains as a migration alias and cannot be supplied at the same time.
    /// </summary>
    public TradingAgentDefinition? AgentDefinitionOverride { get; init; }

    /// <summary>
    /// Multi-agent architecture Phase 5: per-assignment computed-analysis override, resolved
    /// through <c>Simulator.Engine.AnalysisProfileRegistry</c> (Phase 3). Null (the default)
    /// reuses <c>StreamingComparativeEngineOptions.AnnotationOptions</c> exactly as before
    /// this phase, so every assignment on one run still shares a single profile/engine unless a
    /// caller explicitly opts an assignment out.
    /// </summary>
    public ChartAnnotationOptions? AnalysisOptionsOverride { get; init; }

    /// <summary>
    /// Multi-agent architecture Phase 5: declared execution disposition. Validated here (at most
    /// one <see cref="AgentExecutionMode.Executable"/> assignment per instrument - see
    /// <see cref="BacktestRequest.Validate"/>) and carried through to
    /// <c>StrategyWorkerHost.Key</c>/<c>SimulatorAgentRuntime.Mode</c>, but - scope boundary,
    /// stated explicitly per the approved plan - NOT YET wired into
    /// <c>SharedPortfolioRuntime</c>/<c>ExecutionCoordinator</c>'s actual admission decisions.
    /// That wiring is Phase 7 (deferred); do not mistake this validation for a completed
    /// capital-safety guarantee.
    /// </summary>
    public AgentExecutionMode Mode { get; init; } = AgentExecutionMode.Shadow;

    /// <summary>Multi-agent architecture Phase 5: optional policy-bundle identity, carried
    /// through to this assignment's <c>AgentInstanceKey</c> for parity with the live host's
    /// registration identity. Null (the default) keeps today's placeholder
    /// (<c>Guid.Empty</c>/<c>0</c>) identity - see <c>StrategyWorkerHost</c>'s constructor.</summary>
    public Guid? PolicyBundleId { get; init; }
    public int? PolicyRevision { get; init; }

    /// <summary>
    /// Indicator-calibration blueprint §15.1: explicit pinning only, never a "latest approved"
    /// lookup. Null (the default, existing behaviour) means this assignment runs with whatever
    /// <see cref="AgentDefinitionOverride"/>/strategy defaults it would have used anyway - setting
    /// this alone changes nothing until something actually resolves the referenced artifact and
    /// applies it (see <c>Simulator.Experiments.IndicatorCalibration.IndicatorCalibrationOverlayResolver</c>).
    /// <c>BacktestApplicationService</c> itself never reads this field - consistent with how
    /// Setup/Management/MetaModel calibration artifacts are already resolved by the caller before
    /// a request reaches the engine, not by the engine itself.
    /// </summary>
    public Guid? IndicatorCalibrationArtifactId { get; init; }

    /// <summary>
    /// The <c>LiquidityBreakRetestOptions</c> counterpart to <see cref="IndicatorCalibrationArtifactId"/> -
    /// same explicit-pinning-only semantics, resolved by
    /// <c>Simulator.Experiments.IndicatorCalibration.Strategies.LiquidityBreakRetestRequestOverlayResolver</c>.
    /// An assignment may pin both this and <see cref="IndicatorCalibrationArtifactId"/> at once, since
    /// they overlay different sub-objects of <c>StructuralConfluenceStrategyOptions</c>.
    /// </summary>
    public Guid? LiquidityBreakRetestCalibrationArtifactId { get; init; }
}

/// <summary>User/API request that starts a simulation job.</summary>
public sealed record BacktestRequest
{
    /// <summary>
    /// Default/back-compat single traded instrument, and the candle-request template
    /// (interval/from/to/cache options) every per-instrument request is derived from when
    /// <see cref="StrategyAssignments"/> is set. Still required even for multi-instrument
    /// requests for that reason.
    /// </summary>
    public required InstrumentKey Instrument { get; init; }
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }
    public BrokerEnvironment Environment { get; init; } = BrokerEnvironment.Demo;
    public IReadOnlyList<string> Strategies { get; init; } = ["legacy", "improved"];
    /// <summary>
    /// Optional per-strategy instrument assignment for the §7 multi-instrument portfolio
    /// clock. Null/empty (the default) preserves today's behaviour exactly: every name in
    /// <see cref="Strategies"/> trades <see cref="Instrument"/>. When set, this replaces
    /// <see cref="Strategies"/> entirely - see <see cref="Validate"/>.
    /// </summary>
    public IReadOnlyList<StrategyInstrumentAssignment>? StrategyAssignments { get; init; }
    public decimal StartingBalance { get; init; } = 100_000m;
    public string? BaseCurrency { get; init; }
    public decimal Quantity { get; init; } = 1_000m;
    public decimal Leverage { get; init; } = 20m;
    public decimal CommissionRate { get; init; } = 0.00002m;
    public decimal SpreadBasisPoints { get; init; } = 1m;
    public decimal SlippageBasisPoints { get; init; } = 0.5m;
    public decimal MinimumRewardRisk { get; init; } = 1.5m;
    public PriceActionConfirmationMode PriceActionConfirmation { get; init; } = PriceActionConfirmationMode.Soft;
    public decimal MinimumPriceActionConfidence { get; init; } = 55m;
    public bool RejectStrongOpposingPriceAction { get; init; } = true;
    public string OutputDirectory { get; init; } = Path.Combine("Dashboard", "public", "data", "simulations");
    public string CacheDirectory { get; init; } = Path.Combine(".cache", "oanda");
    public string JobsDirectory { get; init; } = Path.Combine(".cache", "simulation-jobs");
    public BacktestRuntimeOptions Runtime { get; init; } = new();
    /// <summary>
    /// Operational output switch for internal training runs. It is intentionally excluded
    /// from persisted requests and configuration hashes because it does not change trading
    /// decisions or results; normal user simulations still capture chart replay.
    /// </summary>
    [JsonIgnore]
    public bool CaptureMarketReplay { get; init; } = true;
    /// <summary>Optional in-memory candle override for tests and CI without OANDA.</summary>
    [JsonIgnore]
    public IReadOnlyList<Candle>? InlineCandles { get; init; }
    [JsonIgnore]
    public string? AccountId { get; init; }
    [JsonIgnore]
    public string? AccessToken { get; init; }

    /// <summary>
    /// Every distinct instrument this request actually trades - <see cref="StrategyAssignments"/>'s
    /// instruments when set (§7 multi-instrument clock), otherwise just <see cref="Instrument"/>.
    /// </summary>
    public IReadOnlyList<InstrumentKey> TradedInstruments() =>
        StrategyAssignments is { Count: > 0 }
            ? StrategyAssignments.Select(assignment => assignment.Instrument).Distinct().ToArray()
            : [Instrument];

    /// <summary>
    /// Resolves the exact environment-neutral Agent options used by the simulator. Policy
    /// promotion calls this same method, so OANDA Demo cannot silently reconstruct a different
    /// strategy from a second live-only settings model.
    /// </summary>
    public ProgressiveStrategyOptions ResolveProgressiveStrategyOptions() =>
        Runtime.ResolveProgressiveStrategyOptions(
            Quantity,
            MinimumRewardRisk,
            PriceActionConfirmation,
            MinimumPriceActionConfidence,
            RejectStrongOpposingPriceAction);

    public TradingAgentDefinition ResolveAgentDefinition(
        string strategyType,
        TradingAgentDefinition? definitionOverride = null,
        ProgressiveStrategyOptions? progressiveOptionsOverride = null)
    {
        if (definitionOverride is not null && progressiveOptionsOverride is not null)
            throw new ArgumentException("AgentDefinitionOverride conflicts with the legacy AgentOptionsOverride.");

        TradingAgentKind kind = TradingAgentTypeIds.Parse(strategyType);
        if (definitionOverride is not null)
        {
            definitionOverride.Validate();
            if (definitionOverride.Kind != kind)
            {
                throw new ArgumentException(
                    $"Strategy type '{strategyType}' conflicts with agent definition kind '{definitionOverride.Kind}'.");
            }
            if (kind != TradingAgentKind.StructuralConfluence)
                return definitionOverride;

            TradingAgentDefinition resolvedOverride = definitionOverride with
            {
                StructuralConfluence = definitionOverride.StructuralConfluence! with
                {
                    MarketRegime = Runtime.MarketRegimeRouting
                }
            };
            ValidateStructuralAnnotationRequirements(resolvedOverride.StructuralConfluence!);
            return resolvedOverride;
        }

        switch (kind)
        {
            case TradingAgentKind.LegacyProgressive or TradingAgentKind.ImprovedProgressive:
                return new TradingAgentDefinition
                {
                    Kind = kind,
                    Progressive = progressiveOptionsOverride ?? ResolveProgressiveStrategyOptions()
                };
            case TradingAgentKind.StructuralConfluence:
                var structural = new StructuralConfluenceStrategyOptions
                {
                    Quantity = Quantity,
                    MinimumRewardRisk = MinimumRewardRisk,
                    TriggerInterval = Runtime.StructuralTriggerInterval,
                    SetupInterval = Runtime.StructuralSetupInterval,
                    ContextInterval = Runtime.StructuralContextInterval,
                    MarketRegime = Runtime.MarketRegimeRouting,
                    Trigger = new StructuralTriggerOptions
                    {
                        MinimumPriceActionConfidence = MinimumPriceActionConfidence
                    },
                    // All four playbooks on for CLI/sim structural runs. IC uses tightened
                    // IndicatorConfluenceOptions defaults (ADX/RSI/cooldown/setup ATR).
                    LiquiditySweepReversal = new LiquiditySweepReversalOptions { Enabled = true },
                    SupplyDemandPullback = new SupplyDemandPullbackOptions { Enabled = true },
                    LiquidityBreakRetest = new LiquidityBreakRetestOptions { Enabled = true },
                    IndicatorConfluence = new IndicatorConfluenceOptions { Enabled = true }
                };
                ValidateStructuralAnnotationRequirements(structural);
                return new TradingAgentDefinition { Kind = kind, StructuralConfluence = structural };
            case TradingAgentKind.DivergenceReversal:
                var divergenceReversal = new DivergenceReversalStrategyOptions
                {
                    MonitoredIntervals = [BarInterval.Minutes(30), BarInterval.Minutes(15)],
                    ConfirmationIntervals = [BarInterval.Minutes(5), BarInterval.Minutes(1)],
                    Quantity = Quantity
                };
                return new TradingAgentDefinition { Kind = kind, DivergenceReversal = divergenceReversal };
            default:
                throw new ArgumentOutOfRangeException(nameof(strategyType));
        }
    }

    /// <summary>
    /// Mirrors <c>SimulationStrategyProfile.ValidateForExecution()</c>'s identical check for the
    /// Dashboard profile path. The plain <see cref="Strategies"/>/<see cref="StrategyAssignments"/>
    /// path used by the CLI, walk-forward tooling, and direct API callers had no equivalent guard:
    /// a bare request enables three playbooks that read liquidity pools/supply-demand zones, but
    /// <see cref="ChartAnnotationOptions.Liquidity"/>/<see cref="ChartAnnotationOptions.SupplyDemand"/>
    /// both default to disabled, so those three playbooks silently evaluated "unavailable" on every
    /// bar - zero trades, no error - for the lifetime of a run. Confirmed empirically: a 60-day real
    /// EUR/USD walk-forward window produced 0 trades from structural-confluence under bare defaults
    /// while legacy/improved-progressive produced 83/16 trades on the identical candles.
    /// </summary>
    private void ValidateStructuralAnnotationRequirements(StructuralConfluenceStrategyOptions strategy)
    {
        bool requiresLiquidity = strategy.LiquiditySweepReversal.Enabled || strategy.LiquidityBreakRetest.Enabled;
        if (requiresLiquidity && !Runtime.AnnotationOptions.Liquidity.Enabled)
        {
            throw new ArgumentException(
                "Runtime.AnnotationOptions.Liquidity.Enabled must be true when the liquidity-sweep " +
                "or accepted-break/retest playbook is enabled - otherwise those playbooks can never " +
                "produce a candidate.");
        }

        bool requiresSupplyDemand = strategy.SupplyDemandPullback.Enabled ||
            (strategy.LiquiditySweepReversal.Enabled &&
             strategy.LiquiditySweepReversal.SupplyDemandConfluence != StructuralConfluenceRequirement.Disabled);
        if (requiresSupplyDemand && !Runtime.AnnotationOptions.SupplyDemand.Enabled)
        {
            throw new ArgumentException(
                "Runtime.AnnotationOptions.SupplyDemand.Enabled must be true when the supply/demand-" +
                "pullback playbook or liquidity-sweep supply/demand confluence is enabled - otherwise " +
                "the supply/demand-pullback playbook can never produce a candidate.");
        }
    }

    public void Validate()
    {
        if (Instrument.IsEmpty)
            throw new ArgumentException("Instrument is required.");
        if (From >= To)
            throw new ArgumentException("From must be earlier than To.");
        if (Strategies is null || Strategies.Count == 0)
            throw new ArgumentException("At least one strategy is required.");
        if (StartingBalance <= 0 || Quantity <= 0 || Leverage <= 0 || MinimumRewardRisk <= 0)
            throw new ArgumentException("Balance, quantity, leverage, and minimum R:R must be positive.");
        if (!Enum.IsDefined(PriceActionConfirmation) || MinimumPriceActionConfidence is < 0m or > 100m)
            throw new ArgumentException("Price-action confirmation mode and confidence must be valid.");
        if (InlineCandles is null && Runtime.SourceKind == HistoricalDataSourceKind.InlineTestData)
            throw new ArgumentException("InlineTestData requires InlineCandles and is not an external source.");

        if (StrategyAssignments is { Count: > 0 } assignments)
        {
            foreach (StrategyInstrumentAssignment assignment in assignments)
            {
                if (assignment.Instrument.IsEmpty)
                    throw new ArgumentException("Every strategy assignment requires an instrument.");
                ResolveAgentDefinition(
                    assignment.StrategyType,
                    assignment.AgentDefinitionOverride,
                    assignment.AgentOptionsOverride);
            }
            string[] ids = assignments
                .Select(assignment => assignment.Id ?? $"{assignment.StrategyType}:{assignment.Instrument.Value}")
                .ToArray();
            if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
                throw new ArgumentException("StrategyAssignments produced duplicate strategy ids.");

            // Mirrors the live host's existing "one owner per instrument" rule
            // (LiveEngineHostedService.RegisterAgentsAsync) - closes a real, previously-unflagged
            // gap: before this phase the simulator had no shadow/executable concept per
            // assignment at all, so two Executable assignments on one instrument in
            // SharedPortfolioAccount mode would compete for capital with no "one owner" rule.
            IEnumerable<InstrumentKey> instrumentsWithMultipleExecutables = assignments
                .Where(assignment => assignment.Mode == AgentExecutionMode.Executable)
                .GroupBy(assignment => assignment.Instrument)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key);
            if (instrumentsWithMultipleExecutables.FirstOrDefault() is { IsEmpty: false } duplicateInstrument)
            {
                throw new ArgumentException(
                    $"{duplicateInstrument} has more than one Executable assignment. " +
                    "At most one assignment per instrument may be Executable.");
            }
        }
        else
        {
            foreach (string strategy in Strategies)
                ResolveAgentDefinition(strategy);
        }

        // Inline fixtures use the inline capability set (includes 1s for tests).
        BacktestRuntimeOptions runtime = InlineCandles is not null
            ? Runtime with { SourceKind = HistoricalDataSourceKind.InlineTestData }
            : Runtime;
        int selectedStrategyCount = StrategyAssignments is { Count: > 0 } assigned
            ? assigned.Count
            : Strategies.Count;
        runtime.Validate(selectedStrategyCount);
        if (runtime.Financing.Enabled)
        {
            InstrumentKey[] missingRates = TradedInstruments()
                .Where(instrument => !runtime.Financing.InstrumentRates.ContainsKey(instrument.Value))
                .ToArray();
            if (missingRates.Length > 0)
            {
                throw new ArgumentException(
                    "Enabled financing requires an explicit long/short rate for " +
                    string.Join(", ", missingRates.Select(instrument => instrument.Value)) + ".");
            }
        }

        // No production IEconomicEventProvider is wired into this request pipeline yet
        // (StrategySimulationSession always defaults it to null). Enabling the flag here
        // would silently report event protection as active while never blocking an
        // entry - see TradingHub_Quantitative_Enhancements audit §10. Reject rather than
        // continue unprotected.
        if (runtime.TradingConditions.EconomicEventFilterEnabled)
        {
            throw new ArgumentException(
                "EconomicEventFilterEnabled requires a configured IEconomicEventProvider. No " +
                "production provider is wired into this request pipeline, so this configuration " +
                "would run unprotected while claiming to be protected. Leave it disabled until a " +
                "provider is implemented and wired.");
        }
    }

    public DateTimeOffset ResolveWarmupFrom() => Runtime.ResolveWarmupFrom(From);
}
