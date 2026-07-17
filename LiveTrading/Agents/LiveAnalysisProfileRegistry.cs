using Brokers.Models;
using ChartAnnotator.Engine;
using RiskManager.Calibration;
using TradingCore.Pipeline;

namespace LiveTrading.Agents;

/// <summary>
/// Live-side counterpart to <c>Simulator.Engine.AnalysisProfileRegistry</c>: resolves distinct
/// <see cref="ChartAnnotationOptions"/> content (sourced from each registered assignment's
/// resolved <c>LiveTradingPolicyBundle.FeaturePolicy.AnnotationOptions</c>) into de-duplicated
/// <see cref="ChartAnnotationEngine"/> instances keyed by <see cref="AnalysisProfileKey"/>.
/// Replaces the previous hardcoded <c>new ChartAnnotationEngine()</c> in
/// <c>LiveEngineHostedService.BuildActor</c>, which silently ignored every registered
/// assignment's declared annotation options in favour of library defaults.
/// </summary>
/// <remarks>
/// Full per-profile dispatch (one <c>MarketAnalysisActor</c>/output channel per distinct profile,
/// with <c>AgentSupervisor</c> routing each agent only to updates computed under its own profile)
/// is Phase 6 work - <c>AgentSupervisor.ApplyAsync</c>/<c>LiveDecisionEpochCoordinator</c> today
/// assume exactly one analysis update stream per instrument per epoch. Until that lands,
/// <c>LiveEngineHostedService.RegisterAgentsAsync</c> must reject any market whose registered
/// assignments resolve to more than one distinct profile, rather than silently evaluating some
/// agents against analysis computed under another agent's settings.
/// </remarks>
public sealed class LiveAnalysisProfileRegistry
{
    private readonly Dictionary<AnalysisProfileKey, ChartAnnotationEngine> _engines = new();

    public IReadOnlyList<AnalysisProfileKey> Profiles => _engines.Keys.ToArray();

    public AnalysisProfileKey GetOrCreateProfile(
        ChartAnnotationOptions? options,
        IReadOnlySet<BarInterval> requiredIntervals)
    {
        ChartAnnotationOptions resolved = options ?? new ChartAnnotationOptions();
        AnalysisProfileKey key = AnalysisProfileKey.Create(
            resolved, requiredIntervals, MetaLabelFeatureFactory.SchemaVersion);
        if (!_engines.ContainsKey(key))
            _engines[key] = new ChartAnnotationEngine(resolved);
        return key;
    }

    public ChartAnnotationEngine EngineFor(AnalysisProfileKey profile)
    {
        if (!_engines.TryGetValue(profile, out ChartAnnotationEngine? engine))
        {
            throw new InvalidOperationException(
                $"Analysis profile {profile.ProfileHash} was never registered via {nameof(GetOrCreateProfile)}.");
        }
        return engine;
    }
}
