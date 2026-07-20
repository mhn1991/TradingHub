using Agent.Configuration;
using Agent.Models;
using Brokers.Models;
using ChartAnnotator.Models;
using ChartAnnotator.Regime;
using LiveTrading.Agents;
using LiveTrading.Actors;
using LiveTrading.Configuration;
using LiveTrading.MarketData;
using LiveTrading.Persistence;
using TradeManager;

namespace LiveTrading.Shadow.Outcomes;

public interface ILiveShadowOutcomeService
{
    LiveShadowOutcomeSnapshot Snapshot { get; }
    LiveShadowOutcomeCheckpoint Checkpoint { get; }
    Task RestoreAsync(LiveShadowOutcomeCheckpoint? checkpoint, CancellationToken cancellationToken);
    Task<ShadowAdmissionResult> AdmitAsync(
        LiveTradeCandidate candidate,
        StrategyActivationMode mode,
        LiveQuoteSnapshot? quote,
        CancellationToken cancellationToken);
    Task OnQuoteAsync(LiveQuoteSnapshot quote, CancellationToken cancellationToken);
    Task OnCompletedCandleAsync(Candle candle, CancellationToken cancellationToken);
    Task OnAnalysisAsync(
        MarketAnalysisUpdate update,
        LiveQuoteSnapshot? quote,
        CancellationToken cancellationToken);
    Task<ShadowManagementEvent> ApplyManagementAsync(
        ShadowManagementRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Broker-independent paper outcome engine for ObserveOnly and Shadow deployments. It never owns
/// or receives a broker client and therefore cannot submit, amend, or close a real order.
/// </summary>
public sealed class LiveShadowOutcomeService : ILiveShadowOutcomeService
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly LiveShadowOutcomeOptions _options;
    private readonly ILiveTradingPersistence _persistence;
    private readonly TimeProvider _timeProvider;
    private readonly ILivePolicyRegistry? _policyRegistry;
    private readonly HashSet<string> _seenCandidateIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ShadowPaperPosition> _positions = new(StringComparer.Ordinal);
    private long _candidateCount;
    private long _admittedCount;
    private long _rejectedCount;
    private long _completedCount;
    private long _ambiguousCount;
    private decimal _netProfitLoss;
    private decimal _netR;
    private string? _lastPlaybookId;
    private string? _lastCandidateId;
    private string? _lastRejection;
    private int? _lastPolicyRevision;

    public LiveShadowOutcomeService(
        LiveShadowOutcomeOptions options,
        ILiveTradingPersistence persistence,
        TimeProvider timeProvider,
        ILivePolicyRegistry? policyRegistry = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _policyRegistry = policyRegistry;
    }

    public LiveShadowOutcomeSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                ShadowPaperPosition[] positions = OrderedPositions();
                ShadowPaperPosition? latest = positions.MaxBy(position => position.UpdatedAt);
                return new LiveShadowOutcomeSnapshot
                {
                    Enabled = _options.Enabled,
                    CandidateCount = _candidateCount,
                    AdmittedCount = _admittedCount,
                    RejectedCount = _rejectedCount,
                    OpenPositionCount = positions.Length,
                    CompletedCount = _completedCount,
                    AmbiguousCount = _ambiguousCount,
                    NetProfitLoss = _netProfitLoss,
                    NetR = _netR,
                    UnrealizedProfitLoss = positions.Sum(Unrealized),
                    LastPlaybookId = _lastPlaybookId,
                    LastCandidateId = _lastCandidateId,
                    LastRejection = _lastRejection,
                    LastStopPrice = latest?.StopPrice,
                    LastTargetPrice = latest?.TargetPrice,
                    LastPolicyRevision = _lastPolicyRevision,
                    OpenPositions = positions
                };
            }
        }
    }

    public LiveShadowOutcomeCheckpoint Checkpoint
    {
        get
        {
            lock (_sync)
            {
                return new LiveShadowOutcomeCheckpoint
                {
                    SeenCandidateIds = _seenCandidateIds.Order(StringComparer.Ordinal).ToArray(),
                    OpenPositions = OrderedPositions(),
                    CandidateCount = _candidateCount,
                    AdmittedCount = _admittedCount,
                    RejectedCount = _rejectedCount,
                    CompletedCount = _completedCount,
                    AmbiguousCount = _ambiguousCount,
                    NetProfitLoss = _netProfitLoss,
                    NetR = _netR,
                    LastPlaybookId = _lastPlaybookId,
                    LastCandidateId = _lastCandidateId,
                    LastRejection = _lastRejection,
                    LastPolicyRevision = _lastPolicyRevision
                };
            }
        }
    }

    public async Task RestoreAsync(
        LiveShadowOutcomeCheckpoint? checkpoint,
        CancellationToken cancellationToken)
    {
        if (checkpoint is null)
            return;
        if (checkpoint.SchemaVersion != 1)
            throw new InvalidDataException("The shadow outcome checkpoint schema is unsupported.");
        var ambiguous = new List<ShadowTradeOutcome>();
        lock (_sync)
        {
            _seenCandidateIds.Clear();
            foreach (string id in checkpoint.SeenCandidateIds.Where(id => !string.IsNullOrWhiteSpace(id)))
                _seenCandidateIds.Add(id);
            _positions.Clear();
            long invalid = 0;
            foreach ((ShadowPaperPosition position, int index) in checkpoint.OpenPositions
                         .Select((position, index) => (position, index)))
            {
                if (IsRecoverable(position))
                {
                    _positions[position.PositionId] = position;
                    _seenCandidateIds.Add(position.CandidateId);
                }
                else
                {
                    invalid++;
                    ambiguous.Add(RecoveryAmbiguity(position, index));
                }
            }
            _candidateCount = checkpoint.CandidateCount;
            _admittedCount = checkpoint.AdmittedCount;
            _rejectedCount = checkpoint.RejectedCount;
            _completedCount = checkpoint.CompletedCount;
            _ambiguousCount = checkpoint.AmbiguousCount + invalid;
            _netProfitLoss = checkpoint.NetProfitLoss;
            _netR = checkpoint.NetR;
            _lastPlaybookId = checkpoint.LastPlaybookId;
            _lastCandidateId = checkpoint.LastCandidateId;
            _lastRejection = checkpoint.LastRejection;
            _lastPolicyRevision = checkpoint.LastPolicyRevision;
        }
        foreach (ShadowTradeOutcome outcome in ambiguous)
        {
            await _persistence.AppendAsync("shadow-outcomes", outcome, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task<ShadowAdmissionResult> AdmitAsync(
        LiveTradeCandidate candidate,
        StrategyActivationMode mode,
        LiveQuoteSnapshot? quote,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            lock (_sync)
            {
                if (_seenCandidateIds.Contains(candidate.CandidateId))
                {
                    return new ShadowAdmissionResult
                    {
                        Admission = Admission(candidate, mode, ShadowCandidateDisposition.Duplicate, now,
                            quote, "DuplicateCandidate", "The stable candidate id was already observed."),
                        Position = _positions.Values.SingleOrDefault(position =>
                            position.CandidateId == candidate.CandidateId)
                    };
                }
            }

            ShadowCandidateAdmission admission;
            ShadowPaperPosition? opened = null;
            if (!_options.Enabled || mode == StrategyActivationMode.ObserveOnly)
            {
                admission = Admission(candidate, mode, ShadowCandidateDisposition.Observed, now, quote,
                    _options.Enabled ? null : "ShadowOutcomesDisabled",
                    _options.Enabled
                        ? "ObserveOnly records diagnostics and never opens a paper position."
                        : "The shadow outcome service is disabled.");
            }
            else if (mode != StrategyActivationMode.Shadow)
            {
                admission = Admission(candidate, mode, ShadowCandidateDisposition.Rejected, now, quote,
                    "UnsupportedDeploymentMode", "Only Shadow deployments may open paper positions.");
            }
            else if (!TryValidate(candidate, quote, out string? rejection))
            {
                admission = Admission(candidate, mode, ShadowCandidateDisposition.Rejected, now, quote,
                    rejection, "The candidate failed executable paper-entry validation.");
            }
            else
            {
                bool atCapacity;
                bool instrumentOccupied;
                lock (_sync)
                {
                    atCapacity = _positions.Count >= _options.MaximumOpenPaperPositions;
                    instrumentOccupied = _options.RejectSecondPositionPerInstrument &&
                        _positions.Values.Any(position => position.Instrument == candidate.Instrument);
                }
                if (atCapacity || instrumentOccupied)
                {
                    admission = Admission(candidate, mode, ShadowCandidateDisposition.Rejected, now, quote,
                        atCapacity ? "PaperPortfolioCapacity" : "PaperInstrumentAlreadyOpen",
                        atCapacity
                            ? "The bounded paper portfolio is at capacity."
                            : "The paper portfolio already has an open position on this instrument.");
                }
                else
                {
                    opened = OpenPosition(candidate, quote!, now, out ShadowPaperFill fill);
                    admission = Admission(candidate, mode, ShadowCandidateDisposition.Admitted, now, quote,
                        null, "The validated candidate was filled against the executable quote side.") with
                    {
                        EntryFill = fill,
                        OpenedPosition = opened
                    };
                }
            }

            // Canonical candidate + fill + position envelope is one durable append. The two
            // following streams are query projections and are not needed to reconstruct admission.
            await _persistence.AppendAsync("shadow-candidates", admission, cancellationToken)
                .ConfigureAwait(false);
            if (admission.EntryFill is not null)
                await _persistence.AppendAsync("shadow-paper-fills", admission.EntryFill, cancellationToken)
                    .ConfigureAwait(false);
            if (admission.OpenedPosition is not null)
                await PersistPositionAsync(admission.OpenedPosition, "Opened", now, cancellationToken)
                    .ConfigureAwait(false);

            lock (_sync)
            {
                _seenCandidateIds.Add(candidate.CandidateId);
                _candidateCount++;
                _lastCandidateId = candidate.CandidateId;
                _lastPlaybookId = candidate.PlaybookId;
                _lastPolicyRevision = candidate.PolicyRevision;
                if (admission.Disposition == ShadowCandidateDisposition.Admitted)
                {
                    _positions.Add(opened!.PositionId, opened);
                    _admittedCount++;
                    _lastRejection = null;
                }
                else if (admission.Disposition == ShadowCandidateDisposition.Rejected)
                {
                    _rejectedCount++;
                    _lastRejection = admission.RejectionCode;
                }
            }
            return new ShadowAdmissionResult { Admission = admission, Position = opened };
        }
        finally
        {
            _serial.Release();
        }
    }

    public Task OnQuoteAsync(LiveQuoteSnapshot quote, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(quote);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            foreach (ShadowPaperPosition position in _positions.Values
                         .Where(position => position.Instrument == quote.Instrument).ToArray())
            {
                decimal mark = position.Side == AgentAction.Buy ? quote.Bid : quote.Ask;
                if (mark > 0m)
                    _positions[position.PositionId] = position with
                    {
                        LastMarkPrice = mark,
                        UpdatedAt = quote.BrokerTime
                    };
            }
        }
        return Task.CompletedTask;
    }

    public async Task OnCompletedCandleAsync(Candle candle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candle);
        if (!candle.IsComplete)
            return;
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ShadowPaperPosition[] positions;
            lock (_sync)
                positions = _positions.Values.Where(position => position.Instrument == candle.Instrument).ToArray();
            foreach (ShadowPaperPosition original in positions)
            {
                DateTimeOffset completedAt = candle.CloseTime ?? candle.OpenTime;
                ShadowPaperPosition updated = AccrueFinancing(UpdateExcursions(original, candle), completedAt);
                (bool stop, bool target) = Hits(updated, candle);
                if (!stop && !target)
                {
                    ShadowPaperPosition marked = updated with
                    {
                        LastMarkPrice = candle.Prices.Close,
                        UpdatedAt = completedAt
                    };
                    lock (_sync)
                        _positions[marked.PositionId] = marked;
                    continue;
                }
                ShadowExitReason reason = stop && target
                    ? ShadowExitReason.StopAndTargetSameCandle
                    : stop ? ShadowExitReason.StopLoss : ShadowExitReason.TakeProfit;
                string assumption = reason == ShadowExitReason.StopAndTargetSameCandle
                    ? "Stop and target touched in one completed candle; the stop is assumed first."
                    : "Completed-candle OHLC exit with gap handling and adverse slippage.";
                await CompleteAsync(updated, updated.RemainingQuantity, ExitPrice(updated, candle, reason),
                    completedAt, reason, assumption, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _serial.Release();
        }
    }

    public async Task OnAnalysisAsync(
        MarketAnalysisUpdate update,
        LiveQuoteSnapshot? quote,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (_policyRegistry is null || quote is null || quote.IsStale || !quote.IsTradeable ||
            quote.Instrument != update.Instrument)
        {
            return;
        }

        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ShadowPaperPosition[] positions;
            lock (_sync)
                positions = _positions.Values.Where(position => position.Instrument == update.Instrument).ToArray();

            foreach (ShadowPaperPosition original in positions)
            {
                LiveManagementPolicy? management = _policyRegistry.TryResolveManagementByRevision(
                    original.StrategyId,
                    original.Instrument,
                    original.PolicyBundleId,
                    original.PolicyRevision);
                if (management is null)
                {
                    await PersistManagementAsync(new ShadowManagementRequest
                    {
                        PositionId = original.PositionId,
                        Reason = "PinnedPolicyRevisionUnavailable",
                        RequestedAt = update.AvailableAt
                    }, false, "The entry-pinned policy revision is unavailable; no management action was guessed.",
                    null, original, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                PositionManagementOptions options = ResolveOptions(original.StrategyId, management.Policy);
                (TradeManagementEvaluationScope Scope, BarInterval? Interval) evaluation =
                    SelectEvaluation(update, options);
                if (evaluation.Interval is not BarInterval interval ||
                    !update.Analysis.TryGet(interval, out AnalysisSnapshot analysis))
                {
                    continue;
                }

                decimal executable = original.Side == AgentAction.Buy ? quote.Bid : quote.Ask;
                if (executable <= 0m || original.InitialRiskPerUnit <= 0m)
                    continue;
                decimal signedMove = original.Side == AgentAction.Buy
                    ? executable - original.EntryPrice
                    : original.EntryPrice - executable;
                decimal openR = signedMove / original.InitialRiskPerUnit;
                decimal maximumFavourable = Math.Max(original.MaximumFavourableExcursionR, openR);
                decimal maximumAdverse = Math.Max(original.MaximumAdverseExcursionR, -openR);
                int barsWithoutNewMfe = openR >= original.LastObservedMfeR + options.StagnationMinimumMfeAdvanceR
                    ? 0
                    : SaturatingIncrement(original.AnalysisBarsWithoutNewMfe);
                decimal lastObservedMfe = barsWithoutNewMfe == 0 ? openR : original.LastObservedMfeR;
                ShadowPaperPosition analyzed = original with
                {
                    LastMarkPrice = executable,
                    MaximumFavourableExcursionR = maximumFavourable,
                    MaximumAdverseExcursionR = maximumAdverse,
                    AnalysisBarsSinceLastReduction = SaturatingIncrement(original.AnalysisBarsSinceLastReduction),
                    AnalysisBarsSinceLastAmendment = SaturatingIncrement(original.AnalysisBarsSinceLastAmendment),
                    AnalysisBarsWithoutNewMfe = barsWithoutNewMfe,
                    LastObservedMfeR = lastObservedMfe,
                    UpdatedAt = update.AvailableAt
                };
                lock (_sync)
                    _positions[analyzed.PositionId] = analyzed;

                IStructureBasedTradeManager manager = CreateManager(management, options);
                TradeManagementRecommendation recommendation = manager.Evaluate(
                    ToManagedTrade(analyzed, executable, quote.Spread, update.AvailableAt),
                    analysis,
                    evaluation.Scope);
                (bool applied, string explanation) = await ApplyRecommendationAsync(
                    analyzed, recommendation, update.MarketSequence, update.AvailableAt, cancellationToken)
                    .ConfigureAwait(false);

                if (recommendation.Action != TradeManagementAction.Hold)
                {
                    await _persistence.AppendAsync("shadow-management", new ShadowTradeManagementEvaluation
                    {
                        PositionId = analyzed.PositionId,
                        MarketSequence = update.MarketSequence,
                        Scope = evaluation.Scope,
                        AnalysisInterval = interval,
                        EvaluatedAt = update.AvailableAt,
                        ConfigurationHash = management.Policy.ConfigurationHash,
                        Recommendation = recommendation,
                        Applied = applied,
                        Explanation = explanation
                    }, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _serial.Release();
        }
    }

    public async Task<ShadowManagementEvent> ApplyManagementAsync(
        ShadowManagementRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ShadowPaperPosition? position;
            lock (_sync)
                _positions.TryGetValue(request.PositionId, out position);
            if (position is null)
                return await PersistManagementAsync(request, false, "The paper position is not open.",
                    null, null, cancellationToken).ConfigureAwait(false);

            if (request.QuantityToClose is > 0m)
                return await ReduceAsync(position, request, cancellationToken).ConfigureAwait(false);

            return await AmendStopAsync(position, request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _serial.Release();
        }
    }

    private async Task<ShadowManagementEvent> ReduceAsync(
        ShadowPaperPosition position,
        ShadowManagementRequest request,
        CancellationToken cancellationToken)
    {
        decimal quantity = request.QuantityToClose!.Value;
        if (quantity > position.RemainingQuantity || position.LastMarkPrice is not > 0m)
            return await PersistManagementAsync(request, false,
                "The partial quantity or executable paper mark is invalid.", null, position,
                cancellationToken).ConfigureAwait(false);
        ShadowPaperFill fill = ExitFill(position, quantity, position.LastMarkPrice.Value,
            request.RequestedAt, ShadowPaperFillKind.PartialExit, request.Reason);
        decimal gross = Gross(position, quantity, fill.Price);
        ShadowPaperPosition reduced = position with
        {
            RemainingQuantity = position.RemainingQuantity - quantity,
            RealizedGrossProfitLoss = position.RealizedGrossProfitLoss + gross,
            RealizedCosts = position.RealizedCosts + fill.Commission,
            CompletedReductionStageIds = string.IsNullOrWhiteSpace(request.StageId)
                ? position.CompletedReductionStageIds
                : position.CompletedReductionStageIds
                    .Append(request.StageId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
            AnalysisBarsSinceLastReduction = 0,
            LastReductionSnapshotVersion = request.MarketSequence ?? request.RequestedAt.ToUnixTimeMilliseconds(),
            StagnationReductionCompleted = position.StagnationReductionCompleted ||
                request.ReductionReason == PositionReductionReason.Stagnation,
            StructuralDeteriorationReductionCount = position.StructuralDeteriorationReductionCount +
                (request.ReductionReason == PositionReductionReason.StructuralDeterioration ? 1 : 0),
            MomentumDecayReductionCount = position.MomentumDecayReductionCount +
                (request.ReductionReason == PositionReductionReason.MomentumDecay ? 1 : 0),
            VolatilityExhaustionReductionCount = position.VolatilityExhaustionReductionCount +
                (request.ReductionReason == PositionReductionReason.VolatilityExhaustion ? 1 : 0),
            RegimeDegradationReductionCount = position.RegimeDegradationReductionCount +
                (request.ReductionReason == PositionReductionReason.RegimeDegradation ? 1 : 0),
            RiskWindowReductionCompleted = position.RiskWindowReductionCompleted ||
                request.ReductionReason == PositionReductionReason.SessionRisk,
            ExecutionCostStressReductionCompleted = position.ExecutionCostStressReductionCompleted ||
                request.ReductionReason == PositionReductionReason.ExecutionCostStress,
            LastManagementAction = request.Reason,
            UpdatedAt = request.RequestedAt
        };
        await _persistence.AppendAsync("shadow-paper-fills", fill, cancellationToken).ConfigureAwait(false);
        if (reduced.RemainingQuantity == 0m)
            await CompleteReducedAsync(reduced, fill, request, cancellationToken).ConfigureAwait(false);
        else
        {
            await PersistPositionAsync(reduced, request.Reason, request.RequestedAt, cancellationToken)
                .ConfigureAwait(false);
            lock (_sync)
                _positions[reduced.PositionId] = reduced;
        }
        return await PersistManagementAsync(request, true, "The paper position was reduced.",
            fill, reduced, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ShadowManagementEvent> AmendStopAsync(
        ShadowPaperPosition position,
        ShadowManagementRequest request,
        CancellationToken cancellationToken)
    {
        if (request.NewStopPrice is not > 0m ||
            position.Side == AgentAction.Buy && request.NewStopPrice >= position.TargetPrice ||
            position.Side == AgentAction.Sell && request.NewStopPrice <= position.TargetPrice ||
            position.Side == AgentAction.Buy && request.NewStopPrice <= position.StopPrice ||
            position.Side == AgentAction.Sell && request.NewStopPrice >= position.StopPrice ||
            position.LastMarkPrice is > 0m && position.Side == AgentAction.Buy &&
                request.NewStopPrice >= position.LastMarkPrice ||
            position.LastMarkPrice is > 0m && position.Side == AgentAction.Sell &&
                request.NewStopPrice <= position.LastMarkPrice)
        {
            return await PersistManagementAsync(request, false, "The proposed paper stop is invalid.",
                null, position, cancellationToken).ConfigureAwait(false);
        }
        ShadowPaperPosition amended = position with
        {
            StopPrice = request.NewStopPrice.Value,
            AnalysisBarsSinceLastAmendment = 0,
            LastAmendmentSnapshotVersion = request.MarketSequence ?? request.RequestedAt.ToUnixTimeMilliseconds(),
            LastManagementAction = request.Reason,
            UpdatedAt = request.RequestedAt
        };
        await PersistPositionAsync(amended, request.Reason, request.RequestedAt, cancellationToken)
            .ConfigureAwait(false);
        lock (_sync)
            _positions[amended.PositionId] = amended;
        return await PersistManagementAsync(request, true, "The paper stop was amended.",
            null, amended, cancellationToken).ConfigureAwait(false);
    }

    private static IStructureBasedTradeManager CreateManager(
        LiveManagementPolicy management,
        PositionManagementOptions options) =>
        management.CalibrationOptions.Enabled && management.Calibration is not null
            ? new CalibratedStructureBasedTradeManager(
                options,
                management.Calibration,
                management.CalibrationOptions,
                management.Policy.RegimeManagement)
            : management.Policy.RegimeManagement.Enabled
                ? new RegimeAwareStructureBasedTradeManager(options, management.Policy.RegimeManagement)
                : new StructureBasedTradeManager(options);

    private static PositionManagementOptions ResolveOptions(
        string strategyId,
        LiveTradingPolicyBundle policy)
    {
        if (string.Equals(
                strategyId.Trim(),
                TradingAgentTypeIds.StructuralConfluence,
                StringComparison.OrdinalIgnoreCase))
        {
            return policy.StructuralManagement;
        }

        return strategyId.Contains("legacy", StringComparison.OrdinalIgnoreCase)
            ? policy.LegacyManagement
            : policy.ImprovedManagement;
    }

    private static (TradeManagementEvaluationScope Scope, BarInterval? Interval) SelectEvaluation(
        MarketAnalysisUpdate update,
        PositionManagementOptions options)
    {
        BarInterval? thesis = options.ThesisInterval;
        BarInterval? main = options.MainStructureInterval ?? options.ManagementInterval;
        BarInterval? fast = options.FastStructureInterval;
        if (thesis is BarInterval thesisInterval && update.ClosedIntervals.Contains(thesisInterval))
            return (TradeManagementEvaluationScope.Thesis, thesisInterval);
        if (main is BarInterval mainInterval && update.ClosedIntervals.Contains(mainInterval))
            return (TradeManagementEvaluationScope.MainStructure, mainInterval);
        if (fast is BarInterval fastInterval && update.ClosedIntervals.Contains(fastInterval))
            return (TradeManagementEvaluationScope.FastStructure, fastInterval);
        return (TradeManagementEvaluationScope.Mechanical, null);
    }

    private static ManagedTradeState ToManagedTrade(
        ShadowPaperPosition position,
        decimal executablePrice,
        decimal spread,
        DateTimeOffset evaluatedAt) => new()
    {
        StrategyId = position.StrategyId,
        InstrumentGroup = InstrumentGroupResolver.Resolve(position.Instrument),
        SetupType = position.PlaybookId ?? position.SetupId ?? "Unknown",
        EntrySession = "Unknown",
        EntryVolatilityBucket = "Live",
        EntryConfidence = position.EntryConfidence,
        Instrument = position.Instrument,
        Side = position.Side == AgentAction.Buy ? OrderSide.Buy : OrderSide.Sell,
        EntryPrice = position.EntryPrice,
        InitialStopPrice = position.InitialStopPrice,
        CurrentStopPrice = position.StopPrice,
        CurrentPrice = executablePrice,
        TakeProfitPrice = position.TargetPrice,
        InitialQuantity = position.InitialQuantity,
        CurrentQuantity = position.RemainingQuantity,
        MinimumQuantityIncrement = MinimumQuantityIncrement(position.Instrument),
        MaximumFavourableExcursionR = position.MaximumFavourableExcursionR,
        CompletedReductionStageIds = new HashSet<string>(
            position.CompletedReductionStageIds,
            StringComparer.OrdinalIgnoreCase),
        LastReductionSnapshotVersion = position.LastReductionSnapshotVersion,
        AnalysisBarsSinceLastReduction = position.AnalysisBarsSinceLastReduction,
        AnalysisBarsWithoutNewMfe = position.AnalysisBarsWithoutNewMfe,
        StagnationReductionCompleted = position.StagnationReductionCompleted,
        StructuralDeteriorationReductionCount = position.StructuralDeteriorationReductionCount,
        MomentumDecayReductionCount = position.MomentumDecayReductionCount,
        VolatilityExhaustionReductionCount = position.VolatilityExhaustionReductionCount,
        RegimeDegradationReductionCount = position.RegimeDegradationReductionCount,
        VolatilityExpansionSeenSinceEntry = position.VolatilityExpansionSeenSinceEntry,
        RiskWindowReductionCompleted = position.RiskWindowReductionCompleted,
        ExecutionCostStressReductionCompleted = position.ExecutionCostStressReductionCompleted,
        EvaluatedAt = evaluatedAt,
        MinimumPriceIncrement = MinimumPriceIncrement(position.Instrument),
        SpreadPrice = spread,
        LastAmendmentSnapshotVersion = position.LastAmendmentSnapshotVersion,
        AnalysisBarsSinceLastAmendment = position.AnalysisBarsSinceLastAmendment,
        EntryRegime = position.EntryRegime,
        EntrySupplyDemandZoneId = position.EntrySupplyDemandZoneId,
        TargetLiquidityPoolId = position.TargetLiquidityPoolId,
        EntrySupplyDemandManagementEnabled = position.EntrySupplyDemandManagementEnabled,
        EntryLiquidityManagementEnabled = position.EntryLiquidityManagementEnabled,
        StructuralManagementPolicyRevision = position.StructuralManagementPolicyRevision
    };

    private async Task<(bool Applied, string Explanation)> ApplyRecommendationAsync(
        ShadowPaperPosition position,
        TradeManagementRecommendation recommendation,
        long marketSequence,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        switch (recommendation.Action)
        {
            case TradeManagementAction.Hold:
                return (false, recommendation.Reason);

            case TradeManagementAction.Exit:
                ShadowExitReason exitReason = recommendation.ExitReason is
                    TradeManagementExitReason.EntrySupplyDemandZoneInvalidated or
                    TradeManagementExitReason.TargetLiquidityAcceptedBreak
                        ? ShadowExitReason.StructuralInvalidation
                        : ShadowExitReason.Management;
                await CompleteAsync(
                    position,
                    position.RemainingQuantity,
                    position.LastMarkPrice!.Value,
                    at,
                    exitReason,
                    recommendation.Reason,
                    cancellationToken).ConfigureAwait(false);
                return (true, recommendation.Reason);

            case TradeManagementAction.ReducePosition:
                return await ApplyReductionRecommendationAsync(
                    position, recommendation, marketSequence, at, cancellationToken).ConfigureAwait(false);

            case TradeManagementAction.MoveStop:
                ShadowManagementEvent amendment = await AmendStopAsync(position, new ShadowManagementRequest
                {
                    PositionId = position.PositionId,
                    NewStopPrice = recommendation.ProposedStopPrice,
                    MarketSequence = marketSequence,
                    Reason = recommendation.Reason,
                    RequestedAt = at
                }, cancellationToken).ConfigureAwait(false);
                return (amendment.Applied, amendment.Explanation);

            case TradeManagementAction.ReduceAndMoveStop:
                (bool reduced, string reductionExplanation) = await ApplyReductionRecommendationAsync(
                    position, recommendation, marketSequence, at, cancellationToken).ConfigureAwait(false);
                ShadowPaperPosition? remaining;
                lock (_sync)
                    _positions.TryGetValue(position.PositionId, out remaining);
                if (remaining is null)
                    return (reduced, reductionExplanation);
                ShadowManagementEvent combinedAmendment = await AmendStopAsync(remaining, new ShadowManagementRequest
                {
                    PositionId = remaining.PositionId,
                    NewStopPrice = recommendation.ProposedStopPrice,
                    MarketSequence = marketSequence,
                    Reason = recommendation.Reason,
                    RequestedAt = at
                }, cancellationToken).ConfigureAwait(false);
                return (reduced || combinedAmendment.Applied,
                    $"{reductionExplanation} {combinedAmendment.Explanation}".Trim());

            default:
                return (false, "The management recommendation action is unsupported.");
        }
    }

    private async Task<(bool Applied, string Explanation)> ApplyReductionRecommendationAsync(
        ShadowPaperPosition position,
        TradeManagementRecommendation recommendation,
        long marketSequence,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        if (recommendation.PositionReduction is not { } reduction)
            return (false, "The reduction recommendation did not contain a quantity.");
        decimal quantity = Math.Min(position.RemainingQuantity, reduction.QuantityToClose);
        if (quantity <= 0m)
            return (false, "The reduction recommendation quantity was not positive.");
        ShadowManagementEvent result = await ReduceAsync(position, new ShadowManagementRequest
        {
            PositionId = position.PositionId,
            QuantityToClose = quantity,
            StageId = reduction.StageId,
            ReductionReason = reduction.Reason,
            MarketSequence = marketSequence,
            Reason = recommendation.Reason,
            RequestedAt = at
        }, cancellationToken).ConfigureAwait(false);
        return (result.Applied, result.Explanation);
    }

    private static int SaturatingIncrement(int value) =>
        value == int.MaxValue ? int.MaxValue : value + 1;

    private static decimal MinimumPriceIncrement(InstrumentKey instrument) =>
        instrument.Value.EndsWith("/JPY", StringComparison.OrdinalIgnoreCase)
            ? 0.001m
            : 0.00001m;

    private static decimal MinimumQuantityIncrement(InstrumentKey instrument) =>
        instrument.Value.StartsWith("FX:", StringComparison.OrdinalIgnoreCase)
            ? 1m
            : 0.00000001m;

    private ShadowPaperPosition OpenPosition(
        LiveTradeCandidate candidate,
        LiveQuoteSnapshot quote,
        DateTimeOffset now,
        out ShadowPaperFill fill)
    {
        decimal executable = candidate.Action == AgentAction.Buy ? quote.Ask : quote.Bid;
        decimal entry = Slipped(executable, candidate.Action, _options.EntrySlippageBasisPoints);
        decimal quantity = candidate.SuggestedQuantity is > 0m
            ? candidate.SuggestedQuantity.Value
            : _options.DefaultQuantity;
        decimal commission = Cost(entry, quantity, _options.CommissionBasisPointsPerSide);
        string positionId = $"shadow-{candidate.CandidateId}";
        fill = new ShadowPaperFill
        {
            FillId = $"{positionId}-entry",
            CandidateId = candidate.CandidateId,
            PositionId = positionId,
            Kind = ShadowPaperFillKind.Entry,
            Side = candidate.Action,
            Quantity = quantity,
            Price = entry,
            SlippageCost = Math.Abs(entry - executable) * quantity,
            Commission = commission,
            FilledAt = now,
            Assumption = "Executable bid/ask plus adverse configured slippage."
        };
        return new ShadowPaperPosition
        {
            PositionId = positionId,
            CandidateId = candidate.CandidateId,
            DecisionId = candidate.DecisionId,
            SetupId = candidate.SetupId,
            PlaybookId = candidate.PlaybookId,
            PlaybookVersion = candidate.PlaybookVersion,
            StrategyId = candidate.StrategyId,
            AgentInstance = candidate.AgentInstance,
            Instrument = candidate.Instrument,
            Side = candidate.Action,
            EntryConfidence = candidate.RawConfidence,
            EntryRegime = candidate.EntryRegime,
            InitialQuantity = quantity,
            RemainingQuantity = quantity,
            EntryPrice = entry,
            InitialStopPrice = candidate.StopLossPrice!.Value,
            StopPrice = candidate.StopLossPrice.Value,
            TargetPrice = candidate.TakeProfitPrice!.Value,
            InitialRiskPerUnit = Math.Abs(entry - candidate.StopLossPrice.Value),
            EntryCommission = commission,
            AccruedFinancing = 0m,
            RealizedGrossProfitLoss = 0m,
            RealizedCosts = commission,
            MaximumFavourableExcursionR = 0m,
            MaximumAdverseExcursionR = 0m,
            LastMarkPrice = candidate.Action == AgentAction.Buy ? quote.Bid : quote.Ask,
            OpenedAt = now,
            LastFinancingAt = now,
            UpdatedAt = now,
            PolicyBundleId = candidate.PolicyBundleId,
            PolicyRevision = candidate.PolicyRevision,
            AnalysisProfileHash = candidate.AnalysisProfileHash,
            EntrySupplyDemandZoneId = candidate.EntrySupplyDemandZoneId,
            EntrySupplyDemandZoneLowerPrice = candidate.EntrySupplyDemandZoneLowerPrice,
            EntrySupplyDemandZoneUpperPrice = candidate.EntrySupplyDemandZoneUpperPrice,
            EntrySupplyDemandZoneState = candidate.EntrySupplyDemandZoneState,
            EntrySupplyDemandProfileHash = candidate.EntrySupplyDemandProfileHash,
            OriginatingLiquidityPoolId = candidate.OriginatingLiquidityPoolId,
            OriginatingLiquiditySweepId = candidate.OriginatingLiquiditySweepId,
            OriginatingLiquidityProfileHash = candidate.OriginatingLiquidityProfileHash,
            TargetLiquidityPoolId = candidate.TargetLiquidityPoolId,
            TargetLiquidityProfileHash = candidate.TargetLiquidityProfileHash,
            StructuralInvalidationReference = candidate.StructuralInvalidationReference,
            EntrySupplyDemandManagementEnabled = candidate.EntrySupplyDemandManagementEnabled,
            EntryLiquidityManagementEnabled = candidate.EntryLiquidityManagementEnabled,
            StructuralManagementPolicyRevision = candidate.StructuralManagementPolicyRevision
        };
    }

    private static bool TryValidate(
        LiveTradeCandidate candidate,
        LiveQuoteSnapshot? quote,
        out string? rejection)
    {
        rejection = null;
        if (candidate.Action is not (AgentAction.Buy or AgentAction.Sell))
            rejection = "NonEntryAction";
        else if (candidate.StopLossPrice is not > 0m || candidate.TakeProfitPrice is not > 0m)
            rejection = "MissingProtection";
        else if (quote is null || quote.Instrument != candidate.Instrument || quote.IsStale ||
                 !quote.IsTradeable || quote.Bid <= 0m || quote.Ask <= quote.Bid)
            rejection = "ExecutableQuoteUnavailable";
        else
        {
            decimal entry = candidate.Action == AgentAction.Buy ? quote.Ask : quote.Bid;
            if (candidate.Action == AgentAction.Buy &&
                (candidate.StopLossPrice >= entry || candidate.TakeProfitPrice <= entry) ||
                candidate.Action == AgentAction.Sell &&
                (candidate.StopLossPrice <= entry || candidate.TakeProfitPrice >= entry))
                rejection = "InvalidStopTargetGeometry";
        }
        return rejection is null;
    }

    private static ShadowCandidateAdmission Admission(
        LiveTradeCandidate candidate,
        StrategyActivationMode mode,
        ShadowCandidateDisposition disposition,
        DateTimeOffset now,
        LiveQuoteSnapshot? quote,
        string? rejection,
        string? explanation) => new()
    {
        Candidate = candidate,
        Mode = mode,
        Disposition = disposition,
        RecordedAt = now,
        RejectionCode = rejection,
        Explanation = explanation,
        ExecutableQuote = quote
    };

    private static ShadowPaperPosition UpdateExcursions(ShadowPaperPosition position, Candle candle)
    {
        decimal favourable = position.Side == AgentAction.Buy
            ? candle.Prices.High - position.EntryPrice
            : position.EntryPrice - candle.Prices.Low;
        decimal adverse = position.Side == AgentAction.Buy
            ? position.EntryPrice - candle.Prices.Low
            : candle.Prices.High - position.EntryPrice;
        return position with
        {
            MaximumFavourableExcursionR = Math.Max(position.MaximumFavourableExcursionR,
                favourable / position.InitialRiskPerUnit),
            MaximumAdverseExcursionR = Math.Max(position.MaximumAdverseExcursionR,
                adverse / position.InitialRiskPerUnit)
        };
    }

    private ShadowPaperPosition AccrueFinancing(ShadowPaperPosition position, DateTimeOffset at)
    {
        int days = (int)Math.Floor((at - position.LastFinancingAt).TotalDays);
        if (days <= 0 || _options.FinancingBasisPointsPerDay == 0m)
            return position;
        decimal financing = Cost(position.EntryPrice, position.RemainingQuantity,
            _options.FinancingBasisPointsPerDay) * days;
        return position with
        {
            AccruedFinancing = position.AccruedFinancing + financing,
            RealizedCosts = position.RealizedCosts + financing,
            LastFinancingAt = position.LastFinancingAt.AddDays(days)
        };
    }

    private static (bool Stop, bool Target) Hits(ShadowPaperPosition position, Candle candle) =>
        position.Side == AgentAction.Buy
            ? (candle.Prices.Low <= position.StopPrice, candle.Prices.High >= position.TargetPrice)
            : (candle.Prices.High >= position.StopPrice, candle.Prices.Low <= position.TargetPrice);

    private static decimal ExitPrice(ShadowPaperPosition position, Candle candle, ShadowExitReason reason)
    {
        bool stop = reason is ShadowExitReason.StopLoss or ShadowExitReason.StopAndTargetSameCandle;
        if (!stop)
            return position.TargetPrice;
        if (position.Side == AgentAction.Buy && candle.Prices.Open < position.StopPrice ||
            position.Side == AgentAction.Sell && candle.Prices.Open > position.StopPrice)
            return candle.Prices.Open;
        return position.StopPrice;
    }

    private async Task CompleteAsync(
        ShadowPaperPosition position,
        decimal quantity,
        decimal rawExit,
        DateTimeOffset at,
        ShadowExitReason reason,
        string assumption,
        CancellationToken cancellationToken)
    {
        ShadowPaperFill fill = ExitFill(position, quantity, rawExit, at, ShadowPaperFillKind.Exit, assumption);
        decimal gross = position.RealizedGrossProfitLoss + Gross(position, quantity, fill.Price);
        decimal costs = position.RealizedCosts + fill.Commission;
        ShadowPaperPosition closed = position with
        {
            RemainingQuantity = 0m,
            RealizedGrossProfitLoss = gross,
            RealizedCosts = costs,
            LastMarkPrice = fill.Price,
            LastManagementAction = reason.ToString(),
            UpdatedAt = at
        };
        ShadowTradeOutcome outcome = Outcome(closed, fill, reason, at, assumption);
        await _persistence.AppendAsync("shadow-paper-fills", fill, cancellationToken).ConfigureAwait(false);
        await PersistPositionAsync(closed, reason.ToString(), at, cancellationToken).ConfigureAwait(false);
        await _persistence.AppendAsync("shadow-outcomes", outcome, cancellationToken).ConfigureAwait(false);
        RecordOutcome(outcome);
    }

    private async Task CompleteReducedAsync(
        ShadowPaperPosition closed,
        ShadowPaperFill fill,
        ShadowManagementRequest request,
        CancellationToken cancellationToken)
    {
        ShadowTradeOutcome outcome = Outcome(closed, fill, ShadowExitReason.Management,
            request.RequestedAt, request.Reason);
        await PersistPositionAsync(closed, request.Reason, request.RequestedAt, cancellationToken)
            .ConfigureAwait(false);
        await _persistence.AppendAsync("shadow-outcomes", outcome, cancellationToken).ConfigureAwait(false);
        RecordOutcome(outcome);
    }

    private static ShadowTradeOutcome Outcome(
        ShadowPaperPosition closed,
        ShadowPaperFill fill,
        ShadowExitReason reason,
        DateTimeOffset at,
        string assumption)
    {
        decimal net = closed.RealizedGrossProfitLoss - closed.RealizedCosts;
        decimal risk = closed.InitialRiskPerUnit * closed.InitialQuantity;
        return new ShadowTradeOutcome
        {
            OutcomeId = $"{closed.PositionId}-outcome",
            State = ShadowOutcomeState.Completed,
            ExitReason = reason,
            Position = closed,
            ExitFill = fill,
            GrossProfitLoss = closed.RealizedGrossProfitLoss,
            NetProfitLoss = net,
            NetR = risk > 0m ? net / risk : 0m,
            TotalCosts = closed.RealizedCosts,
            CompletedAt = at,
            Assumption = assumption
        };
    }

    private void RecordOutcome(ShadowTradeOutcome outcome)
    {
        lock (_sync)
        {
            _positions.Remove(outcome.Position.PositionId);
            _completedCount++;
            _netProfitLoss += outcome.NetProfitLoss;
            _netR += outcome.NetR;
        }
    }

    private async Task<ShadowManagementEvent> PersistManagementAsync(
        ShadowManagementRequest request,
        bool applied,
        string explanation,
        ShadowPaperFill? fill,
        ShadowPaperPosition? position,
        CancellationToken cancellationToken)
    {
        var management = new ShadowManagementEvent
        {
            Request = request,
            Applied = applied,
            Explanation = explanation,
            Fill = fill,
            Position = position
        };
        await _persistence.AppendAsync("shadow-management", management, cancellationToken).ConfigureAwait(false);
        return management;
    }

    private ValueTask PersistPositionAsync(
        ShadowPaperPosition position,
        string reason,
        DateTimeOffset at,
        CancellationToken cancellationToken) =>
        _persistence.AppendAsync("shadow-paper-positions", new ShadowPaperPositionUpdate
        {
            Position = position,
            Reason = reason,
            RecordedAt = at
        }, cancellationToken);

    private ShadowPaperFill ExitFill(
        ShadowPaperPosition position,
        decimal quantity,
        decimal rawExit,
        DateTimeOffset at,
        ShadowPaperFillKind kind,
        string assumption)
    {
        AgentAction side = position.Side == AgentAction.Buy ? AgentAction.Sell : AgentAction.Buy;
        decimal price = Slipped(rawExit, side, _options.ExitSlippageBasisPoints);
        return new ShadowPaperFill
        {
            FillId = $"{position.PositionId}-{kind.ToString().ToLowerInvariant()}-{at.ToUnixTimeMilliseconds()}",
            CandidateId = position.CandidateId,
            PositionId = position.PositionId,
            Kind = kind,
            Side = side,
            Quantity = quantity,
            Price = price,
            SlippageCost = Math.Abs(price - rawExit) * quantity,
            Commission = Cost(price, quantity, _options.CommissionBasisPointsPerSide),
            FilledAt = at,
            Assumption = assumption
        };
    }

    private static decimal Gross(ShadowPaperPosition position, decimal quantity, decimal exitPrice) =>
        (position.Side == AgentAction.Buy
            ? exitPrice - position.EntryPrice
            : position.EntryPrice - exitPrice) * quantity;

    private static decimal Slipped(decimal price, AgentAction side, decimal basisPoints) =>
        side == AgentAction.Buy
            ? price * (1m + basisPoints / 10_000m)
            : price * (1m - basisPoints / 10_000m);

    private static decimal Cost(decimal price, decimal quantity, decimal basisPoints) =>
        Math.Abs(price * quantity) * basisPoints / 10_000m;

    private static decimal Unrealized(ShadowPaperPosition position) =>
        position.LastMarkPrice is > 0m
            ? position.RealizedGrossProfitLoss +
              Gross(position, position.RemainingQuantity, position.LastMarkPrice.Value) -
              position.RealizedCosts
            : 0m;

    private ShadowTradeOutcome RecoveryAmbiguity(ShadowPaperPosition position, int index)
    {
        decimal risk = position.InitialRiskPerUnit * position.InitialQuantity;
        decimal net = position.RealizedGrossProfitLoss - position.RealizedCosts;
        return new ShadowTradeOutcome
        {
            OutcomeId = $"{(string.IsNullOrWhiteSpace(position.PositionId) ? "shadow-unknown" : position.PositionId)}-recovery-ambiguous-{index}",
            State = ShadowOutcomeState.Ambiguous,
            ExitReason = ShadowExitReason.RecoveryAmbiguity,
            Position = position,
            GrossProfitLoss = position.RealizedGrossProfitLoss,
            NetProfitLoss = net,
            NetR = risk > 0m ? net / risk : 0m,
            TotalCosts = position.RealizedCosts,
            CompletedAt = _timeProvider.GetUtcNow(),
            Assumption = "The checkpoint contained an invalid open paper position; it was terminally classified as ambiguous and was not reopened."
        };
    }

    private ShadowPaperPosition[] OrderedPositions() => _positions.Values
        .OrderBy(position => position.OpenedAt)
        .ThenBy(position => position.PositionId, StringComparer.Ordinal)
        .ToArray();

    private static bool IsRecoverable(ShadowPaperPosition position) =>
        !string.IsNullOrWhiteSpace(position.PositionId) &&
        !string.IsNullOrWhiteSpace(position.CandidateId) &&
        position.Side is AgentAction.Buy or AgentAction.Sell &&
        position.RemainingQuantity > 0m && position.EntryPrice > 0m &&
        position.InitialRiskPerUnit > 0m && position.StopPrice > 0m && position.TargetPrice > 0m;
}
