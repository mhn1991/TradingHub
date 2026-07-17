using ChartAnnotator.Models;

namespace ChartAnnotator.NeoWave;

public enum NeoWaveEvidenceMode
{
    Disabled,
    RecordOnly,
    SoftConfidence,
    SoftRiskReduction,
    SoftConfidenceAndRisk
}

/// <summary>
/// Optional interpretation policy. Defaults to disabled; it may only reduce risk and never
/// creates an entry by itself.
/// </summary>
public sealed record NeoWaveEvidenceOptions
{
    public NeoWaveEvidenceMode Mode { get; init; } = NeoWaveEvidenceMode.Disabled;
    public decimal MinimumStructuralScore { get; init; } = 60m;
    public decimal MaximumTrustedConflictScore { get; init; } = 40m;
    public decimal AlignmentConfidenceAdjustment { get; init; } = 3m;
    public decimal OppositionConfidenceAdjustment { get; init; } = -4m;
    public decimal UncertainConfidenceAdjustment { get; init; }
    public decimal MinimumRiskMultiplier { get; init; } = 0.75m;
    public decimal MaximumConflictRiskReduction { get; init; } = 0.25m;

    public bool Enabled => Mode != NeoWaveEvidenceMode.Disabled;
    public bool AdjustsConfidence => Mode is NeoWaveEvidenceMode.SoftConfidence or NeoWaveEvidenceMode.SoftConfidenceAndRisk;
    public bool AdjustsRisk => Mode is NeoWaveEvidenceMode.SoftRiskReduction or NeoWaveEvidenceMode.SoftConfidenceAndRisk;

    public void Validate()
    {
        if (!Enum.IsDefined(Mode) ||
            MinimumStructuralScore is < 0m or > 100m ||
            MaximumTrustedConflictScore is < 0m or > 100m ||
            AlignmentConfidenceAdjustment is < -100m or > 100m ||
            OppositionConfidenceAdjustment is < -100m or > 100m ||
            UncertainConfidenceAdjustment is < -100m or > 100m ||
            MinimumRiskMultiplier is < 0m or > 1m ||
            MaximumConflictRiskReduction is < 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(NeoWaveEvidenceOptions));
        }
    }
}

public sealed record NeoWaveDecisionEvidence
{
    public required decimal ConfidenceAdjustment { get; init; }
    public required decimal RiskMultiplier { get; init; }
    public required bool DirectionAligned { get; init; }
    public required bool DirectionOpposed { get; init; }
    public required IReadOnlyList<string> ReasonCodes { get; init; }
    public NeoWavePatternType? PatternType { get; init; }
    public NeoWaveDirection Direction { get; init; }
    public string? HypothesisId { get; init; }
    public decimal StructuralScore { get; init; }
    public decimal Maturity { get; init; }
    public decimal ConflictScore { get; init; }
    public decimal? InvalidationPrice { get; init; }
    public decimal? InvalidationDistanceAtr { get; init; }
}

public static class NeoWaveEvidenceEvaluator
{
    public static NeoWaveDecisionEvidence Evaluate(
        AnalysisSnapshot snapshot,
        bool buy,
        NeoWaveEvidenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        NeoWaveSnapshot wave = snapshot.NeoWave;
        if (!options.Enabled)
            return Neutral("NeoWaveEvidenceDisabled", wave);
        if (!wave.Enabled || !wave.Quality.IsReady)
            return Neutral("NeoWaveEvidenceNotReady", wave, options.UncertainConfidenceAdjustment);
        NeoWaveHypothesis? preferred = wave.Hypotheses.FirstOrDefault(item => item.HypothesisId == wave.PreferredHypothesisId);
        if (preferred is null || preferred.StructuralScore < options.MinimumStructuralScore)
            return Neutral("NeoWaveNoTrustedHypothesis", wave, options.UncertainConfidenceAdjustment);

        NeoWaveDirection desired = buy ? NeoWaveDirection.Up : NeoWaveDirection.Down;
        bool directionalPattern = preferred.PatternType is
            NeoWavePatternType.TrendSequence or
            NeoWavePatternType.ImpulseCandidate;
        bool aligned = directionalPattern && preferred.Direction == desired;
        bool opposed = directionalPattern &&
            preferred.Direction != NeoWaveDirection.Neutral &&
            preferred.Direction != desired;
        bool conflicted = wave.ConflictScore > options.MaximumTrustedConflictScore;

        // A completed correction has no reliable forecast direction without parent-degree
        // context. Preserve it for diagnostics, but do not turn it into directional confidence
        // or a risk penalty. RecordOnly may still report the pattern without changing the trade.
        decimal confidence = options.AdjustsConfidence && directionalPattern
            ? conflicted
                ? options.UncertainConfidenceAdjustment
                : aligned
                    ? options.AlignmentConfidenceAdjustment
                    : opposed
                        ? options.OppositionConfidenceAdjustment
                        : options.UncertainConfidenceAdjustment
            : 0m;

        decimal risk = 1m;
        if (options.AdjustsRisk && directionalPattern)
        {
            decimal conflictReduction = options.MaximumConflictRiskReduction * Math.Clamp(wave.ConflictScore / 100m, 0m, 1m);
            decimal oppositionReduction = opposed ? options.MaximumConflictRiskReduction : 0m;
            risk = Math.Clamp(1m - Math.Max(conflictReduction, oppositionReduction), options.MinimumRiskMultiplier, 1m);
        }

        var reasons = new List<string>
        {
            $"NeoWavePattern:{preferred.PatternType}",
            directionalPattern
                ? aligned ? "NeoWaveAligned" : opposed ? "NeoWaveOpposed" : "NeoWaveNeutral"
                : "NeoWavePatternNonDirectional",
            conflicted ? "NeoWaveConflictHigh" : "NeoWaveConflictAcceptable"
        };

        return new NeoWaveDecisionEvidence
        {
            ConfidenceAdjustment = confidence,
            RiskMultiplier = risk,
            DirectionAligned = aligned,
            DirectionOpposed = opposed,
            ReasonCodes = reasons,
            PatternType = preferred.PatternType,
            Direction = preferred.Direction,
            HypothesisId = preferred.HypothesisId,
            StructuralScore = preferred.StructuralScore,
            Maturity = preferred.Maturity,
            ConflictScore = wave.ConflictScore,
            InvalidationPrice = directionalPattern ? wave.InvalidationPrice : null,
            InvalidationDistanceAtr = directionalPattern ? wave.InvalidationDistanceAtr : null
        };
    }

    private static NeoWaveDecisionEvidence Neutral(
        string reason,
        NeoWaveSnapshot wave,
        decimal confidenceAdjustment = 0m) => new()
    {
        ConfidenceAdjustment = confidenceAdjustment,
        RiskMultiplier = 1m,
        DirectionAligned = false,
        DirectionOpposed = false,
        ReasonCodes = [reason],
        Direction = wave.StructuralBias,
        StructuralScore = wave.StructuralScore,
        Maturity = wave.Maturity,
        ConflictScore = wave.ConflictScore,
        InvalidationPrice = null,
        InvalidationDistanceAtr = null
    };
}
