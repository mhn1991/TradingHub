using System.Security.Cryptography;
using System.Text;
using Agent.Strategies;
using ChartAnnotator.CurrencyStrength;
using ChartAnnotator.Engine;
using ChartAnnotator.NeoWave;
using ChartAnnotator.Value;
using PortfolioManager.CrossMarket;
using RiskManager.Calibration;

namespace TradingCore.Pipeline;

/// <summary>
/// The declared set of analysis/decision-feature toggles a strategy run is authorized to use.
/// <see cref="SetupCalibration"/> is the only member the pipeline factory functionally consumes
/// (it builds <c>ISetupCalibrationPolicy</c> from it) - the rest are audit/provenance only today,
/// hashed into <see cref="ComputeHash"/> so a later live host can verify a running agent matches
/// what was declared, without yet enforcing anything at runtime. Deliberately excludes
/// <c>RegimeManagementOptions</c> (post-entry trade management, not entry-decision feature
/// policy) and position-sizing/risk/trading-condition/safety options (those live on
/// <c>LiveTradingPolicyBundle</c> directly).
/// </summary>
public sealed record RuntimeFeaturePolicy
{
    public required ChartAnnotationOptions AnnotationOptions { get; init; }
    public required MarketRegimePolicyOptions MarketRegimeRouting { get; init; }
    public required ValueLocationEvidenceOptions ValueLocationEvidence { get; init; }
    public required CurrencyStrengthEvidenceOptions CurrencyStrengthEvidence { get; init; }
    public required RsiBollingerSignalOptions RsiBollingerSignals { get; init; }
    public required bool DmiConfirmationEnabled { get; init; }
    public required CurrencyStrengthOptions CurrencyStrength { get; init; }
    public required SetupCalibrationPolicyOptions SetupCalibration { get; init; }
    public NeoWaveEvidenceOptions NeoWaveEvidence { get; init; } = new();

    public string ComputeHash()
    {
        // Record-generated ToString() prints dictionary/list-typed properties as their runtime
        // type name, not their contents - MarketRegimeRouting.Policies and
        // CurrencyStrength.Baskets are serialized explicitly below via dedicated identity
        // helpers; AnnotationOptions' own canonicalization (including the same problem for
        // PriceActionSetups.EnabledSetups) is shared with AnalysisProfileKey via
        // ChartAnnotationOptionsHasher.Canonicalize - see that method's doc comment for why this
        // matters. Adding a decision-affecting feature intentionally changes this hash so a
        // promoted policy cannot silently acquire NEoWave evidence without a new identity.
        string canonical =
            $"{ChartAnnotationOptionsHasher.Canonicalize(AnnotationOptions)}|" +
            $"regime:{RegimeIdentity(MarketRegimeRouting)}|" +
            $"value-location:{ValueLocationEvidence}|" +
            $"currency-strength-evidence:{CurrencyStrengthEvidence}|" +
            $"rsi-bollinger:{RsiBollingerSignals}|" +
            $"dmi:{DmiConfirmationEnabled}|" +
            $"currency-strength:{CurrencyStrengthIdentity(CurrencyStrength)}|" +
            $"setup-calibration:{SetupCalibration}|" +
            $"neowave-evidence:{NeoWaveEvidence}|" +
            $"schema:{MetaLabelFeatureFactory.SchemaVersion}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string RegimeIdentity(MarketRegimePolicyOptions options)
    {
        string policies = string.Join(';', options.Policies
            .OrderBy(entry => entry.Key)
            .Select(entry =>
                $"{entry.Key}:{entry.Value.AllowNewEntries},{entry.Value.MinimumConfidenceAdjustment}," +
                $"{entry.Value.RiskMultiplier},{entry.Value.EntryProfileId},{entry.Value.ManagementProfileId}"));
        return $"{options.Enabled},{options.MinimumRegimeConfidence},[{policies}]";
    }

    private static string CurrencyStrengthIdentity(CurrencyStrengthOptions options)
    {
        string baskets = string.Join(';', options.Baskets
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => $"{entry.Key}:{string.Join(',', entry.Value.Select(i => i.Value))}"));
        return $"{options.Enabled},{options.Interval},{options.ReturnLookbackBars}," +
            $"{options.VolatilityLookbackBars},{options.MinimumCurrencyCoveragePercent},[{baskets}]";
    }
}
