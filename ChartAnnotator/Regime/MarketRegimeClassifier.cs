using Brokers.Models;
using ChartAnnotator.Models;

namespace ChartAnnotator.Regime;

/// <summary>
/// Flattened, already-resolved inputs to the pure rule hierarchy in
/// <see cref="MarketRegimeClassifier.Classify"/>. Public (not internal) so the rule
/// hierarchy can be unit-tested directly without hysteresis/calibration noise.
/// </summary>
public sealed record MarketRegimeRuleInputs
{
    public required AtrVolatilityRegime AtrRegime { get; init; }
    public required BollingerWidthRegime BollingerWidthRegime { get; init; }
    public required VolatilityDirection BollingerWidthDirection { get; init; }
    public required decimal? Adx { get; init; }
    public required PriceActionDirection AdxDirectionalBias { get; init; }
    public required decimal? EfficiencyRatio { get; init; }
    public required MarketEfficiencyState EfficiencyState { get; init; }
    public required MomentumDirection EfficiencyDirection { get; init; }
    public required bool DonchianClosedAboveUpper { get; init; }
    public required bool DonchianClosedBelowLower { get; init; }
    public required MarketStructureDirection StructureDirection { get; init; }
    public required bool StructureDirectionChanged { get; init; }
    public required MarketStructureBreak StructureBreak { get; init; }
    public required int ConsecutiveHigherHighs { get; init; }
    public required int ConsecutiveHigherLows { get; init; }
    public required int ConsecutiveLowerHighs { get; init; }
    public required int ConsecutiveLowerLows { get; init; }
    public required bool RecentAlignedBullishDisplacement { get; init; }
    public required bool RecentAlignedBearishDisplacement { get; init; }
    public required MarketRegime PreviousRegime { get; init; }
    public required MarketRegimeCalibrationSnapshot AdxCalibration { get; init; }
    public required decimal? SpreadAtr { get; init; }
    public required bool DataQualityOk { get; init; }
}

public sealed record MarketRegimeEvaluation(
    MarketRegime Regime,
    decimal Confidence,
    IReadOnlyList<RegimeContribution> Contributions,
    string ReasonCode);

/// <summary>
/// Stateful, per-chart deterministic regime classifier. The rule hierarchy itself is
/// a pure static method (independently testable); this class layers ADX calibration
/// and hysteresis (confirmation/persistence/switch-margin) on top, mirroring how
/// <c>AtrAnalysisState</c> holds history while <c>ConfidenceScorer</c> stays stateless.
/// </summary>
public sealed class MarketRegimeClassifier
{
    private readonly MarketRegimeOptions _options;
    private readonly MarketRegimeCalibration _adxCalibration;
    private int _barsSinceBullishDisplacement = -1;
    private int _barsSinceBearishDisplacement = -1;
    private MarketRegime _currentRegime = MarketRegime.Unknown;
    private decimal _currentConfidence;
    private int _ageCandles;
    private DateTimeOffset _confirmedAt = DateTimeOffset.MinValue;
    private MarketRegime? _pendingRegime;
    private int _pendingBars;

    public MarketRegimeClassifier(MarketRegimeOptions? options = null)
    {
        _options = options ?? new MarketRegimeOptions();
        _options.Validate();
        _adxCalibration = new MarketRegimeCalibration(
            _options.AdxCalibrationHistoryPeriod,
            _options.AdxCalibrationMinimumSamples,
            _options.TrendAdxMinimumPercentile,
            _options.RangeAdxMaximumPercentile);
    }

    public void FreezeCalibration(DateTimeOffset frozenAt) => _adxCalibration.FreezeCalibration(frozenAt);

    public MarketRegimeSnapshot Update(
        Candle candle,
        IndicatorSnapshot indicators,
        MarketStructureSnapshot structure,
        PriceActionSnapshot priceAction,
        decimal? spreadAtr = null,
        bool dataQualityOk = true)
    {
        ArgumentNullException.ThrowIfNull(candle);
        ArgumentNullException.ThrowIfNull(indicators);
        ArgumentNullException.ThrowIfNull(structure);
        ArgumentNullException.ThrowIfNull(priceAction);

        bool bullishDisplacementThisBar = priceAction.Events.Any(item =>
            item.Type is PriceActionEventType.BullishDisplacement or PriceActionEventType.BullishCompressionBreakout);
        _barsSinceBullishDisplacement = bullishDisplacementThisBar
            ? 0
            : _barsSinceBullishDisplacement < 0 ? -1 : _barsSinceBullishDisplacement + 1;

        bool bearishDisplacementThisBar = priceAction.Events.Any(item =>
            item.Type is PriceActionEventType.BearishDisplacement or PriceActionEventType.BearishCompressionBreakout);
        _barsSinceBearishDisplacement = bearishDisplacementThisBar
            ? 0
            : _barsSinceBearishDisplacement < 0 ? -1 : _barsSinceBearishDisplacement + 1;

        AdxAnalysisSnapshot adx = indicators.AdxAnalysis;
        MarketRegimeCalibrationSnapshot adxCalibration = adx.Adx is decimal adxValue
            ? _adxCalibration.Observe(adxValue)
            : MarketRegimeCalibrationSnapshot.Empty;

        var inputs = new MarketRegimeRuleInputs
        {
            AtrRegime = indicators.AtrAnalysis.Regime,
            BollingerWidthRegime = indicators.BollingerAnalysis.WidthRegime,
            BollingerWidthDirection = indicators.BollingerAnalysis.WidthDirection,
            Adx = adx.Adx,
            AdxDirectionalBias = adx.DirectionalBias,
            EfficiencyRatio = indicators.EfficiencyRatio,
            EfficiencyState = indicators.EfficiencyAnalysis.State,
            EfficiencyDirection = indicators.EfficiencyAnalysis.Direction,
            DonchianClosedAboveUpper = indicators.Donchian.ClosedAbovePreviousUpper,
            DonchianClosedBelowLower = indicators.Donchian.ClosedBelowPreviousLower,
            StructureDirection = structure.Direction,
            StructureDirectionChanged = structure.DirectionChanged,
            StructureBreak = structure.Break,
            ConsecutiveHigherHighs = structure.ConsecutiveHigherHighs,
            ConsecutiveHigherLows = structure.ConsecutiveHigherLows,
            ConsecutiveLowerHighs = structure.ConsecutiveLowerHighs,
            ConsecutiveLowerLows = structure.ConsecutiveLowerLows,
            RecentAlignedBullishDisplacement =
                _barsSinceBullishDisplacement >= 0 && _barsSinceBullishDisplacement <= _options.DisplacementLookbackBars,
            RecentAlignedBearishDisplacement =
                _barsSinceBearishDisplacement >= 0 && _barsSinceBearishDisplacement <= _options.DisplacementLookbackBars,
            PreviousRegime = _currentRegime,
            AdxCalibration = adxCalibration,
            SpreadAtr = spreadAtr,
            DataQualityOk = dataQualityOk
        };

        MarketRegimeEvaluation raw = Classify(inputs, _options);
        DateTimeOffset at = candle.CloseTime ?? candle.OpenTime;
        ApplyHysteresis(raw, at);

        return new MarketRegimeSnapshot
        {
            Regime = _currentRegime,
            Confidence = _currentConfidence,
            ConfirmedAt = _confirmedAt,
            AgeCandles = _ageCandles,
            Contributions = raw.Contributions,
            ReasonCode = raw.ReasonCode,
            IsTradeable = _currentRegime is not (MarketRegime.IlliquidUnsafe or MarketRegime.HighVolatilityDisorder)
        };
    }

    private void ApplyHysteresis(MarketRegimeEvaluation raw, DateTimeOffset at)
    {
        // Bars since the last confirmed flip, regardless of what a challenger bar
        // raw-classifies as in between - this is what MinimumPersistenceBars
        // measures a cool-down against.
        _ageCandles++;

        if (raw.Regime == MarketRegime.IlliquidUnsafe)
        {
            Flip(raw, at);
            return;
        }

        if (raw.Regime == _currentRegime)
        {
            _currentConfidence = raw.Confidence;
            _pendingRegime = null;
            _pendingBars = 0;
            return;
        }

        if (_pendingRegime == raw.Regime)
        {
            _pendingBars++;
            if (_pendingBars >= _options.MinimumConfirmationBars &&
                _ageCandles >= _options.MinimumPersistenceBars &&
                raw.Confidence >= NeutralConfidence + _options.SwitchConfidenceMargin)
            {
                // The challenger's own confidence must clear a fixed floor above the
                // neutral baseline every branch starts from (see Result(...) below).
                // Comparing against the OLD regime's frozen confidence instead would
                // let a high-confidence regime become permanently unbeatable once
                // _currentConfidence is never refreshed again (raw stops matching
                // _currentRegime), since confidence is capped at 100.
                Flip(raw, at);
            }

            return;
        }

        _pendingRegime = raw.Regime;
        _pendingBars = 1;
    }

    private void Flip(MarketRegimeEvaluation raw, DateTimeOffset at)
    {
        _currentRegime = raw.Regime;
        _currentConfidence = raw.Confidence;
        _confirmedAt = at;
        _ageCandles = 0;
        _pendingRegime = null;
        _pendingBars = 0;
    }

    /// <summary>
    /// Pure deterministic rule hierarchy: first matching branch wins. Contains no
    /// history or hysteresis, so it can be tested without a stateful classifier.
    /// </summary>
    public static MarketRegimeEvaluation Classify(MarketRegimeRuleInputs inputs, MarketRegimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(options);

        if (!inputs.DataQualityOk)
        {
            return Result(MarketRegime.IlliquidUnsafe, "DataQualityFailure",
            [
                new RegimeContribution("DataQuality", 50m, "Data quality check failed.")
            ]);
        }

        if (inputs.SpreadAtr is decimal spreadAtr && spreadAtr >= options.HardMaximumSpreadAtr)
        {
            return Result(MarketRegime.IlliquidUnsafe, "SpreadExceedsHardLimit",
            [
                new RegimeContribution(
                    "Spread",
                    50m,
                    $"Spread/ATR {spreadAtr:F2} exceeds the hard limit {options.HardMaximumSpreadAtr:F2}.")
            ]);
        }

        bool veryHighVolatility = inputs.AtrRegime == AtrVolatilityRegime.VeryHigh;
        bool choppy = inputs.EfficiencyState is MarketEfficiencyState.HighlyChoppy or MarketEfficiencyState.Choppy ||
            (inputs.EfficiencyRatio is decimal disorderEr && disorderEr <= options.DisorderMaximumEfficiencyRatio);
        bool structureUnstable = inputs.StructureDirection is MarketStructureDirection.Unknown or MarketStructureDirection.Sideways ||
            inputs.StructureDirectionChanged;
        if (veryHighVolatility && choppy && structureUnstable)
        {
            return Result(MarketRegime.HighVolatilityDisorder, "HighVolatilityDisorder",
            [
                new RegimeContribution("VeryHighVolatility", 20m, "ATR percentile is very high relative to recent history."),
                new RegimeContribution("ChoppyMovement", 15m, "Efficiency Ratio indicates noisy, non-directional movement."),
                new RegimeContribution("UnstableStructure", 10m, "Market structure direction is unknown, sideways or just changed.")
            ]);
        }

        bool priorCompressionOrRange = inputs.PreviousRegime is MarketRegime.Compression or MarketRegime.Range;
        bool bollingerExpanding = inputs.BollingerWidthRegime == BollingerWidthRegime.Expansion;
        bool efficiencyRising = inputs.EfficiencyDirection == MomentumDirection.Rising;
        if (priorCompressionOrRange && bollingerExpanding && efficiencyRising)
        {
            bool breaksUp = inputs.DonchianClosedAboveUpper || inputs.StructureBreak == MarketStructureBreak.Bullish;
            bool breaksDown = inputs.DonchianClosedBelowLower || inputs.StructureBreak == MarketStructureBreak.Bearish;

            if (breaksUp && inputs.RecentAlignedBullishDisplacement)
            {
                return Result(MarketRegime.BreakoutExpansionUp, "BreakoutExpansionUp",
                [
                    new RegimeContribution("PriorCompressionOrRange", 10m, "Prior regime was Compression or Range."),
                    new RegimeContribution("DonchianOrStructureBreakUp", 20m, "Price closed above the prior channel boundary or broke structure bullishly."),
                    new RegimeContribution("BollingerExpansion", 15m, "Bollinger bandwidth is expanding."),
                    new RegimeContribution("AlignedDisplacement", 15m, "A recent aligned bullish displacement or compression-breakout event confirms the move."),
                    new RegimeContribution("EfficiencyRising", 5m, "Efficiency Ratio is rising.")
                ]);
            }

            if (breaksDown && inputs.RecentAlignedBearishDisplacement)
            {
                return Result(MarketRegime.BreakoutExpansionDown, "BreakoutExpansionDown",
                [
                    new RegimeContribution("PriorCompressionOrRange", 10m, "Prior regime was Compression or Range."),
                    new RegimeContribution("DonchianOrStructureBreakDown", 20m, "Price closed below the prior channel boundary or broke structure bearishly."),
                    new RegimeContribution("BollingerExpansion", 15m, "Bollinger bandwidth is expanding."),
                    new RegimeContribution("AlignedDisplacement", 15m, "A recent aligned bearish displacement or compression-breakout event confirms the move."),
                    new RegimeContribution("EfficiencyRising", 5m, "Efficiency Ratio is rising.")
                ]);
            }
        }

        decimal? trendAdxThreshold = inputs.AdxCalibration.CalibratedTrendAdxThreshold ?? options.FallbackTrendAdxMinimum;
        bool erAboveTrendMinimum = inputs.EfficiencyRatio is decimal trendEr && trendEr >= options.TrendMinimumEfficiencyRatio;
        if (inputs.Adx is decimal adxValue && adxValue >= trendAdxThreshold && erAboveTrendMinimum)
        {
            bool upAligned = inputs.AdxDirectionalBias == PriceActionDirection.Bullish &&
                inputs.StructureDirection == MarketStructureDirection.Rising &&
                inputs.StructureBreak != MarketStructureBreak.Bearish;
            bool downAligned = inputs.AdxDirectionalBias == PriceActionDirection.Bearish &&
                inputs.StructureDirection == MarketStructureDirection.Falling &&
                inputs.StructureBreak != MarketStructureBreak.Bullish;

            if (upAligned)
            {
                return Result(MarketRegime.TrendingUp, "TrendingUp",
                [
                    new RegimeContribution("AdxAboveThreshold", 15m, $"ADX {adxValue:F1} is above the trend threshold {trendAdxThreshold:F1}."),
                    new RegimeContribution("DiAligned", 10m, "+DI/-DI bias is bullish."),
                    new RegimeContribution("StructureAligned", 10m, "Market structure direction is rising with no opposing break."),
                    new RegimeContribution("EfficiencyAboveMinimum", 5m, "Efficiency Ratio is above the trend minimum.")
                ]);
            }

            if (downAligned)
            {
                return Result(MarketRegime.TrendingDown, "TrendingDown",
                [
                    new RegimeContribution("AdxAboveThreshold", 15m, $"ADX {adxValue:F1} is above the trend threshold {trendAdxThreshold:F1}."),
                    new RegimeContribution("DiAligned", 10m, "+DI/-DI bias is bearish."),
                    new RegimeContribution("StructureAligned", 10m, "Market structure direction is falling with no opposing break."),
                    new RegimeContribution("EfficiencyAboveMinimum", 5m, "Efficiency Ratio is above the trend minimum.")
                ]);
            }
        }

        bool bollingerSqueeze = inputs.BollingerWidthRegime == BollingerWidthRegime.Squeeze;
        bool atrVeryLowOrLow = inputs.AtrRegime is AtrVolatilityRegime.VeryLow or AtrVolatilityRegime.Low;
        bool widthContracting = inputs.BollingerWidthDirection == VolatilityDirection.Contracting;
        if (bollingerSqueeze && atrVeryLowOrLow && widthContracting)
        {
            return Result(MarketRegime.Compression, "Compression",
            [
                new RegimeContribution("BollingerSqueeze", 15m, "Bollinger bandwidth is in a squeeze."),
                new RegimeContribution("AtrLow", 15m, "Normalized ATR is very low or low relative to recent history."),
                new RegimeContribution("WidthContracting", 10m, "Bollinger bandwidth is contracting.")
            ]);
        }

        decimal? rangeAdxThreshold = inputs.AdxCalibration.CalibratedRangeAdxThreshold ?? options.FallbackRangeAdxMaximum;
        bool adxLow = inputs.Adx is decimal rangeAdx && rangeAdx <= rangeAdxThreshold;
        bool erBelowRangeMaximum = inputs.EfficiencyRatio is decimal rangeEr && rangeEr <= options.RangeMaximumEfficiencyRatio;
        bool noPersistentDirection = inputs.StructureDirection == MarketStructureDirection.Sideways ||
            (inputs.ConsecutiveHigherHighs <= 1 && inputs.ConsecutiveHigherLows <= 1 &&
                inputs.ConsecutiveLowerHighs <= 1 && inputs.ConsecutiveLowerLows <= 1);
        if (adxLow && erBelowRangeMaximum && noPersistentDirection)
        {
            return Result(MarketRegime.Range, "Range",
            [
                new RegimeContribution("AdxLow", 15m, $"ADX is at or below the range threshold {rangeAdxThreshold:F1}."),
                new RegimeContribution("EfficiencyBelowMaximum", 10m, "Efficiency Ratio is at or below the range maximum."),
                new RegimeContribution("NoPersistentDirection", 10m, "Market structure shows no persistent directional run.")
            ]);
        }

        return Result(MarketRegime.Unknown, "InsufficientOrContradictoryEvidence", []);
    }

    /// <summary>Base confidence every rule-hierarchy branch starts from before contributions.</summary>
    private const decimal NeutralConfidence = 50m;

    private static MarketRegimeEvaluation Result(
        MarketRegime regime,
        string reasonCode,
        IReadOnlyList<RegimeContribution> contributions)
    {
        decimal confidence = Math.Clamp(NeutralConfidence + contributions.Sum(item => item.Score), 0m, 100m);
        return new MarketRegimeEvaluation(regime, confidence, contributions, reasonCode);
    }
}
