using Brokers.Models;
using LiveTrading.Portfolio;
using ChartAnnotator.SupplyDemand;

namespace LiveTrading.Registry;

public enum LiveOrderState
{
    Candidate,
    PortfolioApproved,
    SubmissionPending,
    SubmissionUnknown,
    Accepted,
    PartiallyFilled,
    Filled,
    CancelPending,
    Cancelled,
    Rejected,
    Expired,
    Closed
}

public sealed record LiveOrderRecord
{
    public required string ClientOrderId { get; init; }
    public string? BrokerOrderId { get; init; }
    public string? BrokerTradeId { get; init; }
    public required string StrategyId { get; init; }
    public string AgentDeploymentId { get; init; } = string.Empty;
    public string AnalysisProfileHash { get; init; } = string.Empty;
    public required InstrumentKey Instrument { get; init; }
    public required string CandidateId { get; init; }
    public required string DecisionId { get; init; }
    public string? SetupId { get; init; }
    public required string ReservationId { get; init; }
    public string? RiskClusterId { get; init; }
    public required Guid PolicyBundleId { get; init; }
    public required int PolicyRevision { get; init; }
    public required string ConfigurationHash { get; init; }
    public required OrderSide Side { get; init; }
    public required decimal RequestedQuantity { get; init; }
    public decimal EntryConfidence { get; init; }
    public string EntryRegime { get; init; } = "Unknown";
    public string EntrySession { get; init; } = "Unknown";
    public string EntrySetupType { get; init; } = "Unknown";
    public string? EntryNeoWaveHypothesisId { get; init; }
    public decimal? EntryNeoWaveInvalidationPrice { get; init; }
    public Guid? EntrySupplyDemandZoneId { get; init; }
    public decimal? EntrySupplyDemandZoneLowerPrice { get; init; }
    public decimal? EntrySupplyDemandZoneUpperPrice { get; init; }
    public SupplyDemandZoneState? EntrySupplyDemandZoneState { get; init; }
    public string? EntrySupplyDemandProfileHash { get; init; }
    public Guid? TargetLiquidityPoolId { get; init; }
    public string? TargetLiquidityProfileHash { get; init; }
    public decimal? StructuralInvalidationReference { get; init; }
    public bool EntrySupplyDemandManagementEnabled { get; init; }
    public bool EntryLiquidityManagementEnabled { get; init; }
    public string? StructuralManagementPolicyRevision { get; init; }
    /// <summary>Regime-routed management profile the decision selected (see <c>Agent.Models.AgentDecision.RegimeManagementProfileId</c>), or "default" when regime routing is disabled - mirrors <c>Simulator.Models.SimulatedTradeRecord.EntryManagementProfileId</c>.</summary>
    public string EntryManagementProfileId { get; init; } = "default";
    public decimal? ProtectiveStopPrice { get; init; }
    public string? ProtectiveStopOrderId { get; init; }
    public decimal? TakeProfitPrice { get; init; }
    public decimal FilledQuantity { get; init; }
    public decimal? AverageFillPrice { get; init; }
    public required LiveOrderState State { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public string? LastReason { get; init; }
}

public sealed record LivePositionRecord
{
    public required string PositionId { get; init; }
    public string? BrokerTradeId { get; init; }
    public required string StrategyId { get; init; }
    public string AgentDeploymentId { get; init; } = string.Empty;
    public string AnalysisProfileHash { get; init; } = string.Empty;
    public required InstrumentKey Instrument { get; init; }
    public required string DecisionId { get; init; }
    public string? SetupId { get; init; }
    public required string ReservationId { get; init; }
    public required Guid PolicyBundleId { get; init; }
    public required int PolicyRevision { get; init; }
    public required string ConfigurationHash { get; init; }
    public required OrderSide Side { get; init; }
    public required decimal InitialQuantity { get; init; }
    public required decimal Quantity { get; init; }
    public decimal EntryConfidence { get; init; }
    public string EntryRegime { get; init; } = "Unknown";
    public string EntrySession { get; init; } = "Unknown";
    public string EntrySetupType { get; init; } = "Unknown";
    public string? EntryNeoWaveHypothesisId { get; init; }
    public decimal? EntryNeoWaveInvalidationPrice { get; init; }
    public Guid? EntrySupplyDemandZoneId { get; init; }
    public decimal? EntrySupplyDemandZoneLowerPrice { get; init; }
    public decimal? EntrySupplyDemandZoneUpperPrice { get; init; }
    public SupplyDemandZoneState? EntrySupplyDemandZoneState { get; init; }
    public string? EntrySupplyDemandProfileHash { get; init; }
    public Guid? TargetLiquidityPoolId { get; init; }
    public string? TargetLiquidityProfileHash { get; init; }
    public decimal? StructuralInvalidationReference { get; init; }
    public bool EntrySupplyDemandManagementEnabled { get; init; }
    public bool EntryLiquidityManagementEnabled { get; init; }
    public string? StructuralManagementPolicyRevision { get; init; }
    /// <summary>Regime-routed management profile this position was opened under - carried unchanged from <see cref="LiveOrderRecord.EntryManagementProfileId"/>, never re-derived after entry.</summary>
    public string EntryManagementProfileId { get; init; } = "default";
    public decimal? AveragePrice { get; init; }
    public decimal? InitialStopPrice { get; init; }
    public decimal? ProtectiveStopPrice { get; init; }
    public string? ProtectiveStopOrderId { get; init; }
    public decimal? TakeProfitPrice { get; init; }
    public decimal MaximumFavourableExcursionR { get; init; }
    public decimal MaximumAdverseExcursionR { get; init; }
    public string? LastManagementAction { get; init; }
    public string? LastManagementReason { get; init; }
    public required DateTimeOffset OpenedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record LiveRegistrySnapshot
{
    public required long Version { get; init; }
    public required IReadOnlyList<LiveOrderRecord> Orders { get; init; }
    public required IReadOnlyList<LivePositionRecord> Positions { get; init; }
    public IReadOnlyList<string> AppliedEventIds { get; init; } = [];
}

public sealed record RegistryApplyResult
{
    public required bool Applied { get; init; }
    public required bool Duplicate { get; init; }
    public required string ReasonCode { get; init; }
    public LiveOrderRecord? Order { get; init; }
    public LivePositionRecord? Position { get; init; }
}

public interface ILiveOrderPositionRegistry
{
    LiveRegistrySnapshot Snapshot { get; }
    LiveOrderRecord RegisterApproved(PortfolioApprovedDecision decision, string clientOrderId, DateTimeOffset now);
    LiveOrderRecord ApplySubmission(string clientOrderId, OrderSubmission submission, DateTimeOffset now);
    RegistryApplyResult Apply(OrderEvent brokerEvent);
    RegistryApplyResult ApplyPositionReduction(PositionReductionResult result, DateTimeOffset appliedAt);
    LivePositionRecord ApplyProtectiveStop(
        string positionId,
        ProtectiveStopAmendmentResult result,
        string reason,
        DateTimeOffset appliedAt);
    LivePositionRecord ApplyManagementObservation(
        string positionId,
        decimal maximumFavourableExcursionR,
        decimal maximumAdverseExcursionR,
        string action,
        string reason,
        DateTimeOffset observedAt);
    RegistryApplyResult ApplyReconciliation(
        IReadOnlyList<BrokerOrder> brokerOrders,
        IReadOnlyList<BrokerPosition> brokerPositions,
        DateTimeOffset reconciledAt);
    void Restore(LiveRegistrySnapshot snapshot);
}

/// <summary>
/// Thread-safe local order/position projection. Broker transaction IDs are idempotency keys; when
/// a broker omits one, a deterministic event fingerprint is used instead. Position-reducing fills
/// are never interpreted as new exposure.
/// </summary>
public sealed class LiveOrderPositionRegistry : ILiveOrderPositionRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<string, LiveOrderRecord> _ordersByClientId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _clientIdByBrokerOrderId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LivePositionRecord> _positions = new(StringComparer.Ordinal);
    private const int MaximumRememberedBrokerEvents = 20_000;
    private readonly HashSet<string> _appliedEvents = new(StringComparer.Ordinal);
    private readonly Queue<string> _appliedEventOrder = new();
    private long _version;

    public LiveOrderRecord RegisterApproved(PortfolioApprovedDecision decision, string clientOrderId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOrderId);
        decimal quantity = decision.Decision.SuggestedQuantity ?? throw new InvalidOperationException(
            "An approved decision requires a quantity.");
        var record = new LiveOrderRecord
        {
            ClientOrderId = clientOrderId,
            StrategyId = decision.Candidate.StrategyId,
            AgentDeploymentId = decision.Candidate.AgentInstance?.DeploymentId ?? string.Empty,
            AnalysisProfileHash = decision.Candidate.AnalysisProfileHash,
            Instrument = decision.Candidate.Instrument,
            CandidateId = decision.Candidate.CandidateId,
            DecisionId = decision.Candidate.DecisionId,
            SetupId = decision.Candidate.SetupId,
            ReservationId = decision.ReservationId,
            RiskClusterId = decision.Decision.RiskClusterId,
            PolicyBundleId = decision.PolicyBundleId,
            PolicyRevision = decision.PolicyRevision,
            ConfigurationHash = decision.ConfigurationHash,
            Side = decision.Decision.Action == Agent.Models.AgentAction.Buy ? OrderSide.Buy : OrderSide.Sell,
            RequestedQuantity = quantity,
            EntryConfidence = decision.Candidate.RawConfidence,
            EntryRegime = decision.Candidate.EntryRegime.ToString(),
            EntrySession = decision.Candidate.TradingCondition?.Session.ToString() ?? "Unknown",
            EntrySetupType = decision.Decision.PriceActionSetupType?.ToString() ??
                decision.Candidate.SetupId ?? "Unknown",
            EntryNeoWaveHypothesisId = decision.Decision.NeoWaveHypothesisId,
            EntryNeoWaveInvalidationPrice = decision.Decision.NeoWaveInvalidationPrice,
            EntrySupplyDemandZoneId = decision.Decision.EntrySupplyDemandZoneId,
            EntrySupplyDemandZoneLowerPrice = decision.Decision.EntrySupplyDemandZoneLowerPrice,
            EntrySupplyDemandZoneUpperPrice = decision.Decision.EntrySupplyDemandZoneUpperPrice,
            EntrySupplyDemandZoneState = decision.Decision.EntrySupplyDemandZoneState,
            EntrySupplyDemandProfileHash = decision.Decision.EntrySupplyDemandProfileHash,
            TargetLiquidityPoolId = decision.Decision.TargetLiquidityPoolId,
            TargetLiquidityProfileHash = decision.Decision.TargetLiquidityProfileHash,
            StructuralInvalidationReference = decision.Decision.StructuralInvalidationReference,
            EntrySupplyDemandManagementEnabled = decision.Decision.EntrySupplyDemandManagementEnabled,
            EntryLiquidityManagementEnabled = decision.Decision.EntryLiquidityManagementEnabled,
            StructuralManagementPolicyRevision = decision.Decision.StructuralManagementPolicyRevision,
            EntryManagementProfileId = decision.Decision.RegimeManagementProfileId ?? "default",
            ProtectiveStopPrice = decision.Decision.StopLossPrice,
            TakeProfitPrice = decision.Decision.TakeProfitPrice,
            State = LiveOrderState.PortfolioApproved,
            CreatedAt = now,
            UpdatedAt = now
        };
        lock (_sync)
        {
            if (_ordersByClientId.TryGetValue(clientOrderId, out LiveOrderRecord? existing))
                return existing;
            _ordersByClientId.Add(clientOrderId, record);
            _version++;
            return record;
        }
    }

    public LiveOrderRecord ApplySubmission(string clientOrderId, OrderSubmission submission, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOrderId);
        ArgumentNullException.ThrowIfNull(submission);
        lock (_sync)
        {
            if (!_ordersByClientId.TryGetValue(clientOrderId, out LiveOrderRecord? current))
                throw new KeyNotFoundException($"Client order '{clientOrderId}' is not registered.");
            LiveOrderState state = submission.Certainty switch
            {
                ExecutionCertainty.Unknown => LiveOrderState.SubmissionUnknown,
                ExecutionCertainty.Rejected => LiveOrderState.Rejected,
                _ => submission.Status switch
                {
                    SubmissionStatus.Filled => LiveOrderState.Filled,
                    SubmissionStatus.PartiallyFilled => LiveOrderState.PartiallyFilled,
                    SubmissionStatus.Accepted or SubmissionStatus.Pending => LiveOrderState.Accepted,
                    _ => LiveOrderState.SubmissionPending
                }
            };
            LiveOrderRecord updated = current with
            {
                BrokerOrderId = submission.BrokerOrderId ?? current.BrokerOrderId,
                State = state,
                UpdatedAt = now,
                LastReason = submission.RejectionReason
            };
            StoreOrderUnsafe(updated);
            _version++;
            return updated;
        }
    }

    public RegistryApplyResult Apply(OrderEvent brokerEvent)
    {
        ArgumentNullException.ThrowIfNull(brokerEvent);
        string eventKey = brokerEvent.BrokerTransactionId ?? EventFingerprint(brokerEvent);
        lock (_sync)
        {
            if (!TryMarkEventAppliedUnsafe(eventKey))
                return new RegistryApplyResult { Applied = false, Duplicate = true, ReasonCode = "DuplicateBrokerEvent" };

            LivePositionRecord? ownedPosition = FindPositionUnsafe(brokerEvent.BrokerTradeId);
            LiveOrderRecord? current = FindOrderUnsafe(brokerEvent, ownedPosition);
            if (current is null && ownedPosition is null)
            {
                _version++;
                return new RegistryApplyResult
                {
                    Applied = false,
                    Duplicate = false,
                    ReasonCode = "UnownedBrokerOrder"
                };
            }

            decimal fillQuantity = Math.Abs(brokerEvent.FillQuantity ?? 0m);
            bool reducing = brokerEvent.PositionEffect is OrderPositionEffect.Reduce or OrderPositionEffect.Close;
            LiveOrderRecord? updatedOrder = current;
            if (current is not null)
            {
                decimal cumulativeFilled = reducing
                    ? current.FilledQuantity
                    : current.FilledQuantity + fillQuantity;
                LiveOrderState next = reducing
                    ? current.State
                    : brokerEvent.Type switch
                    {
                        OrderEventType.Accepted or OrderEventType.Triggered or OrderEventType.Replaced => LiveOrderState.Accepted,
                        OrderEventType.PartiallyFilled => LiveOrderState.PartiallyFilled,
                        OrderEventType.Filled => LiveOrderState.Filled,
                        OrderEventType.Cancelled => LiveOrderState.Cancelled,
                        OrderEventType.Rejected => LiveOrderState.Rejected,
                        OrderEventType.Expired => LiveOrderState.Expired,
                        _ => current.State
                    };
                updatedOrder = current with
                {
                    BrokerOrderId = brokerEvent.BrokerOrderId,
                    BrokerTradeId = brokerEvent.BrokerTradeId ?? current.BrokerTradeId,
                    FilledQuantity = cumulativeFilled,
                    AverageFillPrice = reducing
                        ? current.AverageFillPrice
                        : WeightedAverage(current.AverageFillPrice, current.FilledQuantity, brokerEvent.FillPrice, fillQuantity),
                    State = next,
                    UpdatedAt = brokerEvent.Timestamp,
                    LastReason = brokerEvent.Message
                };
                StoreOrderUnsafe(updatedOrder);
            }

            LivePositionRecord? updatedPosition = ownedPosition;
            if ((brokerEvent.Type is OrderEventType.PartiallyFilled or OrderEventType.Filled) && fillQuantity > 0m)
            {
                if (reducing)
                {
                    updatedPosition = ApplyReductionUnsafe(
                        ownedPosition ?? throw new InvalidOperationException("A reducing fill must map to an owned position."),
                        fillQuantity,
                        brokerEvent.Timestamp,
                        brokerEvent.Message ?? brokerEvent.PositionEffect.ToString());
                }
                else if (updatedOrder is not null)
                {
                    string positionId = brokerEvent.BrokerTradeId ?? updatedOrder.BrokerTradeId ??
                        $"{updatedOrder.ClientOrderId}:position";
                    _positions.TryGetValue(positionId, out LivePositionRecord? existing);
                    decimal previousQuantity = existing?.Quantity ?? 0m;
                    decimal nextQuantity = previousQuantity + fillQuantity;
                    updatedPosition = new LivePositionRecord
                    {
                        PositionId = positionId,
                        BrokerTradeId = brokerEvent.BrokerTradeId ?? existing?.BrokerTradeId,
                        StrategyId = updatedOrder.StrategyId,
                        AgentDeploymentId = updatedOrder.AgentDeploymentId,
                        AnalysisProfileHash = updatedOrder.AnalysisProfileHash,
                        Instrument = updatedOrder.Instrument,
                        DecisionId = updatedOrder.DecisionId,
                        SetupId = updatedOrder.SetupId,
                        ReservationId = updatedOrder.ReservationId,
                        PolicyBundleId = updatedOrder.PolicyBundleId,
                        PolicyRevision = updatedOrder.PolicyRevision,
                        ConfigurationHash = updatedOrder.ConfigurationHash,
                        Side = updatedOrder.Side,
                        InitialQuantity = existing?.InitialQuantity ?? nextQuantity,
                        Quantity = nextQuantity,
                        EntryConfidence = existing?.EntryConfidence ?? updatedOrder.EntryConfidence,
                        EntryRegime = existing?.EntryRegime ?? updatedOrder.EntryRegime,
                        EntrySession = existing?.EntrySession ?? updatedOrder.EntrySession,
                        EntrySetupType = existing?.EntrySetupType ?? updatedOrder.EntrySetupType,
                        EntryNeoWaveHypothesisId = existing?.EntryNeoWaveHypothesisId ??
                            updatedOrder.EntryNeoWaveHypothesisId,
                        EntryNeoWaveInvalidationPrice = existing?.EntryNeoWaveInvalidationPrice ??
                            updatedOrder.EntryNeoWaveInvalidationPrice,
                        EntrySupplyDemandZoneId = existing?.EntrySupplyDemandZoneId ?? updatedOrder.EntrySupplyDemandZoneId,
                        EntrySupplyDemandZoneLowerPrice = existing?.EntrySupplyDemandZoneLowerPrice ??
                            updatedOrder.EntrySupplyDemandZoneLowerPrice,
                        EntrySupplyDemandZoneUpperPrice = existing?.EntrySupplyDemandZoneUpperPrice ??
                            updatedOrder.EntrySupplyDemandZoneUpperPrice,
                        EntrySupplyDemandZoneState = existing?.EntrySupplyDemandZoneState ??
                            updatedOrder.EntrySupplyDemandZoneState,
                        EntrySupplyDemandProfileHash = existing?.EntrySupplyDemandProfileHash ??
                            updatedOrder.EntrySupplyDemandProfileHash,
                        TargetLiquidityPoolId = existing?.TargetLiquidityPoolId ?? updatedOrder.TargetLiquidityPoolId,
                        TargetLiquidityProfileHash = existing?.TargetLiquidityProfileHash ??
                            updatedOrder.TargetLiquidityProfileHash,
                        StructuralInvalidationReference = existing?.StructuralInvalidationReference ??
                            updatedOrder.StructuralInvalidationReference,
                        EntrySupplyDemandManagementEnabled = existing?.EntrySupplyDemandManagementEnabled ??
                            updatedOrder.EntrySupplyDemandManagementEnabled,
                        EntryLiquidityManagementEnabled = existing?.EntryLiquidityManagementEnabled ??
                            updatedOrder.EntryLiquidityManagementEnabled,
                        StructuralManagementPolicyRevision = existing?.StructuralManagementPolicyRevision ??
                            updatedOrder.StructuralManagementPolicyRevision,
                        EntryManagementProfileId = existing?.EntryManagementProfileId ?? updatedOrder.EntryManagementProfileId,
                        AveragePrice = WeightedAverage(existing?.AveragePrice, previousQuantity, brokerEvent.FillPrice, fillQuantity),
                        InitialStopPrice = existing?.InitialStopPrice ?? updatedOrder.ProtectiveStopPrice,
                        ProtectiveStopPrice = existing?.ProtectiveStopPrice ?? updatedOrder.ProtectiveStopPrice,
                        ProtectiveStopOrderId = existing?.ProtectiveStopOrderId ?? updatedOrder.ProtectiveStopOrderId,
                        TakeProfitPrice = existing?.TakeProfitPrice ?? updatedOrder.TakeProfitPrice,
                        MaximumFavourableExcursionR = existing?.MaximumFavourableExcursionR ?? 0m,
                        MaximumAdverseExcursionR = existing?.MaximumAdverseExcursionR ?? 0m,
                        LastManagementAction = existing?.LastManagementAction,
                        LastManagementReason = existing?.LastManagementReason,
                        OpenedAt = existing?.OpenedAt ?? brokerEvent.Timestamp,
                        UpdatedAt = brokerEvent.Timestamp
                    };
                    _positions[positionId] = updatedPosition;
                }
            }

            _version++;
            return new RegistryApplyResult
            {
                Applied = true,
                Duplicate = false,
                ReasonCode = reducing ? "PositionReductionApplied" : "BrokerEventApplied",
                Order = updatedOrder,
                Position = updatedPosition
            };
        }
    }

    public RegistryApplyResult ApplyPositionReduction(PositionReductionResult result, DateTimeOffset appliedAt)
    {
        ArgumentNullException.ThrowIfNull(result);
        string eventKey = result.BrokerTransactionId ?? $"reduction|{result.ClientRequestId}|{result.PositionId}";
        lock (_sync)
        {
            if (!TryMarkEventAppliedUnsafe(eventKey))
                return new RegistryApplyResult { Applied = false, Duplicate = true, ReasonCode = "DuplicatePositionReduction" };
            LivePositionRecord? position = FindPositionUnsafe(result.PositionId);
            if (position is null || result.Certainty != ExecutionCertainty.Accepted || result.FilledQuantity is not > 0m)
            {
                return new RegistryApplyResult
                {
                    Applied = false,
                    Duplicate = false,
                    ReasonCode = position is null ? "UnownedPositionReduction" : "ReductionNotConfirmed"
                };
            }
            LivePositionRecord? updated = ApplyReductionUnsafe(
                position,
                result.FilledQuantity.Value,
                appliedAt,
                result.Reason ?? result.Status.ToString());
            _version++;
            return new RegistryApplyResult
            {
                Applied = true,
                Duplicate = false,
                ReasonCode = updated is null ? "PositionClosed" : "PositionReduced",
                Position = updated
            };
        }
    }

    public LivePositionRecord ApplyProtectiveStop(
        string positionId,
        ProtectiveStopAmendmentResult result,
        string reason,
        DateTimeOffset appliedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(positionId);
        ArgumentNullException.ThrowIfNull(result);
        lock (_sync)
        {
            LivePositionRecord position = FindPositionUnsafe(positionId) ?? throw new KeyNotFoundException(
                $"Owned position '{positionId}' was not found.");
            if (result.Certainty != ExecutionCertainty.Accepted || result.AcceptedStopPrice is not > 0m)
                return position;
            LivePositionRecord updated = position with
            {
                ProtectiveStopPrice = result.AcceptedStopPrice,
                ProtectiveStopOrderId = result.CurrentStopOrderId ?? position.ProtectiveStopOrderId,
                LastManagementAction = "MoveStop",
                LastManagementReason = reason,
                UpdatedAt = appliedAt
            };
            _positions[position.PositionId] = updated;
            LiveOrderRecord? order = _ordersByClientId.Values.FirstOrDefault(value =>
                value.DecisionId == position.DecisionId && value.StrategyId == position.StrategyId);
            if (order is not null)
            {
                StoreOrderUnsafe(order with
                {
                    ProtectiveStopPrice = updated.ProtectiveStopPrice,
                    ProtectiveStopOrderId = updated.ProtectiveStopOrderId,
                    UpdatedAt = appliedAt
                });
            }
            _version++;
            return updated;
        }
    }

    public LivePositionRecord ApplyManagementObservation(
        string positionId,
        decimal maximumFavourableExcursionR,
        decimal maximumAdverseExcursionR,
        string action,
        string reason,
        DateTimeOffset observedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(positionId);
        lock (_sync)
        {
            LivePositionRecord position = FindPositionUnsafe(positionId) ?? throw new KeyNotFoundException(
                $"Owned position '{positionId}' was not found.");
            LivePositionRecord updated = position with
            {
                MaximumFavourableExcursionR = Math.Max(position.MaximumFavourableExcursionR, maximumFavourableExcursionR),
                MaximumAdverseExcursionR = Math.Max(position.MaximumAdverseExcursionR, maximumAdverseExcursionR),
                LastManagementAction = action,
                LastManagementReason = reason,
                UpdatedAt = observedAt
            };
            _positions[position.PositionId] = updated;
            _version++;
            return updated;
        }
    }

    public RegistryApplyResult ApplyReconciliation(
        IReadOnlyList<BrokerOrder> brokerOrders,
        IReadOnlyList<BrokerPosition> brokerPositions,
        DateTimeOffset reconciledAt)
    {
        ArgumentNullException.ThrowIfNull(brokerOrders);
        ArgumentNullException.ThrowIfNull(brokerPositions);
        lock (_sync)
        {
            foreach (BrokerOrder brokerOrder in brokerOrders)
            {
                if (string.IsNullOrWhiteSpace(brokerOrder.ClientOrderId) ||
                    !_ordersByClientId.TryGetValue(brokerOrder.ClientOrderId, out LiveOrderRecord? local))
                    continue;
                LiveOrderState state = brokerOrder.NormalizedStatus switch
                {
                    OrderStatus.PartiallyFilled => LiveOrderState.PartiallyFilled,
                    OrderStatus.Filled => LiveOrderState.Filled,
                    OrderStatus.Cancelled => LiveOrderState.Cancelled,
                    OrderStatus.Rejected => LiveOrderState.Rejected,
                    OrderStatus.Expired => LiveOrderState.Expired,
                    _ => LiveOrderState.Accepted
                };
                StoreOrderUnsafe(local with
                {
                    BrokerOrderId = brokerOrder.BrokerOrderId,
                    FilledQuantity = brokerOrder.FilledQuantity ?? local.FilledQuantity,
                    State = state,
                    UpdatedAt = reconciledAt
                });
            }

            var rebuilt = new Dictionary<string, LivePositionRecord>(StringComparer.Ordinal);
            LivePositionRecord[] previous = _positions.Values.ToArray();
            foreach (BrokerPosition brokerPosition in brokerPositions)
            {
                LivePositionRecord? existing = previous.FirstOrDefault(local =>
                    local.PositionId == brokerPosition.PositionId || local.BrokerTradeId == brokerPosition.PositionId);
                LiveOrderRecord? ownershipOrder = !string.IsNullOrWhiteSpace(brokerPosition.ClientTradeId) &&
                    _ordersByClientId.TryGetValue(brokerPosition.ClientTradeId, out LiveOrderRecord? matchedOrder)
                        ? matchedOrder
                        : null;
                if (existing is null && ownershipOrder is null)
                {
                    LivePositionRecord[] ownership = previous.Where(local =>
                        local.Instrument == brokerPosition.Instrument && local.Side == brokerPosition.Side).ToArray();
                    if (ownership.Length == 1)
                        existing = ownership[0];
                }

                if (ownershipOrder is not null)
                {
                    StoreOrderUnsafe(ownershipOrder with
                    {
                        BrokerTradeId = brokerPosition.PositionId,
                        FilledQuantity = Math.Max(ownershipOrder.FilledQuantity, brokerPosition.InitialQuantity ?? brokerPosition.Quantity),
                        AverageFillPrice = brokerPosition.AveragePrice ?? ownershipOrder.AverageFillPrice,
                        ProtectiveStopPrice = brokerPosition.ProtectiveStopPrice ?? ownershipOrder.ProtectiveStopPrice,
                        ProtectiveStopOrderId = brokerPosition.ProtectiveStopOrderId ?? ownershipOrder.ProtectiveStopOrderId,
                        TakeProfitPrice = brokerPosition.TakeProfitPrice ?? ownershipOrder.TakeProfitPrice,
                        State = LiveOrderState.Filled,
                        UpdatedAt = reconciledAt,
                        LastReason = "ReconciledFromOpenTrade"
                    });
                }

                string id = existing?.PositionId ?? brokerPosition.PositionId;
                rebuilt[id] = new LivePositionRecord
                {
                    PositionId = id,
                    BrokerTradeId = brokerPosition.PositionId,
                    StrategyId = existing?.StrategyId ?? ownershipOrder?.StrategyId ??
                        brokerPosition.StrategyId ?? "unowned",
                    AgentDeploymentId = existing?.AgentDeploymentId ?? ownershipOrder?.AgentDeploymentId ?? string.Empty,
                    AnalysisProfileHash = existing?.AnalysisProfileHash ?? ownershipOrder?.AnalysisProfileHash ?? string.Empty,
                    Instrument = brokerPosition.Instrument,
                    DecisionId = existing?.DecisionId ?? ownershipOrder?.DecisionId ??
                        brokerPosition.DecisionId ?? "unknown",
                    SetupId = existing?.SetupId ?? ownershipOrder?.SetupId ?? brokerPosition.SetupId,
                    ReservationId = existing?.ReservationId ?? ownershipOrder?.ReservationId ??
                        brokerPosition.PortfolioReservationId ?? "unknown",
                    PolicyBundleId = existing?.PolicyBundleId ?? ownershipOrder?.PolicyBundleId ?? Guid.Empty,
                    PolicyRevision = existing?.PolicyRevision ?? ownershipOrder?.PolicyRevision ?? 0,
                    ConfigurationHash = existing?.ConfigurationHash ?? ownershipOrder?.ConfigurationHash ?? "unowned",
                    Side = brokerPosition.Side,
                    InitialQuantity = existing?.InitialQuantity ?? brokerPosition.InitialQuantity ??
                        ownershipOrder?.RequestedQuantity ?? brokerPosition.Quantity,
                    Quantity = brokerPosition.Quantity,
                    EntryConfidence = existing?.EntryConfidence ?? ownershipOrder?.EntryConfidence ?? 0m,
                    EntryRegime = existing?.EntryRegime ?? ownershipOrder?.EntryRegime ?? "Unknown",
                    EntrySession = existing?.EntrySession ?? ownershipOrder?.EntrySession ?? "Unknown",
                    EntrySetupType = existing?.EntrySetupType ?? ownershipOrder?.EntrySetupType ?? "Unknown",
                    EntryNeoWaveHypothesisId = existing?.EntryNeoWaveHypothesisId ??
                        ownershipOrder?.EntryNeoWaveHypothesisId,
                    EntryNeoWaveInvalidationPrice = existing?.EntryNeoWaveInvalidationPrice ??
                        ownershipOrder?.EntryNeoWaveInvalidationPrice,
                    EntrySupplyDemandZoneId = existing?.EntrySupplyDemandZoneId ?? ownershipOrder?.EntrySupplyDemandZoneId,
                    EntrySupplyDemandZoneLowerPrice = existing?.EntrySupplyDemandZoneLowerPrice ??
                        ownershipOrder?.EntrySupplyDemandZoneLowerPrice,
                    EntrySupplyDemandZoneUpperPrice = existing?.EntrySupplyDemandZoneUpperPrice ??
                        ownershipOrder?.EntrySupplyDemandZoneUpperPrice,
                    EntrySupplyDemandZoneState = existing?.EntrySupplyDemandZoneState ??
                        ownershipOrder?.EntrySupplyDemandZoneState,
                    EntrySupplyDemandProfileHash = existing?.EntrySupplyDemandProfileHash ??
                        ownershipOrder?.EntrySupplyDemandProfileHash,
                    TargetLiquidityPoolId = existing?.TargetLiquidityPoolId ?? ownershipOrder?.TargetLiquidityPoolId,
                    TargetLiquidityProfileHash = existing?.TargetLiquidityProfileHash ??
                        ownershipOrder?.TargetLiquidityProfileHash,
                    StructuralInvalidationReference = existing?.StructuralInvalidationReference ??
                        ownershipOrder?.StructuralInvalidationReference,
                    EntrySupplyDemandManagementEnabled = existing?.EntrySupplyDemandManagementEnabled ??
                        ownershipOrder?.EntrySupplyDemandManagementEnabled ?? false,
                    EntryLiquidityManagementEnabled = existing?.EntryLiquidityManagementEnabled ??
                        ownershipOrder?.EntryLiquidityManagementEnabled ?? false,
                    StructuralManagementPolicyRevision = existing?.StructuralManagementPolicyRevision ??
                        ownershipOrder?.StructuralManagementPolicyRevision,
                    EntryManagementProfileId = existing?.EntryManagementProfileId ?? ownershipOrder?.EntryManagementProfileId ?? "default",
                    AveragePrice = brokerPosition.AveragePrice,
                    InitialStopPrice = existing?.InitialStopPrice ?? ownershipOrder?.ProtectiveStopPrice ??
                        brokerPosition.ProtectiveStopPrice,
                    ProtectiveStopPrice = brokerPosition.ProtectiveStopPrice,
                    ProtectiveStopOrderId = brokerPosition.ProtectiveStopOrderId,
                    TakeProfitPrice = brokerPosition.TakeProfitPrice,
                    MaximumFavourableExcursionR = existing?.MaximumFavourableExcursionR ?? 0m,
                    MaximumAdverseExcursionR = existing?.MaximumAdverseExcursionR ?? 0m,
                    LastManagementAction = existing?.LastManagementAction,
                    LastManagementReason = existing?.LastManagementReason,
                    OpenedAt = existing?.OpenedAt ?? brokerPosition.OpenedAt ?? reconciledAt,
                    UpdatedAt = reconciledAt
                };
            }
            _positions.Clear();
            foreach ((string id, LivePositionRecord position) in rebuilt)
                _positions[id] = position;
            _version++;
            return new RegistryApplyResult
            {
                Applied = true,
                Duplicate = false,
                ReasonCode = "ReconciliationApplied"
            };
        }
    }

    public void Restore(LiveRegistrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_sync)
        {
            _ordersByClientId.Clear();
            _clientIdByBrokerOrderId.Clear();
            _positions.Clear();
            _appliedEvents.Clear();
            _appliedEventOrder.Clear();
            foreach (string eventId in snapshot.AppliedEventIds.TakeLast(MaximumRememberedBrokerEvents))
            {
                if (_appliedEvents.Add(eventId))
                    _appliedEventOrder.Enqueue(eventId);
            }
            foreach (LiveOrderRecord order in snapshot.Orders)
                StoreOrderUnsafe(order);
            foreach (LivePositionRecord position in snapshot.Positions)
                _positions[position.PositionId] = position;
            _version = snapshot.Version;
        }
    }

    public LiveRegistrySnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                return new LiveRegistrySnapshot
                {
                    Version = _version,
                    Orders = _ordersByClientId.Values.OrderByDescending(o => o.UpdatedAt).ToArray(),
                    Positions = _positions.Values
                        .OrderBy(p => p.Instrument.Value, StringComparer.Ordinal)
                        .ThenBy(p => p.StrategyId, StringComparer.Ordinal)
                        .ToArray(),
                    AppliedEventIds = _appliedEventOrder.ToArray()
                };
            }
        }
    }

    private LiveOrderRecord? FindOrderUnsafe(OrderEvent brokerEvent, LivePositionRecord? position)
    {
        string? clientId = brokerEvent.ClientOrderId;
        if (string.IsNullOrWhiteSpace(clientId))
            _clientIdByBrokerOrderId.TryGetValue(brokerEvent.BrokerOrderId, out clientId);
        if (!string.IsNullOrWhiteSpace(clientId) && _ordersByClientId.TryGetValue(clientId, out LiveOrderRecord? order))
            return order;
        return position is null
            ? null
            : _ordersByClientId.Values.FirstOrDefault(value =>
                value.DecisionId == position.DecisionId && value.StrategyId == position.StrategyId);
    }

    private LivePositionRecord? FindPositionUnsafe(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        if (_positions.TryGetValue(id, out LivePositionRecord? direct))
            return direct;
        return _positions.Values.FirstOrDefault(value => value.BrokerTradeId == id);
    }

    private LivePositionRecord? ApplyReductionUnsafe(
        LivePositionRecord position,
        decimal fillQuantity,
        DateTimeOffset timestamp,
        string reason)
    {
        decimal remaining = Math.Max(0m, position.Quantity - Math.Min(fillQuantity, position.Quantity));
        if (remaining <= 0m)
        {
            _positions.Remove(position.PositionId);
            return null;
        }
        LivePositionRecord updated = position with
        {
            Quantity = remaining,
            LastManagementAction = "ReducePosition",
            LastManagementReason = reason,
            UpdatedAt = timestamp
        };
        _positions[position.PositionId] = updated;
        return updated;
    }

    private void StoreOrderUnsafe(LiveOrderRecord order)
    {
        _ordersByClientId[order.ClientOrderId] = order;
        if (!string.IsNullOrWhiteSpace(order.BrokerOrderId))
            _clientIdByBrokerOrderId[order.BrokerOrderId] = order.ClientOrderId;
    }

    private static decimal? WeightedAverage(
        decimal? previousPrice,
        decimal previousQuantity,
        decimal? fillPrice,
        decimal fillQuantity)
    {
        if (fillPrice is not decimal currentPrice)
            return previousPrice;
        if (previousPrice is not decimal priorPrice || previousQuantity <= 0m)
            return currentPrice;
        decimal total = previousQuantity + fillQuantity;
        return total > 0m
            ? (priorPrice * previousQuantity + currentPrice * fillQuantity) / total
            : currentPrice;
    }

    private bool TryMarkEventAppliedUnsafe(string eventKey)
    {
        if (!_appliedEvents.Add(eventKey))
            return false;
        _appliedEventOrder.Enqueue(eventKey);
        while (_appliedEventOrder.Count > MaximumRememberedBrokerEvents)
        {
            string expired = _appliedEventOrder.Dequeue();
            _appliedEvents.Remove(expired);
        }
        return true;
    }

    private static string EventFingerprint(OrderEvent value) => string.Join("|",
        value.BrokerOrderId,
        value.BrokerTradeId,
        value.ClientOrderId,
        value.Type,
        value.PositionEffect,
        value.Timestamp.ToUnixTimeMilliseconds(),
        value.FillPrice,
        value.FillQuantity,
        value.RemainingQuantity);
}
