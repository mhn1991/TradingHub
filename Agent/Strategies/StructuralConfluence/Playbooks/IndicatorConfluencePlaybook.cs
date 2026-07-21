using Agent.Models;
using Agent.Strategies.StructuralConfluence.Evidence;
using ChartAnnotator.Models;
using ChartAnnotator.Regime;

namespace Agent.Strategies.StructuralConfluence.Playbooks;

/// <summary>
/// Entry driven purely by indicator confluence rather than structure: ADX/DMI establishes trend
/// presence and direction, RSI confirms momentum is turning in that direction (and isn't already
/// exhausted), and Bollinger Bands confirm there's enough volatility for the move to travel. Each
/// indicator answers a different question - deliberately not stacking two indicators that measure
/// the same thing (e.g. RSI and CCI would both just be momentum oscillators agreeing with
/// themselves). Has no zone/pool/level to anchor risk to, so unlike the other three playbooks its
/// stop/target is a plain ATR multiple.
/// </summary>
public sealed class IndicatorConfluencePlaybook : IStructuralPlaybook
{
    public const string StableId = "structural.indicator-confluence";
    private readonly StructuralConfluenceStrategyOptions _root;
    private readonly IndicatorConfluenceOptions _options;

    public IndicatorConfluencePlaybook(StructuralConfluenceStrategyOptions options)
    {
        _root = options;
        _options = options.IndicatorConfluence;
    }

    public string PlaybookId => StableId;
    public string Version => "1.1";

    public PlaybookEvaluation Evaluate(StructuralEvidencePacket evidence, PlaybookRuntimeState state)
    {
        IndicatorConfirmationPacket indicators = evidence.Indicators;
        AdxAnalysisSnapshot adx = indicators.AdxAnalysis;

        // ADX/DMI supplies both "is there a trend" and "which way" - there is no structural zone
        // to derive direction from here, so the trend indicator itself is the direction source.
        if (adx.DirectionalBias == PriceActionDirection.Neutral)
            return Dormant("StructuralIndicatorTrendUnavailable");

        PriceActionDirection direction = adx.DirectionalBias;
        bool buy = direction == PriceActionDirection.Bullish;
        decimal price = evidence.Trigger.LatestCandle.Prices.Close;
        // Prefer setup ATR for geometry so stop distance matches the structural TF; fall back to
        // trigger ATR when setup is not ready (should be rare once evidence packet is valid).
        decimal? atr = _options.PreferSetupAtrForGeometry
            ? evidence.Setup.Indicators.Atr ?? evidence.Indicators.Atr
            : evidence.Indicators.Atr ?? evidence.Setup.Indicators.Atr;

        RsiAnalysisSnapshot rsi = indicators.RsiAnalysis;
        decimal? rsiValue = indicators.Rsi;
        BollingerAnalysisSnapshot bollinger = indicators.BollingerAnalysis;

        bool trendPasses = adx.Adx is decimal adxValue && adxValue >= _options.MinimumAdx &&
            (!_options.RequireTrendStrengthening || adx.IsTrendStrengthening);

        bool momentumDirectionMatches = buy
            ? rsi.MomentumDirection == MomentumDirection.Rising
            : rsi.MomentumDirection == MomentumDirection.Falling;
        bool momentumInBand = rsiValue is decimal rsiVal &&
            (buy
                ? rsiVal >= _options.MinimumRsiForBuy && rsiVal <= _options.MaximumRsiForBuy
                : rsiVal >= _options.MinimumRsiForSell && rsiVal <= _options.MaximumRsiForSell);
        bool momentumPasses = momentumDirectionMatches && momentumInBand;

        bool volatilityPasses = _options.RequireSqueezeBreakout
            ? !bollinger.IsSqueeze && bollinger.WidthDirection == VolatilityDirection.Expanding
            : !bollinger.IsSqueeze;

        bool contextPasses = !_options.RequireContextAlignment || IsContextAligned(evidence, direction);
        bool cooldownPasses = IsCooldownElapsed(evidence, state);

        decimal adxQuality = adx.Adx is decimal adxForQuality
            ? Math.Clamp(50m + (adxForQuality - _options.MinimumAdx) * 2m, 0m, 100m)
            : 0m;
        // Distance from neutral (50), not the raw RSI value - using RSI itself as quality made
        // Confidence = min(gate qualities) silently require RSI >= MinimumConfidence (55 by
        // default) for every buy, since that gate's quality would otherwise sit below the
        // confidence floor even when the actual momentum gate (momentumPasses above) had already
        // passed. Centering on neutral means a fresh momentum shift right off 50 doesn't get
        // penalized just for not yet being deep into overbought/oversold territory.
        decimal rsiQuality = rsiValue is decimal rsiForQuality
            ? Math.Clamp(50m + (buy ? rsiForQuality - 50m : 50m - rsiForQuality) * 1.5m, 0m, 100m)
            : 0m;
        decimal bollingerQuality = bollinger.WidthPercentile is decimal widthPercentile
            ? Math.Clamp(widthPercentile, 0m, 100m)
            : 50m;
        decimal contextQuality = buy
            ? evidence.ContextEvidence.BullishQuality
            : evidence.ContextEvidence.BearishQuality;

        var gates = new List<MandatoryGate>
        {
            Gate("Trend", trendPasses, adxQuality, "StructuralIndicatorTrendBelowMinimum"),
            Gate("Momentum", momentumPasses, rsiQuality, "StructuralIndicatorMomentumNotAligned"),
            Gate("Volatility", volatilityPasses, bollingerQuality, "StructuralIndicatorVolatilityInsufficient"),
            Gate("Context", contextPasses, Math.Clamp(contextQuality, 0m, 100m), "StructuralIndicatorContextOpposed"),
            Gate("Cooldown", cooldownPasses, cooldownPasses ? 70m : 0m, "StructuralIndicatorCooldownActive")
        };

        StructuralGeometry geometry = BuildAtrGeometry(direction, price, atr, evidence.ExecutableSpread);
        gates.Add(Gate("Geometry", geometry.IsValid, geometry.Quality, geometry.ReasonCode));

        // Unlike the other three playbooks, there is no persistent zone/pool/break event to anchor
        // identity to - confluence is recomputed fresh from indicator state every bar. Without this,
        // a fresh SetupId (and CatalystAt) was minted every single bar confluence held, breaking any
        // consumer that dedupes or tracks a setup by SetupId across bars and re-arming a "new" setup
        // every evaluation even while the same trade opportunity was still live. Carry the prior
        // bar's identity forward for as long as the same direction stays ready; only mint a new one
        // when this is a fresh (or re-armed, or direction-flipped) candidate.
        //
        // continuedWithoutGap guards against a second failure mode: StructuralConfluenceAgent skips
        // Evaluate entirely for the whole time a position is open, so state.LastEvaluation stays
        // frozen at whatever it was when that position was entered. If indicators are STILL
        // confluent the instant that position closes, the carry-forward above would otherwise reuse
        // the exact same SetupId for what is functionally a brand-new entry attempt - the same
        // re-entry-after-close bug found and fixed (via a discrete NotAlreadySignaled gate) in the
        // other three playbooks. A gap larger than about one evaluation cycle means evaluation was
        // suspended (position open), so carry-forward is skipped and a fresh identity is minted.
        bool provisionalReady = gates.All(item => item.Passed);
        bool continuedWithoutGap = state.LastAvailableAt != DateTimeOffset.MinValue &&
            evidence.AvailableAt - state.LastAvailableAt <= StructuralPlaybookRules.Bars(evidence.Trigger.Interval, 2);
        DateTimeOffset catalystAt;
        string setupId;
        if (provisionalReady && continuedWithoutGap &&
            state.LastEvaluation is { IsReady: true, SetupId: not null } lastReady &&
            lastReady.Direction == direction)
        {
            setupId = lastReady.SetupId;
            catalystAt = lastReady.CatalystAt ?? evidence.AvailableAt;
        }
        else
        {
            catalystAt = evidence.AvailableAt;
            string catalystIdentity = $"{catalystAt:O}-{price}";
            setupId = StructuralIdentity.Create(_root.StrategyVersion, evidence.Instrument, PlaybookId,
                null, null, catalystIdentity, catalystAt, direction);
        }

        // Sticky LastReadySetupId: refuse to re-signal the exact identity already traded.
        bool notAlreadySignaled = setupId != state.LastReadySetupId;
        gates.Add(Gate("NotAlreadySignaled", notAlreadySignaled, notAlreadySignaled ? 70m : 0m,
            "StructuralIndicatorAlreadySignaled"));

        decimal confidence = StructuralPlaybookRules.Confidence(gates, 0m, 0m, 0m);
        bool ready = gates.All(item => item.Passed) && confidence >= _options.MinimumConfidence;

        return new PlaybookEvaluation
        {
            PlaybookId = PlaybookId,
            Version = Version,
            Direction = direction,
            Lifecycle = ready ? StructuralSetupLifecycle.CandidateProduced : StructuralSetupLifecycle.Dormant,
            SetupId = setupId,
            CatalystAt = catalystAt,
            MandatoryGates = gates.AsReadOnly(),
            SupportingEvidence = BuildSupport(trendPasses, momentumPasses, volatilityPasses, contextPasses),
            ConflictingEvidence = [],
            ConfidenceContributions =
            [
                new("Trend", 0m, trendPasses ? "StructuralIndicatorTrendAligned" : "StructuralIndicatorTrendMissing"),
                new("Momentum", 0m, momentumPasses ? "StructuralIndicatorMomentumAligned" : "StructuralIndicatorMomentumMissing")
            ],
            ContextQuality = Math.Max(adxQuality, contextQuality),
            LocationQuality = 0m,
            CatalystQuality = rsiQuality,
            TriggerQuality = rsiQuality,
            ConfirmationQuality = bollingerQuality,
            GeometryQuality = geometry.Quality,
            Confidence = confidence,
            ExpiresAt = catalystAt + StructuralPlaybookRules.Bars(evidence.Trigger.Interval, _root.MaximumArmedSetupBars),
            ReasonCode = ready ? "StructuralIndicatorConfluenceCandidate" : FirstFailure(gates, confidence, _options.MinimumConfidence),
            IsReady = ready,
            Geometry = geometry
        };
    }

    private bool IsCooldownElapsed(StructuralEvidencePacket evidence, PlaybookRuntimeState state)
    {
        if (_options.MinimumBarsBetweenEntries <= 0)
            return true;
        // Only enforce after we have already produced a ready (and typically traded) identity.
        if (state.LastReadySetupId is null)
            return true;
        DateTimeOffset? lastSignalAt = state.LastEvaluation?.CatalystAt;
        if (lastSignalAt is null)
            return true;
        TimeSpan cooldown = StructuralPlaybookRules.Bars(evidence.Trigger.Interval, _options.MinimumBarsBetweenEntries);
        return evidence.AvailableAt >= lastSignalAt.Value + cooldown;
    }

    private static bool IsContextAligned(StructuralEvidencePacket evidence, PriceActionDirection direction)
    {
        if (evidence.Context.MarketRegime.Regime == MarketRegime.IlliquidUnsafe)
            return false;

        MarketRegime regime = evidence.Context.MarketRegime.Regime;
        bool structureOk = direction == PriceActionDirection.Bullish
            ? regime is MarketRegime.TrendingUp or MarketRegime.BreakoutExpansionUp
                || evidence.Context.MarketStructure.Direction == MarketStructureDirection.Rising
            : regime is MarketRegime.TrendingDown or MarketRegime.BreakoutExpansionDown
                || evidence.Context.MarketStructure.Direction == MarketStructureDirection.Falling;

        EvidenceAlignment alignment = direction == PriceActionDirection.Bullish
            ? evidence.ContextEvidence.BullishAlignment
            : evidence.ContextEvidence.BearishAlignment;

        // Allow when context is explicitly aligned or structure/regime agrees; block hard conflict.
        if (alignment == EvidenceAlignment.Conflicting)
            return false;
        if (alignment == EvidenceAlignment.Aligned)
            return true;
        return structureOk;
    }

    private StructuralGeometry BuildAtrGeometry(
        PriceActionDirection direction,
        decimal price,
        decimal? atr,
        decimal executableSpread)
    {
        if (atr is not > 0m)
            return Invalid(price, "StructuralIndicatorAtrUnavailable");

        bool buy = direction == PriceActionDirection.Bullish;
        decimal entry = price + (buy ? executableSpread : -executableSpread) / 2m;
        decimal stop = entry + (buy ? -1m : 1m) * _options.StopAtr * atr.Value;
        decimal target = entry + (buy ? 1m : -1m) * _options.TargetAtr * atr.Value;
        decimal risk = buy ? entry - stop : stop - entry;
        decimal reward = buy ? target - entry : entry - target;
        if (risk <= 0m || reward <= 0m)
            return Invalid(entry, "StructuralIndicatorGeometryInvalid");

        decimal rewardRisk = reward / risk;
        decimal quality = Math.Clamp(50m + Math.Min(40m, (rewardRisk - 1m) * 15m), 0m, 100m);
        return new StructuralGeometry
        {
            IsValid = true,
            Entry = entry,
            Stop = stop,
            Target = target,
            RewardRisk = rewardRisk,
            Quality = quality,
            StopSource = $"AtrMultiple:{_options.StopAtr:F2}",
            TargetSource = $"AtrMultiple:{_options.TargetAtr:F2}",
            ReasonCode = "StructuralIndicatorGeometryValid"
        };
    }

    private static StructuralGeometry Invalid(decimal entry, string reasonCode) => new()
    {
        IsValid = false,
        Entry = entry,
        ReasonCode = reasonCode
    };

    private PlaybookEvaluation Dormant(string reasonCode) => new()
    {
        PlaybookId = PlaybookId,
        Version = Version,
        Direction = PriceActionDirection.Neutral,
        Lifecycle = StructuralSetupLifecycle.Dormant,
        ReasonCode = reasonCode
    };

    private static IReadOnlyList<string> BuildSupport(bool trend, bool momentum, bool volatility, bool context)
    {
        var result = new List<string>();
        if (trend) result.Add("StructuralIndicatorTrendAligned");
        if (momentum) result.Add("StructuralIndicatorMomentumAligned");
        if (volatility) result.Add("StructuralIndicatorVolatilitySupportive");
        if (context) result.Add("StructuralIndicatorContextAligned");
        return result.AsReadOnly();
    }

    private static MandatoryGate Gate(string name, bool passed, decimal quality, string failure) =>
        new(name, passed, Math.Clamp(quality, 0m, 100m), passed ? $"{name}Passed" : failure);

    private static string FirstFailure(IReadOnlyList<MandatoryGate> gates, decimal confidence, decimal minimum) =>
        gates.FirstOrDefault(item => !item.Passed)?.ReasonCode ??
        (confidence < minimum ? "StructuralConfidenceBelowMinimum" : "StructuralCandidateNotReady");
}
