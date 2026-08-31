using TradingClassifier.Features;

namespace TradingClassifier.Models;

/// <summary>
/// Section 32's prediction object. Probabilities only - deciding what to do with them is the
/// signal generator's job, which is what lets section 33's trading rules change without retraining.
/// </summary>
public readonly record struct Prediction
{
    public required double SellProbability { get; init; }
    public required double NoTradeProbability { get; init; }
    public required double BuyProbability { get; init; }

    /// <summary>
    /// The argmax class. Section 19 is explicit that this must not drive execution on its own -
    /// it is here for the confusion matrix and macro-F1 in section 21, which are argmax metrics.
    /// </summary>
    public TradeLabel MostLikely
    {
        get
        {
            if (BuyProbability >= SellProbability && BuyProbability >= NoTradeProbability)
                return TradeLabel.Buy;
            return SellProbability >= NoTradeProbability ? TradeLabel.Sell : TradeLabel.NoTrade;
        }
    }

    public static Prediction FromScores(IReadOnlyList<float> scores)
    {
        ArgumentNullException.ThrowIfNull(scores);
        if (scores.Count != 3)
        {
            throw new ArgumentException(
                $"Expected 3 class scores (Sell, NoTrade, Buy) but got {scores.Count}.",
                nameof(scores));
        }

        // Index order is TradeLabel's numeric order, which is also the ML.NET key order.
        return new Prediction
        {
            SellProbability = scores[(int)TradeLabel.Sell],
            NoTradeProbability = scores[(int)TradeLabel.NoTrade],
            BuyProbability = scores[(int)TradeLabel.Buy]
        };
    }
}

/// <summary>Section 30's <c>ITradingModel</c>.</summary>
public interface ITradingModel
{
    /// <summary>
    /// The schema the model was trained against. Inference must reject a feature vector built from
    /// different options - silently scoring a 42-column model with a 30-column vector produces
    /// confident nonsense rather than an error.
    /// </summary>
    IReadOnlyList<string> FeatureNames { get; }

    Prediction Predict(FeatureVector features);
}

/// <summary>
/// Always returns the training set's class distribution, ignoring its input.
/// <para>
/// Section 21's warning made concrete: on a 75% NO_TRADE dataset this scores 75% accuracy while
/// being worth nothing. Every real model is compared against it, and a LightGBM run that fails to
/// beat it is a signal to go back to the features and the target rather than to tune harder.
/// </para>
/// </summary>
public sealed class PriorProbabilityModel : ITradingModel
{
    private readonly Prediction _prior;

    public PriorProbabilityModel(IReadOnlyDictionary<TradeLabel, int> classCounts, IReadOnlyList<string> featureNames)
    {
        ArgumentNullException.ThrowIfNull(classCounts);
        double total = classCounts.Values.Sum();
        if (total <= 0)
            throw new ArgumentException("Class counts must contain at least one row.", nameof(classCounts));

        _prior = new Prediction
        {
            SellProbability = classCounts.GetValueOrDefault(TradeLabel.Sell) / total,
            NoTradeProbability = classCounts.GetValueOrDefault(TradeLabel.NoTrade) / total,
            BuyProbability = classCounts.GetValueOrDefault(TradeLabel.Buy) / total
        };
        FeatureNames = featureNames;
    }

    public IReadOnlyList<string> FeatureNames { get; }

    public Prediction Predict(FeatureVector features) => _prior;
}

/// <summary>
/// Reports certainty that there is nothing to do.
/// <para>
/// This is what <c>TradingClassificationAgent</c> gets when no trained model has been supplied.
/// The alternative - refusing to construct - would make the agent unlistable in the catalogue and
/// unresolvable from a saved definition, so instead it resolves, validates, warms up its features
/// and observes. Zero trades from this model is a configuration statement ("no model was
/// injected"), not a fault, and it is distinguishable from a trained model that simply found
/// nothing because <see cref="IsUntrained"/> is surfaced in the agent's descriptor.
/// </para>
/// </summary>
public sealed class NoTradeModel : ITradingModel
{
    public NoTradeModel(IReadOnlyList<string> featureNames)
    {
        ArgumentNullException.ThrowIfNull(featureNames);
        FeatureNames = featureNames;
    }

    public IReadOnlyList<string> FeatureNames { get; }

    public static bool IsUntrained(ITradingModel model) => model is NoTradeModel;

    public Prediction Predict(FeatureVector features) => new()
    {
        SellProbability = 0d,
        NoTradeProbability = 1d,
        BuyProbability = 0d
    };
}
