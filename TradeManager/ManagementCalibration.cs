using System.Collections.Concurrent;
using ChartAnnotator.Models;
using ChartAnnotator.Regime;

namespace TradeManager;

public sealed record TradeManagementCalibrationOptions
{
    public bool Enabled { get; init; }
    public int MinimumSamples { get; init; } = 30;

    public void Validate()
    {
        if (MinimumSamples < 1)
            throw new ArgumentOutOfRangeException(nameof(MinimumSamples));
    }
}

public sealed record TradeManagementCohort
{
    public required string CohortId { get; init; }
    public required string StrategyId { get; init; }
    public required string InstrumentGroup { get; init; }
    public required string Regime { get; init; }
    public required string SetupType { get; init; }
    public required string Direction { get; init; }
    public required string Session { get; init; }
    public required string VolatilityBucket { get; init; }
    public required int ConfidenceBucket { get; init; }
    public required int Samples { get; init; }
    public required IReadOnlyDictionary<int, decimal> WinnerMfe80PercentileByBar { get; init; }
    public IReadOnlyDictionary<decimal, decimal> MedianBarsToMfeThreshold { get; init; } =
        new Dictionary<decimal, decimal>();
    public IReadOnlyDictionary<decimal, decimal> MedianMaximumGivebackAfterMfe { get; init; } =
        new Dictionary<decimal, decimal>();
    public required decimal MedianMaeBeforeHalfR { get; init; }
    public required decimal MedianDurationBars { get; init; }
    public required decimal MedianExitEfficiency { get; init; }
    public required decimal MedianStopDistance { get; init; }
    public decimal StopDistance10Percentile { get; init; }
    public decimal StopDistance90Percentile { get; init; }
    public decimal MedianPartialExitContributionR { get; init; }
    public decimal MedianRunnerContributionR { get; init; }
}

public sealed record TradeManagementCalibration
{
    public int SchemaVersion { get; init; } = 1;
    public required string CalibrationId { get; init; }
    public required IReadOnlyList<TradeManagementCohort> Cohorts { get; init; }
    public required string SourceDataHash { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    public void Validate()
    {
        if (SchemaVersion != 1 || string.IsNullOrWhiteSpace(CalibrationId) ||
            string.IsNullOrWhiteSpace(SourceDataHash) || Cohorts is null ||
            Cohorts.Any(item => string.IsNullOrWhiteSpace(item.CohortId) || item.Samples < 0))
            throw new ArgumentException("The trade-management calibration artifact is invalid or incompatible.");
    }
}

public sealed record TradeManagementCalibrationContext
{
    public required string StrategyId { get; init; }
    public required string InstrumentGroup { get; init; }
    public required string Regime { get; init; }
    public required string SetupType { get; init; }
    public required string Direction { get; init; }
    public required string Session { get; init; }
    public required string VolatilityBucket { get; init; }
    public required decimal Confidence { get; init; }
}

public sealed class TradeManagementCalibrationPolicy
{
    private readonly TradeManagementCalibration _artifact;
    private readonly int _minimumSamples;

    public TradeManagementCalibrationPolicy(TradeManagementCalibration artifact, int minimumSamples = 30)
    {
        _artifact = artifact ?? throw new ArgumentNullException(nameof(artifact));
        _artifact.Validate();
        if (minimumSamples < 1) throw new ArgumentOutOfRangeException(nameof(minimumSamples));
        _minimumSamples = minimumSamples;
    }

    public (PositionManagementOptions Options, string ReasonCode) Apply(
        PositionManagementOptions fallback,
        TradeManagementCalibrationContext context)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        ArgumentNullException.ThrowIfNull(context);
        int confidenceBucket = (int)(context.Confidence / 10m) * 10;
        TradeManagementCohort? cohort = _artifact.Cohorts
            .Where(item => Matches(item.StrategyId, context.StrategyId) &&
                Matches(item.InstrumentGroup, context.InstrumentGroup) &&
                Matches(item.Regime, context.Regime) &&
                Matches(item.SetupType, context.SetupType) &&
                Matches(item.Direction, context.Direction) &&
                Matches(item.Session, context.Session) &&
                Matches(item.VolatilityBucket, context.VolatilityBucket) &&
                item.ConfidenceBucket == confidenceBucket)
            .OrderByDescending(item => item.Samples)
            .FirstOrDefault();
        if (cohort is null || cohort.Samples < _minimumSamples)
            return (fallback, "StaticManagementFallback");

        // Clamp before the cast: an out-of-range median would wrap through the unchecked
        // int conversion instead of saturating.
        decimal medianDurationBars = Math.Clamp(cohort.MedianDurationBars, 1m, int.MaxValue);
        int stagnationBars = Math.Max(1, (int)decimal.Ceiling(medianDurationBars));
        PositionManagementOptions calibrated = fallback with
        {
            // Conservative use only, and that has to hold for both fields: calibration may review
            // sooner or keep the static value, never later. Taking the cohort median unbounded let
            // calibration grant MORE patience than the static policy - a loosening, not a
            // tightening, and the opposite of what the sibling field below enforces.
            StagnationBars = Math.Min(fallback.StagnationBars, stagnationBars),
            StagnationReductionFraction = Math.Min(fallback.StagnationReductionFraction, 0.20m)
        };
        calibrated.Validate();
        return (calibrated, $"ManagementCalibration:{_artifact.CalibrationId}:{cohort.CohortId}");
    }

    /// <summary>
    /// Cohort keys match case-insensitively, consistent with RiskManager's SetupCalibrationPolicy.
    /// Ordinal equality meant any case drift between the training pipeline and the runtime context
    /// degraded silently to StaticManagementFallback instead of surfacing.
    /// </summary>
    private static bool Matches(string cohortValue, string contextValue) =>
        string.Equals(cohortValue, contextValue, StringComparison.OrdinalIgnoreCase);
}

public interface IManagementProfileResolver
{
    string ResolveProfileId(MarketRegime regime);
}

/// <summary>
/// Applies a validated cohort artifact before regime-profile selection. It never
/// mutates an existing stop and falls back to the configured static policy whenever
/// the cohort is absent or too small.
/// </summary>
public sealed class CalibratedStructureBasedTradeManager :
    IStructureBasedTradeManager,
    IManagementProfileResolver
{
    private readonly PositionManagementOptions _fallback;
    private readonly TradeManagementCalibrationPolicy _calibration;
    private readonly RegimeManagementOptions _regimeOptions;
    // Mutated from Evaluate, so it cannot be a plain Dictionary: no concurrent caller exists
    // today, but a torn write here would corrupt management for every open position.
    private readonly ConcurrentDictionary<string, IStructureBasedTradeManager> _managers = new(StringComparer.Ordinal);

    public CalibratedStructureBasedTradeManager(
        PositionManagementOptions fallback,
        TradeManagementCalibration artifact,
        TradeManagementCalibrationOptions options,
        RegimeManagementOptions? regimeOptions = null)
    {
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _fallback.Validate();
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _calibration = new TradeManagementCalibrationPolicy(artifact, options.MinimumSamples);
        _regimeOptions = regimeOptions ?? new RegimeManagementOptions();
        _regimeOptions.Validate();
    }

    public string ResolveProfileId(MarketRegime regime) => _regimeOptions.Enabled
        ? new RegimeAwareStructureBasedTradeManager(_fallback, _regimeOptions).ResolveProfileId(regime)
        : "default";

    public TradeManagementRecommendation Evaluate(ManagedTradeState trade, AnalysisSnapshot analysis) =>
        Evaluate(trade, analysis, TradeManagementEvaluationScope.Combined);

    public TradeManagementRecommendation Evaluate(
        ManagedTradeState trade,
        AnalysisSnapshot analysis,
        TradeManagementEvaluationScope scope,
        EquityProtectionDirective? equityProtection = null)
    {
        (PositionManagementOptions calibrated, string reason) = _calibration.Apply(
            _fallback,
            new TradeManagementCalibrationContext
            {
                StrategyId = trade.StrategyId,
                InstrumentGroup = trade.InstrumentGroup,
                Regime = trade.EntryRegime.ToString(),
                SetupType = trade.SetupType,
                Direction = trade.Side.ToString(),
                Session = trade.EntrySession,
                VolatilityBucket = trade.EntryVolatilityBucket,
                Confidence = trade.EntryConfidence
        });
        IStructureBasedTradeManager manager = Manager(reason, calibrated);
        TradeManagementRecommendation recommendation = manager.Evaluate(
            trade,
            analysis,
            scope,
            equityProtection);
        return reason == "StaticManagementFallback"
            ? recommendation
            : recommendation with
            {
                ReasonCode = $"{reason}|{recommendation.ReasonCode}",
                Reason = $"[{reason}] {recommendation.Reason}"
            };
    }

    private IStructureBasedTradeManager Manager(string key, PositionManagementOptions options) =>
        _managers.GetOrAdd(key, _ => _regimeOptions.Enabled
            ? new RegimeAwareStructureBasedTradeManager(options, _regimeOptions)
            : new StructureBasedTradeManager(options));
}
