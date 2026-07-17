using Brokers.Models;
using ChartAnnotator.Models;

namespace ChartAnnotator.CurrencyStrength;

/// <summary>
/// Configuration for optional currency-strength confluence evidence. Disabled by default -
/// a new capability, and its usefulness also depends on the operator having separately
/// configured cross-market currency-strength baskets (also off by default), so this
/// evaluator's own default changes nothing for any existing run either way.
/// </summary>
public sealed record CurrencyStrengthEvidenceOptions
{
    public bool Enabled { get; init; }

    /// <summary>Minimum base-minus-quote score differential, in the trade's favor, to count as confluence.</summary>
    public decimal MinimumDifferentialForConfluence { get; init; } = 0.15m;

    /// <summary>Minimum base-minus-quote score differential, against the trade, to count as opposition.</summary>
    public decimal MinimumDifferentialForOpposition { get; init; } = 0.15m;

    /// <summary>Currency-basket coverage below this threshold is treated as unavailable, not signal.</summary>
    public decimal MinimumCoveragePercent { get; init; } = 60m;

    /// <summary>Soft confidence nudge applied per matched signal - deliberately small; this is
    /// confirmation evidence, never a hard veto.</summary>
    public decimal ConfidenceAdjustmentPerSignal { get; init; } = 4m;

    public void Validate()
    {
        if (!Enabled) return;
        if (MinimumDifferentialForConfluence <= 0m ||
            MinimumDifferentialForOpposition <= 0m ||
            MinimumCoveragePercent is < 0m or > 100m ||
            ConfidenceAdjustmentPerSignal is < 0m or > 25m)
        {
            throw new ArgumentOutOfRangeException(nameof(CurrencyStrengthEvidenceOptions));
        }
    }
}

public sealed record CurrencyStrengthEvidence
{
    public required IReadOnlyList<string> ReasonCodes { get; init; }
    public decimal ConfidenceAdjustment { get; init; }
    public decimal? Differential { get; init; }

    public static CurrencyStrengthEvidence None { get; } = new() { ReasonCodes = [] };
}

/// <summary>
/// Pure evaluator: independent, reason-coded evidence of whether a trade's base/quote
/// currency-strength differential agrees with (confluence) or opposes (opposition) the
/// trade direction. Soft evidence only - never gates or vetoes an entry. Separate
/// namespace from PortfolioManager.CurrencyStrength deliberately: PortfolioManager already
/// depends on Agent (which depends on ChartAnnotator), so referencing it back from
/// ChartAnnotator/Agent would be circular - ParseCurrencies is duplicated locally rather
/// than shared for that reason.
/// </summary>
public static class CurrencyStrengthEvidenceEvaluator
{
    public static CurrencyStrengthEvidence Evaluate(
        InstrumentKey instrument,
        CurrencyStrengthSnapshot? snapshot,
        bool isBuy,
        CurrencyStrengthEvidenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled || snapshot is null)
            return CurrencyStrengthEvidence.None;

        (string baseCurrency, string quoteCurrency) = ParseCurrencies(instrument);
        if (!snapshot.Scores.TryGetValue(baseCurrency, out decimal baseScore) ||
            !snapshot.Scores.TryGetValue(quoteCurrency, out decimal quoteScore))
            return CurrencyStrengthEvidence.None;

        bool baseCoverageOk = !snapshot.Coverage.TryGetValue(baseCurrency, out decimal baseCoverage) ||
            baseCoverage >= options.MinimumCoveragePercent;
        bool quoteCoverageOk = !snapshot.Coverage.TryGetValue(quoteCurrency, out decimal quoteCoverage) ||
            quoteCoverage >= options.MinimumCoveragePercent;
        if (!baseCoverageOk || !quoteCoverageOk)
            return CurrencyStrengthEvidence.None; // low-coverage scores must not masquerade as signal

        // Positive differential favors the base currency, i.e. favors a Buy.
        decimal differential = baseScore - quoteScore;
        var codes = new List<string>();
        decimal adjustment = 0m;

        bool confluence = isBuy
            ? differential >= options.MinimumDifferentialForConfluence
            : differential <= -options.MinimumDifferentialForConfluence;
        bool opposition = isBuy
            ? differential <= -options.MinimumDifferentialForOpposition
            : differential >= options.MinimumDifferentialForOpposition;

        if (confluence)
        {
            codes.Add("CurrencyStrengthConfluence");
            adjustment += options.ConfidenceAdjustmentPerSignal;
        }
        if (opposition)
        {
            codes.Add("CurrencyStrengthOpposition");
            adjustment -= options.ConfidenceAdjustmentPerSignal;
        }

        return codes.Count == 0
            ? CurrencyStrengthEvidence.None
            : new CurrencyStrengthEvidence
            {
                ReasonCodes = codes,
                ConfidenceAdjustment = adjustment,
                Differential = differential
            };
    }

    private static (string Base, string Quote) ParseCurrencies(InstrumentKey instrument)
    {
        string value = instrument.Value;
        string symbol = value.Contains(':') ? value[(value.IndexOf(':') + 1)..] : value;
        string[] parts = symbol.Split(['/', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            throw new ArgumentException($"Instrument '{instrument}' does not expose base/quote currencies.", nameof(instrument));
        return (parts[0].ToUpperInvariant(), parts[1].ToUpperInvariant());
    }
}
