using ChartAnnotator.Engine;
using ChartAnnotator.Models;
using Brokers.Models;
using PortfolioManager.CrossMarket;
using TradingCore.MarketData;
using TradingCore.Pipeline;

namespace Simulator.Engine;

public sealed record AnalysisProfileStatus
{
    public required AnalysisProfileKey Profile { get; init; }
}

/// <summary>
/// Resolves distinct <see cref="ChartAnnotationOptions"/> content into shared, de-duplicated
/// <see cref="ChartAnnotationEngine"/> instances keyed by <see cref="AnalysisProfileKey"/>. Two
/// assignments whose effective annotation options are identical share one engine (and therefore
/// one computed-analysis cost per candle); assignments with genuinely different options each get
/// their own engine, fully isolated from one another. Replaces the single hardcoded
/// <c>sharedAnnotator</c> instance <c>StreamingComparativeEngine</c> used to build for an entire
/// run regardless of how many distinct configurations were actually in play.
/// </summary>
public sealed class AnalysisProfileRegistry
{
    private readonly string _featureSchemaHash;
    private readonly Dictionary<AnalysisProfileKey, ChartAnnotationEngine> _engines = new();

    public AnalysisProfileRegistry(string featureSchemaHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureSchemaHash);
        _featureSchemaHash = featureSchemaHash;
    }

    public IReadOnlyList<AnalysisProfileStatus> Snapshot =>
        _engines.Keys.Select(key => new AnalysisProfileStatus { Profile = key }).ToArray();

    public AnalysisProfileKey GetOrCreateProfile(
        ChartAnnotationOptions? options,
        IReadOnlySet<BarInterval> requiredIntervals)
    {
        ChartAnnotationOptions resolved = options ?? new ChartAnnotationOptions();
        AnalysisProfileKey key = AnalysisProfileKey.Create(resolved, requiredIntervals, _featureSchemaHash);
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

    public static MarketAnalysisSnapshot Publish(
        InstrumentKey instrument,
        AnalysisProfileKey profile,
        long snapshotVersion,
        long decisionEpoch,
        DateTimeOffset availableAt,
        IReadOnlyDictionary<BarInterval, AnalysisSnapshot> timeframes,
        CurrencyStrengthSnapshot? crossMarket,
        DataQualityResult dataQuality)
    {
        return new MarketAnalysisSnapshot
        {
            Instrument = instrument,
            Profile = profile,
            SnapshotVersion = snapshotVersion,
            DecisionEpoch = decisionEpoch,
            AvailableAt = availableAt,
            Timeframes = new Dictionary<BarInterval, AnalysisSnapshot>(timeframes),
            CrossMarket = crossMarket,
            DataQuality = dataQuality
        };
    }
}
