using Agent.Models;
using ChartAnnotator.Models;
using Simulator.Models;

namespace Simulator.Replay;

internal sealed record StructuralCandidateOutcome(
    string StrategyId,
    string PlaybookId,
    string SetupId,
    PriceActionDirection Direction,
    DateTimeOffset StartedAt,
    DateTimeOffset ObservedThrough,
    decimal ReferencePrice,
    int HorizonMinutes,
    bool HorizonComplete,
    decimal MaximumFavourableExcursionBps,
    decimal MaximumAdverseExcursionBps,
    decimal? ClosingReturnBps,
    bool EventuallyReady,
    bool EventuallySelected,
    string? FirstBlockingReasonCode,
    string? LastBlockingReasonCode,
    IReadOnlyList<string> FailedGateReasonCodes);

/// <summary>
/// Research-only forward outcome recorder for structural hypotheses. It observes a fixed horizon
/// from the first meaningful catalyst and never feeds its results back into strategy decisions.
/// Direction-normalized basis-point returns keep the output comparable across instruments without
/// inventing a hypothetical stop, target, fill, or position size.
/// </summary>
internal sealed class StructuralCandidateOutcomeTracker
{
    private static readonly TimeSpan DefaultHorizon = TimeSpan.FromHours(1);
    private readonly TimeSpan _horizon;
    private readonly Dictionary<string, CandidatePath> _paths = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];

    public StructuralCandidateOutcomeTracker(TimeSpan? horizon = null)
    {
        _horizon = horizon ?? DefaultHorizon;
        if (_horizon <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(horizon));
    }

    public void Advance(DateTimeOffset availableAt, decimal high, decimal low, decimal close)
    {
        foreach (CandidatePath path in _paths.Values)
            path.Advance(availableAt, high, low, close);
    }

    public void Record(StrategyReplayEvent evt, decimal referencePrice)
    {
        if (evt.Type != StrategyReplayEventType.StructuralPlaybookEvaluated ||
            string.IsNullOrWhiteSpace(evt.PlaybookId) ||
            string.IsNullOrWhiteSpace(evt.EvaluationSetupId) ||
            evt.StructuralDirection is not PriceActionDirection direction ||
            evt.StructuralLifecycle is not (
                StructuralSetupLifecycle.CatalystObserved or
                StructuralSetupLifecycle.AwaitingTrigger or
                StructuralSetupLifecycle.CandidateProduced))
        {
            return;
        }

        string key = $"{evt.PlaybookId}|{evt.EvaluationSetupId}";
        if (!_paths.TryGetValue(key, out CandidatePath? path))
        {
            if (referencePrice <= 0m)
                return;

            path = new CandidatePath(
                evt.StrategyId,
                evt.PlaybookId,
                evt.EvaluationSetupId,
                direction,
                evt.EventTime,
                referencePrice,
                _horizon);
            _paths.Add(key, path);
            _order.Add(key);
        }

        path.Apply(evt);
    }

    public IEnumerable<StructuralCandidateOutcome> BuildOutcomes() =>
        _order.Select(key => _paths[key].ToOutcome());

    private sealed class CandidatePath
    {
        private readonly string _strategyId;
        private readonly string _playbookId;
        private readonly string _setupId;
        private readonly PriceActionDirection _direction;
        private readonly DateTimeOffset _startedAt;
        private readonly decimal _referencePrice;
        private readonly TimeSpan _horizon;
        private readonly HashSet<string> _failedGateReasonCodes = new(StringComparer.Ordinal);
        private readonly DateTimeOffset _horizonEndsAt;
        private DateTimeOffset _observedThrough;
        private decimal _maximumFavourableBps;
        private decimal _maximumAdverseBps;
        private decimal? _closingReturnBps;
        private bool _horizonComplete;
        private bool _eventuallyReady;
        private bool _eventuallySelected;
        private string? _firstBlockingReasonCode;
        private string? _lastBlockingReasonCode;

        public CandidatePath(
            string strategyId,
            string playbookId,
            string setupId,
            PriceActionDirection direction,
            DateTimeOffset startedAt,
            decimal referencePrice,
            TimeSpan horizon)
        {
            _strategyId = strategyId;
            _playbookId = playbookId;
            _setupId = setupId;
            _direction = direction;
            _startedAt = startedAt;
            _referencePrice = referencePrice;
            _horizon = horizon;
            _horizonEndsAt = startedAt + horizon;
            _observedThrough = startedAt;
        }

        public void Advance(DateTimeOffset availableAt, decimal high, decimal low, decimal close)
        {
            if (_horizonComplete || availableAt <= _startedAt)
                return;
            if (availableAt > _horizonEndsAt)
            {
                _horizonComplete = true;
                return;
            }

            decimal favourable = _direction == PriceActionDirection.Bullish
                ? ToBasisPoints(high - _referencePrice)
                : ToBasisPoints(_referencePrice - low);
            decimal adverse = _direction == PriceActionDirection.Bullish
                ? ToBasisPoints(low - _referencePrice)
                : ToBasisPoints(_referencePrice - high);
            _maximumFavourableBps = Math.Max(_maximumFavourableBps, favourable);
            _maximumAdverseBps = Math.Min(_maximumAdverseBps, adverse);
            _closingReturnBps = _direction == PriceActionDirection.Bullish
                ? ToBasisPoints(close - _referencePrice)
                : ToBasisPoints(_referencePrice - close);
            _observedThrough = availableAt;
            if (availableAt >= _horizonEndsAt)
                _horizonComplete = true;
        }

        public void Apply(StrategyReplayEvent evt)
        {
            _eventuallyReady |= evt.IsReady == true;
            _eventuallySelected |= evt.IsSelected == true;
            if (!string.IsNullOrWhiteSpace(evt.PrimaryBlockingReasonCode))
            {
                _firstBlockingReasonCode ??= evt.PrimaryBlockingReasonCode;
                _lastBlockingReasonCode = evt.PrimaryBlockingReasonCode;
            }
            foreach (string reasonCode in evt.FailedGateReasonCodes)
            {
                if (!string.IsNullOrWhiteSpace(reasonCode))
                    _failedGateReasonCodes.Add(reasonCode);
            }
        }

        public StructuralCandidateOutcome ToOutcome() => new(
            _strategyId,
            _playbookId,
            _setupId,
            _direction,
            _startedAt,
            _observedThrough,
            _referencePrice,
            checked((int)Math.Round(_horizon.TotalMinutes)),
            _horizonComplete,
            _maximumFavourableBps,
            _maximumAdverseBps,
            _closingReturnBps,
            _eventuallyReady,
            _eventuallySelected,
            _firstBlockingReasonCode,
            _lastBlockingReasonCode,
            _failedGateReasonCodes.Order(StringComparer.Ordinal).ToArray());

        private decimal ToBasisPoints(decimal priceDifference) =>
            10_000m * priceDifference / _referencePrice;
    }
}
