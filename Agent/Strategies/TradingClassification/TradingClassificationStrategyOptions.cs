using Brokers.Models;
using TradingClassifier.Configuration;

namespace Agent.Strategies.TradingClassification;

/// <summary>
/// Configuration for <see cref="TradingClassificationAgent"/>.
/// <para>
/// The feature/label/threshold half is <see cref="Classifier"/> - the same
/// <see cref="ClassifierOptions"/> the model was trained with. Section 27 of the blueprint requires
/// training and live to share one feature implementation; sharing the options object is what makes
/// that guarantee real rather than a convention, and <see cref="Validate"/> is where a mismatch
/// gets caught.
/// </para>
/// </summary>
public sealed record TradingClassificationStrategyOptions
{
    /// <summary>The single timeframe the model was trained on and predicts for.</summary>
    public BarInterval SignalInterval { get; init; } = BarInterval.Minutes(5);

    public ClassifierOptions Classifier { get; init; } = new();

    public decimal Quantity { get; init; } = 1_000m;

    /// <summary>
    /// Protective stop in ATR multiples.
    /// <para>
    /// The blueprint never specifies an exit: its section 26 backtest simply holds for
    /// <see cref="ClassifierOptions.PredictionHorizon"/> candles, which is the question the label
    /// asks. A live bracket is this repo's requirement, not the blueprint's, so the default is set
    /// to <see cref="ClassifierOptions.AtrTargetMultiplier"/>'s scale rather than invented
    /// independently - a stop far tighter than the move the label calls significant would stop out
    /// of exactly the trades the model is trying to find.
    /// </para>
    /// </summary>
    public decimal StopAtrMultiple { get; init; } = 1.0m;

    public decimal TargetAtrMultiple { get; init; } = 2.0m;

    public decimal MinimumRewardRisk { get; init; } = 1.5m;

    public IReadOnlySet<BarInterval> RequiredIntervals => new HashSet<BarInterval>([SignalInterval]);

    public void Validate()
    {
        if (!SignalInterval.IsValid)
            throw new ArgumentException("Trading-classification signal interval must be valid.");
        if (Quantity <= 0m)
            throw new ArgumentOutOfRangeException(nameof(Quantity));
        if (StopAtrMultiple <= 0m)
            throw new ArgumentOutOfRangeException(nameof(StopAtrMultiple));
        if (TargetAtrMultiple <= 0m)
            throw new ArgumentOutOfRangeException(nameof(TargetAtrMultiple));
        if (MinimumRewardRisk <= 0m)
            throw new ArgumentOutOfRangeException(nameof(MinimumRewardRisk));
        if (TargetAtrMultiple / StopAtrMultiple < MinimumRewardRisk)
        {
            throw new ArgumentException(
                $"Target/stop ATR multiples give a reward:risk of " +
                $"{TargetAtrMultiple / StopAtrMultiple:F2}, below the configured minimum " +
                $"{MinimumRewardRisk:F2}. PreTradeRiskManager would reject every signal.");
        }
        Classifier.Validate();
    }
}
