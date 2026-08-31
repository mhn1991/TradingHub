using TradingClassifier.Features;

namespace TradingClassifier.Models;

/// <summary>
/// Predicts how far a candidate is likely to move <b>against</b> the trade, in ATR, so the stop can
/// be placed from evidence rather than a fixed multiple.
/// <para>
/// This is the one prediction task in this repo with measurable signal. PROJECT_STATE §3.23 found
/// favourable excursion unpredictable (rho -0.0010) while adverse excursion reached **rho 0.0597**,
/// the only quantity above the 0.05 "no usable information" floor. Adverse excursion is largely
/// driven by volatility, which is more predictable than direction.
/// </para>
/// <para>
/// Deliberately expressed in ATR, never in R: R is defined by the stop distance, so a model
/// predicting adverse excursion in R could not be used to choose that distance without circularity.
/// </para>
/// <para>
/// Kept in this assembly, like <see cref="ITradingModel"/>, so agents can consume it without the
/// Microsoft.ML dependency entering their AOT graph.
/// </para>
/// </summary>
public interface IStopPlacementModel
{
    /// <summary>Schema the model was trained against; inference must reject a mismatched vector.</summary>
    IReadOnlyList<string> FeatureNames { get; }

    /// <summary>
    /// Expected adverse excursion in ATR multiples. Callers apply their own safety margin and
    /// bounds — the model states what it expects, not what risk to take.
    /// </summary>
    decimal PredictAdverseExcursionAtr(FeatureVector features);
}
