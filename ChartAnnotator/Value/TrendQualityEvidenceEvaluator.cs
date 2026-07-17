using ChartAnnotator.Models;

namespace ChartAnnotator.Value;

/// <summary>
/// Configuration for optional trend-quality evidence: efficiency ratio (choppiness),
/// ATR volatility regime, Bollinger width regime, and ADX strengthening/weakening.
/// Disabled by default - new capability, not yet validated against backtest data.
/// </summary>
public sealed record TrendQualityEvidenceOptions
{
    public bool Enabled { get; init; }

    /// <summary>Soft confidence nudge applied per matched signal - deliberately small; this is
    /// confirmation evidence, never a hard veto (a hard choppiness gate would materially cut
    /// trade frequency and deserves its own measured rollout once backtest data justifies it).</summary>
    public decimal ConfidenceAdjustmentPerSignal { get; init; } = 3m;

    public void Validate()
    {
        if (!Enabled) return;
        if (ConfidenceAdjustmentPerSignal is < 0m or > 25m)
            throw new ArgumentOutOfRangeException(nameof(TrendQualityEvidenceOptions));
    }
}

public sealed record TrendQualityEvidence
{
    public required IReadOnlyList<string> ReasonCodes { get; init; }
    public decimal ConfidenceAdjustment { get; init; }

    public static TrendQualityEvidence None { get; } = new() { ReasonCodes = [] };
}

/// <summary>
/// Pure, single-frame evaluator: independent, reason-coded evidence of trend quality from
/// indicator regimes already computed on one AnalysisSnapshot (efficiency ratio, ATR
/// volatility regime, Bollinger width regime, ADX strengthening). Soft evidence only -
/// every signal here nudges confidence by a small configurable amount, never gates or
/// vetoes an entry. Direction (isBuy) matters only for framing reason codes; none of these
/// indicators are directional the way price-action/value-location evidence is, so the same
/// signal applies identically to a Buy or a Sell.
/// </summary>
public static class TrendQualityEvidenceEvaluator
{
    public static TrendQualityEvidence Evaluate(AnalysisSnapshot snapshot, TrendQualityEvidenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
            return TrendQualityEvidence.None;

        IndicatorSnapshot indicators = snapshot.Indicators;
        var codes = new List<string>();
        decimal adjustment = 0m;

        // Efficiency ratio / choppiness: a choppy market makes trend-following entries
        // unreliable false-signal traps; a highly efficient one confirms the move is real.
        if (indicators.EfficiencyAnalysis.State is MarketEfficiencyState.Choppy or MarketEfficiencyState.HighlyChoppy)
        {
            codes.Add("MarketChoppy");
            adjustment -= options.ConfidenceAdjustmentPerSignal;
        }
        else if (indicators.EfficiencyAnalysis.State is MarketEfficiencyState.Efficient or MarketEfficiencyState.HighlyEfficient)
        {
            codes.Add("MarketEfficient");
            adjustment += options.ConfidenceAdjustmentPerSignal;
        }

        // ATR volatility regime: extremes in either direction are execution/whipsaw risk -
        // very high volatility can gap through stops, very low volatility starves a trend
        // of the range it needs to reach a structural target.
        if (indicators.AtrAnalysis.Regime == AtrVolatilityRegime.VeryHigh)
        {
            codes.Add("VolatilityRegimeExtreme");
            adjustment -= options.ConfidenceAdjustmentPerSignal;
        }
        else if (indicators.AtrAnalysis.Regime == AtrVolatilityRegime.VeryLow)
        {
            codes.Add("VolatilityRegimeDead");
            adjustment -= options.ConfidenceAdjustmentPerSignal;
        }

        // Bollinger width regime: an unresolved squeeze is still coiled/undecided (whipsaw
        // risk on either side); confirmed expansion/release means the move is already
        // under way, not still forming.
        if (indicators.BollingerAnalysis.WidthRegime == BollingerWidthRegime.Squeeze &&
            !indicators.BollingerAnalysis.SqueezeReleased)
        {
            codes.Add("BollingerSqueezeUnresolved");
            adjustment -= options.ConfidenceAdjustmentPerSignal;
        }
        else if (indicators.BollingerAnalysis.IsExpansion || indicators.BollingerAnalysis.SqueezeReleased)
        {
            codes.Add("BollingerExpansionSupportsTrade");
            adjustment += options.ConfidenceAdjustmentPerSignal;
        }

        // ADX strengthening/weakening: measures whether directional conviction is building
        // or fading - direction itself is already required elsewhere (DetectSide's
        // DMI support/oppose check), this only measures the trend's momentum, not its side.
        if (indicators.AdxAnalysis.IsTrendStrengthening)
        {
            codes.Add("AdxTrendStrengthening");
            adjustment += options.ConfidenceAdjustmentPerSignal;
        }
        else if (indicators.AdxAnalysis.StrengthDirection == MomentumDirection.Falling)
        {
            codes.Add("AdxTrendWeakening");
            adjustment -= options.ConfidenceAdjustmentPerSignal;
        }

        return codes.Count == 0
            ? TrendQualityEvidence.None
            : new TrendQualityEvidence { ReasonCodes = codes, ConfidenceAdjustment = adjustment };
    }
}
