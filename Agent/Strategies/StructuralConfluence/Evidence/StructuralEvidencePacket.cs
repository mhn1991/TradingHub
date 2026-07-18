using Brokers.Models;
using ChartAnnotator.Liquidity;
using ChartAnnotator.Models;
using ChartAnnotator.SupplyDemand;

namespace Agent.Strategies.StructuralConfluence.Evidence;

public enum EvidenceAlignment
{
    Unavailable,
    Neutral,
    Aligned,
    Conflicting
}

public sealed record StructuralContextEvidence(
    EvidenceAlignment BullishAlignment,
    EvidenceAlignment BearishAlignment,
    decimal BullishQuality,
    decimal BearishQuality);

public sealed record SupplyDemandPacket(
    IReadOnlyList<SupplyDemandZone> Zones,
    IReadOnlyList<SupplyDemandZoneEvent> Events);

public sealed record LiquidityPacket(
    IReadOnlyList<LiquidityPool> Pools,
    IReadOnlyList<LiquidityEvent> Events,
    IReadOnlyList<LiquiditySweepEvent> Sweeps);

public sealed record TriggerPacket(
    IReadOnlyList<PriceActionEvent> Events,
    IReadOnlyList<PriceActionSetup> Setups);

public sealed record IndicatorConfirmationPacket(
    decimal? Atr,
    decimal? Cci,
    CciAnalysisSnapshot CciAnalysis,
    decimal? Rsi,
    RsiAnalysisSnapshot RsiAnalysis,
    BollingerAnalysisSnapshot BollingerAnalysis,
    AdxAnalysisSnapshot AdxAnalysis,
    decimal? EfficiencyRatio);

public sealed record StructuralEvidencePacket
{
    public required InstrumentKey Instrument { get; init; }
    public required DateTimeOffset AvailableAt { get; init; }
    public decimal ExecutableSpread { get; init; }
    public required AnalysisSnapshot Context { get; init; }
    public required AnalysisSnapshot Setup { get; init; }
    public required AnalysisSnapshot Trigger { get; init; }
    public required IReadOnlyList<AnalysisSnapshot> AdditionalContexts { get; init; }
    public required StructuralContextEvidence ContextEvidence { get; init; }
    public required SupplyDemandPacket SupplyDemand { get; init; }
    public required LiquidityPacket Liquidity { get; init; }
    public required TriggerPacket TriggerEvidence { get; init; }
    public required IndicatorConfirmationPacket Indicators { get; init; }
}
