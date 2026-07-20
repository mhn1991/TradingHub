using Simulator.Calibration;

namespace Simulator.Experiments.IndicatorCalibration;

/// <summary>
/// The canonical, immutable identity of one backtest evaluation (blueprint §12). The
/// content-addressed candidate cache key is a hash of this entire record - two evaluations only
/// share a cache entry when every execution-affecting input is identical. This is a research-side
/// concern (candidate evaluation, per blueprint §4.1), not a live-consumed contract - it lives
/// here rather than in the shared <c>Calibration</c> project.
/// </summary>
public sealed record BacktestEvaluationIdentity
{
    public required string StrategyId { get; init; }
    public required string StrategyImplementationHash { get; init; }
    public required string EffectiveAgentDefinitionHash { get; init; }
    public required string CompleteOptionsHash { get; init; }
    public required string FeatureSwitchHash { get; init; }

    public required string Instrument { get; init; }
    public required string CandleDataHash { get; init; }
    public required string PriceComponent { get; init; }

    public required DateTimeOffset WindowStart { get; init; }
    public required DateTimeOffset WindowEnd { get; init; }
    public required DateTimeOffset WarmupStart { get; init; }

    public required string TimeframeTopologyHash { get; init; }
    public required string ExecutionModelVersion { get; init; }
    public required string BrokerCostModelHash { get; init; }
    public required string DataQualityPolicyVersion { get; init; }

    public required int RandomSeed { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(StrategyId) ||
            string.IsNullOrWhiteSpace(StrategyImplementationHash) ||
            string.IsNullOrWhiteSpace(EffectiveAgentDefinitionHash) ||
            string.IsNullOrWhiteSpace(CompleteOptionsHash) ||
            string.IsNullOrWhiteSpace(FeatureSwitchHash) ||
            string.IsNullOrWhiteSpace(Instrument) ||
            string.IsNullOrWhiteSpace(CandleDataHash) ||
            string.IsNullOrWhiteSpace(PriceComponent) ||
            string.IsNullOrWhiteSpace(TimeframeTopologyHash) ||
            string.IsNullOrWhiteSpace(ExecutionModelVersion) ||
            string.IsNullOrWhiteSpace(BrokerCostModelHash) ||
            string.IsNullOrWhiteSpace(DataQualityPolicyVersion))
        {
            throw new ArgumentException("Backtest evaluation identity fields are required.");
        }
        if (WindowStart >= WindowEnd)
            throw new ArgumentException("WindowStart must be earlier than WindowEnd.");
        if (WarmupStart > WindowStart)
            throw new ArgumentException("WarmupStart must not be later than WindowStart.");
    }

    /// <summary>The candidate-cache key: a canonical hash of every field above.</summary>
    public string ComputeCacheKey()
    {
        Validate();
        return IndicatorCalibrationHash.ComputeOfObject(new
        {
            StrategyId,
            StrategyImplementationHash,
            EffectiveAgentDefinitionHash,
            CompleteOptionsHash,
            FeatureSwitchHash,
            Instrument,
            CandleDataHash,
            PriceComponent,
            WindowStart,
            WindowEnd,
            WarmupStart,
            TimeframeTopologyHash,
            ExecutionModelVersion,
            BrokerCostModelHash,
            DataQualityPolicyVersion,
            RandomSeed
        });
    }
}
