using System.Collections.Concurrent;
using Agent.Abstractions;
using Agent.Models;
using Brokers.Models;
using ChartAnnotator.Models;
using Networking.Notifications;
using TradingClassifier.Features;
using TradingClassifier.Models;
using TradingClassifier.Signals;

namespace Agent.Strategies.TradingClassification;

/// <summary>
/// The blueprint's section 27 live prediction pipeline, as an <see cref="ITradingAgent"/>:
/// candle closes -> shared <see cref="FeatureEngine"/> -> <see cref="ITradingModel"/> ->
/// probabilities -> <see cref="ConfidenceSignalGenerator"/> -> signal.
/// <para>
/// The model is injected rather than loaded here. Concrete models live in
/// <c>TradingClassifier.ML</c>, which depends on Microsoft.ML - not AOT-safe, and this assembly is
/// AOT-published by <c>Simulator.AotSmoke</c>. The agent only ever sees the
/// <see cref="ITradingModel"/> interface, so the AOT graph stays clean.
/// </para>
/// <para>
/// Feature state is per-instrument and incremental. The engine is fed exactly one candle per close
/// and cannot be rewound, which is what makes section 25's look-ahead guarantee hold live as well
/// as in training: the agent physically has no access to a bar it has not been given.
/// </para>
/// </summary>
public sealed class TradingClassificationAgent : ITradingAgent
{
    private readonly TradingClassificationStrategyOptions _options;
    private readonly ITradingModel _model;
    private readonly ConfidenceSignalGenerator _signals;
    private readonly ISignalNotifier _notifier;
    private readonly ConcurrentDictionary<InstrumentKey, InstrumentState> _state = new();

    /// <param name="options">Validated strategy options.</param>
    /// <param name="model">
    /// A model trained against <see cref="TradingClassificationStrategyOptions.Classifier"/>. Its
    /// feature names are checked against the schema those options generate, because a model trained
    /// on a different feature set will still score a vector of the wrong shape into confident
    /// nonsense rather than failing.
    /// </param>
    /// <param name="notifier">Optional outbound alerting; silent by default, as in every other agent.</param>
    public TradingClassificationAgent(
        TradingClassificationStrategyOptions options,
        ITradingModel model,
        ISignalNotifier? notifier = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(model);
        options.Validate();

        FeatureSchema schema = FeatureSchema.Create(options.Classifier);
        if (!schema.Names.SequenceEqual(model.FeatureNames, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"The model was trained on {model.FeatureNames.Count} features " +
                $"({string.Join(", ", model.FeatureNames.Take(4))}...) but these options generate " +
                $"{schema.Count} ({string.Join(", ", schema.Names.Take(4))}...). Train and live " +
                "feature sets must match exactly - see blueprint section 27.",
                nameof(model));
        }

        _options = options;
        _model = model;
        _signals = new ConfidenceSignalGenerator(options.Classifier);
        _notifier = notifier ?? NullSignalNotifier.Instance;
        RequiredIntervals = options.RequiredIntervals;
        TriggerInterval = options.SignalInterval;
    }

    public string Name => "Trading classification agent";
    public IReadOnlySet<BarInterval> RequiredIntervals { get; }
    public BarInterval TriggerInterval { get; }
    public AgentExitManagementMode ExitManagementMode => AgentExitManagementMode.Bracket;

    public Task<AgentDecision> EvaluateAsync(
        AgentMarketContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (!context.Analysis.TryGet(_options.SignalInterval, out AnalysisSnapshot snapshot))
            return Observe(context, $"No {BarIntervalParser.Format(_options.SignalInterval)} analysis available.");

        Candle candle = snapshot.LatestCandle;
        InstrumentState state = _state.GetOrAdd(context.Instrument, _ => new InstrumentState(_options.Classifier));

        FeatureVector? features;
        lock (state.Gate)
        {
            // The supervisor can re-evaluate the same close (a retry, or a second agent sharing the
            // context). Feeding the engine that candle twice would corrupt every incremental
            // indicator, so a repeat is skipped and the previous row reused.
            // AvailableAt, not Candle.CloseTime: the latter is nullable, and AvailableAt is the
            // repo's canonical "this became knowable at" stamp - the same one the annotation
            // engine's indicator history is keyed on.
            if (state.LastCandleClose is DateTimeOffset last && snapshot.AvailableAt <= last)
            {
                features = state.LastFeatures;
                if (features is null)
                    return Observe(context, "Still warming up; this candle was already folded in.");
            }
            else
            {
                state.LastCandleClose = snapshot.AvailableAt;
                // The annotation snapshot is passed through rather than ignored: when the Analysis
                // feature group is enabled these are the columns the model was trained on, and they
                // come from the same engine that produced the training data.
                features = state.Engine.Update(
                    new ClassifierCandle(
                        snapshot.AvailableAt,
                        candle.Prices.Open,
                        candle.Prices.High,
                        candle.Prices.Low,
                        candle.Prices.Close),
                    snapshot.Indicators);
                state.LastFeatures = features;
            }
        }

        if (features is null)
            return Observe(context, "Feature engine is still warming up.");

        // Already in a position: the bracket owns the exit, so do not re-signal every bar.
        if (context.Positions.Any(item => item.Instrument == context.Instrument && item.Quantity > 0m))
            return Observe(context, "A position is already open; its bracket owns the exit.");

        Prediction prediction = _model.Predict(features);
        TradingSignal signal = _signals.Generate(prediction);

        if (!signal.IsActionable)
            return Observe(context, signal.Reason);

        if (features.LabelAtr <= 0m)
            return Observe(context, "ATR is not usable for stop placement.");

        bool buy = signal.Action == TradeLabel.Buy;
        decimal entry = candle.Prices.Close;
        decimal stopDistance = features.LabelAtr * _options.StopAtrMultiple;
        decimal targetDistance = features.LabelAtr * _options.TargetAtrMultiple;

        decimal stop = buy ? entry - stopDistance : entry + stopDistance;
        decimal target = buy ? entry + targetDistance : entry - targetDistance;

        if (stopDistance <= 0m)
            return Observe(context, "Degenerate stop distance.");

        string reason =
            $"{(buy ? "BUY" : "SELL")} p={(buy ? prediction.BuyProbability : prediction.SellProbability):F3} " +
            $"(sell {prediction.SellProbability:F3} / no-trade {prediction.NoTradeProbability:F3} / " +
            $"buy {prediction.BuyProbability:F3}). {signal.Reason}";

        return Task.FromResult(new AgentDecision
        {
            Action = buy ? AgentAction.Buy : AgentAction.Sell,
            Instrument = context.Instrument,
            CreatedAt = context.Timestamp,
            // The model's own probability is the confidence, scaled to the 0-100 the pipeline uses.
            // Reporting a fixed number here would throw away the one thing this agent knows that a
            // rule-based one does not.
            Confidence = (decimal)(buy ? prediction.BuyProbability : prediction.SellProbability) * 100m,
            Reason = reason,
            SuggestedQuantity = _options.Quantity,
            ReferencePrice = entry,
            StopLossPrice = stop,
            TakeProfitPrice = target,
            ExpectedRewardRisk = _options.TargetAtrMultiple / _options.StopAtrMultiple,
            SignalInterval = _options.SignalInterval,
            StopSource = $"{_options.StopAtrMultiple:F2} ATR {(buy ? "below" : "above")} entry",
            TargetSource = $"{_options.TargetAtrMultiple:F2} ATR {(buy ? "above" : "below")} entry"
        });
    }

    private static Task<AgentDecision> Observe(AgentMarketContext context, string reason) =>
        Task.FromResult(new AgentDecision
        {
            Action = AgentAction.Observe,
            Instrument = context.Instrument,
            Confidence = 0m,
            CreatedAt = context.Timestamp,
            Reason = reason
        });

    /// <summary>
    /// Per-instrument feature state. One agent instance can be shared across instruments by the
    /// supervisor, and a single engine fed two instruments' candles would produce indicator values
    /// belonging to neither.
    /// </summary>
    private sealed class InstrumentState(TradingClassifier.Configuration.ClassifierOptions options)
    {
        public FeatureEngine Engine { get; } = new(options);
        public Lock Gate { get; } = new();
        public DateTimeOffset? LastCandleClose { get; set; }
        public FeatureVector? LastFeatures { get; set; }
    }
}
