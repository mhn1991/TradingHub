using ChartAnnotator.Models;
using ChartAnnotator.Regime;

namespace TradeManager;

/// <summary>
/// Dispatches trade management by originating playbook (ManagedTradeState.PlaybookId, set from
/// AgentDecision.PlaybookId at entry) rather than only by strategy id. A playbook with no override
/// entry falls through to <c>fallback</c> unchanged - the strategy's normal manager, whatever it
/// already resolves to (calibrated, regime-aware, or plain StructureBasedTradeManager). Safe to
/// wrap unconditionally: an agent that never sets PlaybookId (Legacy/Improved Progressive) has a
/// null ManagedTradeState.PlaybookId, which never matches an override key, so it always falls
/// through to <c>fallback</c>.
/// </summary>
public sealed class PlaybookAwareTradeManager : IStructureBasedTradeManager, IManagementProfileResolver
{
    private readonly IStructureBasedTradeManager _fallback;
    private readonly IReadOnlyDictionary<string, IStructureBasedTradeManager> _overridesByPlaybookId;

    public PlaybookAwareTradeManager(
        IStructureBasedTradeManager fallback,
        IReadOnlyDictionary<string, PositionManagementOptions> overridesByPlaybookId)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        ArgumentNullException.ThrowIfNull(overridesByPlaybookId);
        _fallback = fallback;
        _overridesByPlaybookId = overridesByPlaybookId.ToDictionary(
            item => item.Key,
            item => (IStructureBasedTradeManager)new StructureBasedTradeManager(item.Value),
            StringComparer.Ordinal);
    }

    public TradeManagementRecommendation Evaluate(ManagedTradeState trade, AnalysisSnapshot analysis) =>
        Select(trade).Evaluate(trade, analysis);

    public TradeManagementRecommendation Evaluate(
        ManagedTradeState trade,
        AnalysisSnapshot analysis,
        TradeManagementEvaluationScope scope,
        EquityProtectionDirective? equityProtection = null) =>
        Select(trade).Evaluate(trade, analysis, scope, equityProtection);

    /// <summary>Forwards to the fallback manager so regime-profile-id reporting (see
    /// StrategySimulationSession's use of IManagementProfileResolver) keeps working transparently
    /// through this wrapper.</summary>
    public string ResolveProfileId(MarketRegime regime) =>
        _fallback is IManagementProfileResolver resolver ? resolver.ResolveProfileId(regime) : "default";

    private IStructureBasedTradeManager Select(ManagedTradeState trade) =>
        trade.PlaybookId is { } playbookId && _overridesByPlaybookId.TryGetValue(playbookId, out IStructureBasedTradeManager? manager)
            ? manager
            : _fallback;
}
