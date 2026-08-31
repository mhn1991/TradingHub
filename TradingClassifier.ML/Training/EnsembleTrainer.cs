using TradingClassifier.Features;
using TradingClassifier.Models;

namespace TradingClassifier.ML.Training;

/// <summary>
/// Averages the probability vectors of several trained models — the ML catalogue's Phase 6
/// "ensemble models".
/// <para>
/// Averaging reduces variance, not bias. It helps when members make <i>independent</i> errors around
/// a real signal; it cannot manufacture one. PROJECT_STATE §3.18 measured LightGBM at rho 0.0044
/// against a regularised linear model's 0.0252 on the same rows, so on that data an ensemble would
/// produce a better-calibrated zero. Kept because it is cheap and the question is worth re-asking
/// against a source with an edge — but it is not a fix for a null.
/// </para>
/// </summary>
public sealed class EnsembleModel : ITradingModel
{
    private readonly IReadOnlyList<ITradingModel> _members;
    private readonly IReadOnlyList<double> _weights;

    public EnsembleModel(IReadOnlyList<ITradingModel> members, IReadOnlyList<double>? weights = null)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (members.Count == 0)
            throw new ArgumentException("An ensemble needs at least one member.", nameof(members));

        _members = members;
        if (weights is not null && weights.Count != members.Count)
            throw new ArgumentException("Weights must match member count.", nameof(weights));

        double[] resolved = weights is null
            ? [.. Enumerable.Repeat(1.0 / members.Count, members.Count)]
            : [.. weights];
        double total = resolved.Sum();
        if (total <= 0)
            throw new ArgumentException("Weights must sum to a positive value.", nameof(weights));
        _weights = [.. resolved.Select(weight => weight / total)];

        FeatureNames = members[0].FeatureNames;
    }

    public IReadOnlyList<string> FeatureNames { get; }

    public Prediction Predict(FeatureVector features)
    {
        double sell = 0, noTrade = 0, buy = 0;
        for (int index = 0; index < _members.Count; index++)
        {
            Prediction member = _members[index].Predict(features);
            double weight = _weights[index];
            sell += member.SellProbability * weight;
            noTrade += member.NoTradeProbability * weight;
            buy += member.BuyProbability * weight;
        }

        // Renormalise: members can disagree on normalisation and a weighted mean of near-normalised
        // vectors is not guaranteed to sum to one.
        double sum = sell + noTrade + buy;
        if (sum <= 0)
            return new Prediction { SellProbability = 0, NoTradeProbability = 1, BuyProbability = 0 };

        return new Prediction
        {
            SellProbability = sell / sum,
            NoTradeProbability = noTrade / sum,
            BuyProbability = buy / sum
        };
    }
}
