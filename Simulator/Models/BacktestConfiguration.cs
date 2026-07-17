using System.Text.Json.Serialization;
using Agent.Strategies;
using Brokers.Abstractions;
using Brokers.Models;
using ChartAnnotator.CurrencyStrength;
using ChartAnnotator.Engine;
using ChartAnnotator.MarketData;
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
        if (AnalysisIntervals is null || AnalysisIntervals.Count == 0)
            throw new ArgumentException("At least one analysis interval is required.");
        if (AnalysisIntervals.Any(interval => !interval.IsValid))
            throw new ArgumentException("Every analysis interval must be valid.");

        ToTimeframeOptions().Validate();
        StrategyTimeframes.Validate();
        LegacyPositionManagement.Validate();
        ImprovedPositionManagement.Validate();
        SafetyOptions.Validate();
        PositionSizing.Validate();
        AnnotationOptions.Validate();
        MarketRegimeRouting.Validate();
        ValueLocationEvidence.Validate();
        CurrencyStrengthEvidence.Validate();
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

    public PositionManagementOptions GetPositionManagement(string strategyId) =>
        strategyId.Contains("legacy", StringComparison.OrdinalIgnoreCase)
            ? LegacyPositionManagement
            : ImprovedPositionManagement;

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
    /// <summary>"legacy" or "improved" - the same catalog <see cref="BacktestRequest.Strategies"/> uses.</summary>
    public required string StrategyType { get; init; }
    public required InstrumentKey Instrument { get; init; }
    /// <summary>Optional override; defaults to "{StrategyType}:{Instrument.Value}".</summary>
    public string? Id { get; init; }
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
                if (assignment.StrategyType is not ("legacy" or "improved"))
                {
                    throw new ArgumentException(
                        $"Unknown strategy type '{assignment.StrategyType}' in StrategyAssignments. " +
                        "Use legacy or improved.");
                }
            }
            string[] ids = assignments
                .Select(assignment => assignment.Id ?? $"{assignment.StrategyType}:{assignment.Instrument.Value}")
                .ToArray();
            if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
                throw new ArgumentException("StrategyAssignments produced duplicate strategy ids.");
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
