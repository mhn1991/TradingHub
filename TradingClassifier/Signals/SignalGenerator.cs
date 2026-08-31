using TradingClassifier.Configuration;
using TradingClassifier.Features;
using TradingClassifier.Models;

namespace TradingClassifier.Signals;

/// <summary>What the decision layer concluded, and why.</summary>
public readonly record struct TradingSignal
{
    public required TradeLabel Action { get; init; }
    public required Prediction Prediction { get; init; }
    public required string Reason { get; init; }

    public bool IsActionable => Action != TradeLabel.NoTrade;
}

/// <summary>Section 30's <c>ISignalGenerator</c>.</summary>
public interface ISignalGenerator
{
    TradingSignal Generate(Prediction prediction);
}

/// <summary>
/// Sections 19, 20 and 33.
/// <para>
/// Deliberately trivial, and deliberately outside the model. Section 19's worked example - BUY at
/// 0.41 with NO_TRADE at 0.35 - is the case this exists to reject: the argmax class is BUY, and
/// acting on it would be acting on noise. Keeping the thresholds here rather than baked into the
/// model means section 20's "optimise the threshold on validation data" is a configuration change,
/// not a retrain.
/// </para>
/// </summary>
public sealed class ConfidenceSignalGenerator : ISignalGenerator
{
    private readonly double _buyThreshold;
    private readonly double _sellThreshold;

    public ConfidenceSignalGenerator(ClassifierOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _buyThreshold = options.BuyProbabilityThreshold;
        _sellThreshold = options.SellProbabilityThreshold;
    }

    public TradingSignal Generate(Prediction prediction)
    {
        // Both sides clearing their thresholds is only possible with thresholds at or below 0.5.
        // It is a contradictory reading rather than a doubly-good one, so it trades neither way -
        // the same stance BreakoutDetectorAgent takes when two opposing readings fire on one candle.
        bool buy = prediction.BuyProbability >= _buyThreshold;
        bool sell = prediction.SellProbability >= _sellThreshold;

        if (buy && sell)
        {
            return new TradingSignal
            {
                Action = TradeLabel.NoTrade,
                Prediction = prediction,
                Reason = $"Buy {prediction.BuyProbability:F3} and sell {prediction.SellProbability:F3} " +
                    "both cleared their thresholds; ambiguous."
            };
        }

        if (buy)
        {
            return new TradingSignal
            {
                Action = TradeLabel.Buy,
                Prediction = prediction,
                Reason = $"P(BUY) {prediction.BuyProbability:F3} >= {_buyThreshold:F2}."
            };
        }

        if (sell)
        {
            return new TradingSignal
            {
                Action = TradeLabel.Sell,
                Prediction = prediction,
                Reason = $"P(SELL) {prediction.SellProbability:F3} >= {_sellThreshold:F2}."
            };
        }

        return new TradingSignal
        {
            Action = TradeLabel.NoTrade,
            Prediction = prediction,
            Reason = $"Below confidence: buy {prediction.BuyProbability:F3} / {_buyThreshold:F2}, " +
                $"sell {prediction.SellProbability:F3} / {_sellThreshold:F2}."
        };
    }
}
