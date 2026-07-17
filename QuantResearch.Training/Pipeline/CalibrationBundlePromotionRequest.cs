using Agent.Strategies;

namespace QuantResearch.Training.Pipeline;

/// <summary>
/// Wraps a <see cref="CalibrationTrainingRequest"/> with the extra fields
/// <c>Simulator.Models.TradingPolicyPromotion.CreateProfile</c> needs to turn a successful
/// training run into a draft <see cref="TradingPolicies.TradingPolicyProfile"/>. A bundle
/// candidate is inherently single-strategy (a policy profile has one <c>StrategyId</c>/
/// <c>AgentOptions</c>), so <see cref="CalibrationTrainingRequest.Strategies"/> must contain
/// exactly one entry even though training itself can pool multiple instruments/strategies for
/// statistical power.
/// </summary>
/// <remarks>
/// AGENT-01: <c>AgentOptions</c> is deliberately not a field here - <see cref="CalibrationBundleWorkflow"/>
/// derives it internally from <see cref="Training"/>'s own <c>Runtime</c>/RR/price-action
/// settings (the same settings that produced the training data), so there is no seam for a
/// caller to pass a mismatched value.
/// </remarks>
public sealed record CalibrationBundlePromotionRequest
{
    public required CalibrationTrainingRequest Training { get; init; }
    public required ProgressiveAgentKind AgentKind { get; init; }
    public required string StrategyVersion { get; init; }
    public Guid ProfileId { get; init; } = Guid.NewGuid();

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Training);
        Training.Validate();
        if (Training.Strategies.Count != 1)
        {
            throw new ArgumentException(
                "A bundle candidate is single-strategy - Training.Strategies must contain exactly one entry (training can still pool multiple instruments).");
        }
        if (string.IsNullOrWhiteSpace(StrategyVersion))
            throw new ArgumentException("StrategyVersion is required.");
    }
}
