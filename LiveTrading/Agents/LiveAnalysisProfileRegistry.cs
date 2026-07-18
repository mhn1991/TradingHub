using Brokers.Models;
using ChartAnnotator.Engine;
using RiskManager.Calibration;
using TradingCore.Pipeline;

namespace LiveTrading.Agents;

/// <summary>
/// Live-side counterpart to <c>Simulator.Engine.AnalysisProfileRegistry</c>: resolves distinct
/// <see cref="ChartAnnotationOptions"/> content (sourced from each registered assignment's
/// resolved <c>LiveTradingPolicyBundle.FeaturePolicy.AnnotationOptions</c>) into de-duplicated
/// immutable profile definitions keyed by <see cref="AnalysisProfileKey"/>. Mutable
/// <see cref="ChartAnnotationEngine"/> instances are then owned by an exact
/// (instrument, profile) pair: compatible Agents on one market share their calculation, while
/// different markets never concurrently mutate the same engine.
/// Replaces the previous hardcoded <c>new ChartAnnotationEngine()</c> in
/// <c>LiveEngineHostedService.BuildActor</c>, which silently ignored every registered
/// assignment's declared annotation options in favour of library defaults.
/// </summary>
public sealed class LiveAnalysisProfileRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<AnalysisProfileKey, ChartAnnotationOptions> _options = new();
    private readonly Dictionary<(InstrumentKey Instrument, AnalysisProfileKey Profile), ChartAnnotationEngine> _engines = new();

    public IReadOnlyList<AnalysisProfileKey> Profiles
    {
        get
        {
            lock (_sync)
                return _options.Keys.ToArray();
        }
    }

    public AnalysisProfileKey GetOrCreateProfile(
        ChartAnnotationOptions? options,
        IReadOnlySet<BarInterval> requiredIntervals)
    {
        ChartAnnotationOptions resolved = options ?? new ChartAnnotationOptions();
        AnalysisProfileKey key = AnalysisProfileKey.Create(
            resolved, requiredIntervals, MetaLabelFeatureFactory.SchemaVersion);
        lock (_sync)
            _options.TryAdd(key, resolved);
        return key;
    }

    public ChartAnnotationEngine EngineFor(InstrumentKey instrument, AnalysisProfileKey profile)
    {
        lock (_sync)
        {
            if (!_options.TryGetValue(profile, out ChartAnnotationOptions? options))
            {
                throw new InvalidOperationException(
                    $"Analysis profile {profile.ProfileHash} was never registered via {nameof(GetOrCreateProfile)}.");
            }

            var key = (instrument, profile);
            if (!_engines.TryGetValue(key, out ChartAnnotationEngine? engine))
            {
                engine = new ChartAnnotationEngine(options);
                _engines.Add(key, engine);
            }
            return engine;
        }
    }
}
