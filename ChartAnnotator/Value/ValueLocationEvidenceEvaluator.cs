using Brokers.Models;
using ChartAnnotator.Models;

namespace ChartAnnotator.Value;

/// <summary>
/// Configuration for optional, independent value-location evidence (audit §13.3).
/// Disabled by default so enabling it is an explicit, deliberate choice that does not
/// silently change any existing backtest result.
/// </summary>
public sealed record ValueLocationEvidenceOptions
{
    public bool Enabled { get; init; }
    public ValueAnchorType PreferredAnchor { get; init; } = ValueAnchorType.SessionOpen;
    public decimal NearValueAtrThreshold { get; init; } = 0.5m;
    public decimal StretchedFromValueAtrThreshold { get; init; } = 2.5m;

    /// <summary>Soft confidence nudge applied per matched signal - deliberately small; this is
    /// confirmation evidence, never a hard veto.</summary>
    public decimal ConfidenceAdjustmentPerSignal { get; init; } = 3m;

    public void Validate()
    {
        if (!Enabled) return;
        if (!Enum.IsDefined(PreferredAnchor) ||
            NearValueAtrThreshold <= 0m ||
            StretchedFromValueAtrThreshold <= NearValueAtrThreshold ||
            ConfidenceAdjustmentPerSignal is < 0m or > 25m)
        {
            throw new ArgumentOutOfRangeException(nameof(ValueLocationEvidenceOptions));
        }
    }
}

public sealed record ValueLocationEvidence
{
    public required IReadOnlyList<string> ReasonCodes { get; init; }
    public decimal ConfidenceAdjustment { get; init; }
    public ValueAnchorType? AnchorType { get; init; }
    public decimal? DistanceAtr { get; init; }

    public static ValueLocationEvidence None { get; } = new() { ReasonCodes = [] };
}

/// <summary>
/// Pure, single-frame evaluator: independent, reason-coded evidence of price's location
/// relative to an anchored value reference (audit §13.3). Soft evidence only - every
/// signal here nudges confidence by a small configurable amount, never gates or vetoes
/// an entry. Uses only data already on one AnalysisSnapshot (LatestCandle, MarketStructure,
/// ValueReferences), so it needs no additional rolling state beyond what
/// AnchoredValueReferenceState already maintains.
/// </summary>
public static class ValueLocationEvidenceEvaluator
{
    public static ValueLocationEvidence Evaluate(
        AnalysisSnapshot snapshot,
        bool isBuy,
        ValueLocationEvidenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled || snapshot.ValueReferences.Count == 0)
            return ValueLocationEvidence.None;

        AnchoredValueReference? anchor = snapshot.ValueReferences
            .FirstOrDefault(item => item.AnchorType == options.PreferredAnchor && item.DistanceAtr is not null)
            ?? snapshot.ValueReferences.FirstOrDefault(item => item.DistanceAtr is not null);
        if (anchor is null)
            return ValueLocationEvidence.None;

        decimal distanceAtr = anchor.DistanceAtr!.Value;
        var codes = new List<string>();
        decimal adjustment = 0m;

        // Pullback continuation evidence: price has returned close to the anchored
        // value, consistent with a trend pullback rather than a stretched entry.
        if (Math.Abs(distanceAtr) <= options.NearValueAtrThreshold)
        {
            codes.Add("ValueNearAnchor");
            adjustment += options.ConfidenceAdjustmentPerSignal;
        }

        // Breakout stretch caution: price is far from value in the trade's own direction,
        // i.e. this candidate would be chasing an already-extended move.
        bool stretchedInTradeDirection = isBuy ? distanceAtr >= options.StretchedFromValueAtrThreshold
            : distanceAtr <= -options.StretchedFromValueAtrThreshold;
        if (stretchedInTradeDirection)
        {
            codes.Add("BreakoutStretchedFromValue");
            adjustment -= options.ConfidenceAdjustmentPerSignal;
        }

        // Rejection evidence: this candle wicked through the value level intrabar but
        // closed back on the trade's side of it - a classic value-rejection pattern.
        Candle candle = snapshot.LatestCandle;
        bool rejectsInTrendDirection = isBuy
            ? candle.Prices.Low <= anchor.Value && candle.Prices.Close > anchor.Value
            : candle.Prices.High >= anchor.Value && candle.Prices.Close < anchor.Value;
        if (rejectsInTrendDirection)
        {
            codes.Add("PriceRejectsValueInTrendDirection");
            adjustment += options.ConfidenceAdjustmentPerSignal;
        }

        // Structural-deterioration caution: price sits on the wrong side of value while
        // structure just broke against the trade direction.
        bool wrongSideOfValue = isBuy ? candle.Prices.Close < anchor.Value : candle.Prices.Close > anchor.Value;
        bool structureBrokeAgainstTrade = isBuy
            ? snapshot.MarketStructure.Break == MarketStructureBreak.Bearish
            : snapshot.MarketStructure.Break == MarketStructureBreak.Bullish;
        if (wrongSideOfValue && structureBrokeAgainstTrade)
        {
            codes.Add("PriceLosesValueDuringStructuralDeterioration");
            adjustment -= options.ConfidenceAdjustmentPerSignal;
        }

        return new ValueLocationEvidence
        {
            ReasonCodes = codes,
            ConfidenceAdjustment = adjustment,
            AnchorType = anchor.AnchorType,
            DistanceAtr = distanceAtr
        };
    }
}
