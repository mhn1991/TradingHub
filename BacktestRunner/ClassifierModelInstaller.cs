using Agent.Configuration;
using Agent.Factories;
using Agent.Strategies;
using Agent.Strategies.TradingClassification;
using Agent.Strategies.TrendTactical;
using TradingClassifier.ML.Inference;
using TradingClassifier.ML.Training;
using TradingClassifier.Models;

namespace BacktestRunner;

/// <summary>
/// Loads a trained classifier and installs a catalogue that hands it to the ML agents.
/// <para>
/// Until this existed, <c>trading-classification</c> and <c>trend-tactical</c> at rung C both fell
/// back to a no-trade model inside the trading pipeline and reported <b>0 trades with no error</b> —
/// a broken pipe that reads exactly like a measured null result. Every classifier figure in
/// PROJECT_STATE §3.12-§3.18 therefore came from the research harness, never from the pipeline that
/// applies costs, sizing and the pre-trade risk gates.
/// </para>
/// </summary>
public static class ClassifierModelInstaller
{
    /// <summary>
    /// Installs model-backed builders. Returns the loaded artifact's description, or null when no
    /// path was supplied and the model-free default stays in place.
    /// </summary>
    /// <summary>
    /// Classifier options reconstructed from the loaded artifact, or null when no model is
    /// installed. The agent MUST build its features with these rather than its own defaults: the
    /// feature engine and the model have to agree on groups, horizon and label geometry, and a
    /// mismatch is caught by `EnsureCompatible` only because the artifact records what it was
    /// trained with.
    /// </summary>
    public static TradingClassifier.Configuration.ClassifierOptions? LoadedOptions { get; private set; }

    /// <summary>
    /// The candle interval the model was trained on. The agent's signal/trigger timeframe must match
    /// it: a 15m model fed 5m bars is scoring a different question from the one it learned.
    /// </summary>
    public static int? LoadedIntervalMinutes { get; private set; }

    /// <param name="candleIntervalMinutes">
    /// Unused for the compatibility check, which uses the artifact's own interval: the agent's
    /// signal timeframe is DERIVED from the artifact, so the two agree by construction. Passing the
    /// execution interval here compared 1m against a 15m model and rejected a correct setup.
    /// </param>
    public static string? Install(string? modelPath, int candleIntervalMinutes, string? stopModelPath = null)
    {
        LoadedOptions = null;
        LoadedIntervalMinutes = null;
        if (string.IsNullOrWhiteSpace(modelPath))
            return null;

        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"Classifier model '{modelPath}' does not exist.", modelPath);

        // Loaded once and shared: ITradingModel.Predict is called per bar per instrument, and
        // reloading the ML.NET pipeline per agent would dominate the run.
        string description = string.Empty;

        // Reconstruct the exact options the model was trained under. Everything needed is recorded
        // on the artifact, which is the point of versioning it.
        ClassifierModelArtifact artifact = ClassifierModelArtifact.Load(modelPath);
        TradingClassifier.Configuration.ClassifierOptions trained = new()
        {
            EnabledGroups = Enum.Parse<TradingClassifier.Configuration.FeatureGroups>(artifact.EnabledGroups),
            PredictionHorizon = artifact.PredictionHorizon,
            AtrTargetMultiplier = artifact.AtrTargetMultiplier,
            UseMaximumExcursionLabels = artifact.UseMaximumExcursionLabels,
            TrainingStride = artifact.TrainingStride,
            BuyProbabilityThreshold = artifact.BuyProbabilityThreshold,
            SellProbabilityThreshold = artifact.SellProbabilityThreshold
        };
        trained.Validate();
        LoadedOptions = trained;
        LoadedIntervalMinutes = artifact.CandleIntervalMinutes;

        ITradingModel Resolve(TradingClassifier.Configuration.ClassifierOptions options)
        {
            // EnsureCompatible inside Load is what stops a model trained on one feature set being
            // silently fed another: a schema mismatch fails loudly here rather than producing
            // confident predictions from misaligned columns.
            var (model, metadata) = MlNetTradingModel.Load(
                modelPath, options, artifact.CandleIntervalMinutes);
            description = metadata.ToText();
            return model;
        }

        // Optional: a saved adverse-excursion model for ML stop placement. Absent means the agent
        // keeps its structural stop rather than pretending to have a prediction.
        IStopPlacementModel? stopModel = string.IsNullOrWhiteSpace(stopModelPath)
            ? null
            : MlStopPlacementModel.Load(stopModelPath);

        TradingAgentFactory.Install(new TradingAgentCatalog(
        [
            new ProgressiveTradingAgentBuilder(ProgressiveAgentKind.Legacy),
            new ProgressiveTradingAgentBuilder(ProgressiveAgentKind.Improved),
            new StructuralConfluenceTradingAgentBuilder(),
            new DivergenceReversalTradingAgentBuilder(),
            new BreakoutDetectorTradingAgentBuilder(),
            new TradingClassificationTradingAgentBuilder(
                options => Resolve(options.Classifier)),
            new TrendTacticalTradingAgentBuilder(
                options => Resolve(options.Classifier),
                _ => stopModel)
        ]));

        // Force one resolve so a bad path or an incompatible schema fails at startup rather than
        // mid-run, and so the description is populated for the run log.
        Resolve(trained);
        return description;
    }
}
