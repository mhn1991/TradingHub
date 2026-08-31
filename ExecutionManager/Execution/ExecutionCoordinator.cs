using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TradingJournal;
using Agent.Models;
using RiskManager;
using RiskManager.Safety;
using Brokers.Abstractions;
using Brokers.Models;
using Brokers.Safety;

namespace ExecutionManager;

public sealed class ExecutionCoordinator : IExecutionCoordinator
{
    private readonly ExecutionOptions _options;
    private readonly IPreTradeRiskManager _riskManager;
    private readonly IPositionSizer _positionSizer;
    private readonly IRiskBudgetPolicy _riskBudgetPolicy;
    private readonly IBrokerExecutionSafety _brokerSafety;
    private readonly ITradingSafetyController? _safety;
    private readonly ITradeJournal _journal;
    private readonly ConcurrentDictionary<string, Lazy<Task<OrderSubmission?>>> _inFlight = [];
    private readonly ConcurrentDictionary<string, AmendmentOperation> _amendments = [];

    public ExecutionCoordinator(
        ExecutionOptions? options = null,
        IPreTradeRiskManager? riskManager = null,
        IBrokerExecutionSafety? brokerSafety = null,
        ITradingSafetyController? safety = null,
        ITradeJournal? journal = null,
        IPositionSizer? positionSizer = null,
        IRiskBudgetPolicy? riskBudgetPolicy = null)
    {
        _options = options ?? new ExecutionOptions();
        ValidateOptions(_options);
        _riskManager = riskManager ?? new PreTradeRiskManager();
        _positionSizer = positionSizer ?? new PositionSizer();
        _riskBudgetPolicy = riskBudgetPolicy ?? new RiskBudgetPolicy();
        _brokerSafety = brokerSafety ?? new BrokerExecutionSafety();
        _safety = safety;
        _journal = journal ?? NullTradeJournal.Instance;
    }

    public Task<OrderSubmission?> ProcessAsync(
        AgentDecision decision,
        ITradingBrokerClient broker,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(broker);

        if (decision.Action == AgentAction.Observe)
        {
            AppendJournal(
                TradeJournalEventType.SignalEvaluated,
                decision,
                null,
                decision.Reason);
            return Task.FromResult<OrderSubmission?>(null);
        }

        if (decision.Action == AgentAction.Cancel)
        {
            if (decision.Instrument.IsEmpty)
                throw new InvalidOperationException("A cancel decision requires an instrument.");
            if (string.IsNullOrWhiteSpace(decision.BrokerOrderId))
                throw new InvalidOperationException("A cancel decision requires a broker order ID.");

            AppendJournal(
                TradeJournalEventType.SignalEvaluated,
                decision,
                decision.ClientOrderId,
                decision.Reason);
            return CancelOrderAsync(decision.BrokerOrderId, broker, cancellationToken);
        }

        ValidateDecision(decision);
        string clientOrderId = string.IsNullOrWhiteSpace(decision.ClientOrderId)
            ? CreateClientOrderId(decision, _options.ClientOrderIdPrefix)
            : decision.ClientOrderId;
        // Safety locks prevent new risk, but must never block a position-reducing exit.
        string? immediateRejection = decision.Action is AgentAction.Buy or AgentAction.Sell
            ? GetSafetyRejection()
            : null;
        if (immediateRejection is not null)
        {
            AppendJournal(
                TradeJournalEventType.SignalRejected,
                decision,
                clientOrderId,
                immediateRejection);
            return Task.FromResult<OrderSubmission?>(
                RejectWithoutSending(clientOrderId, immediateRejection));
        }

        var candidate = new Lazy<Task<OrderSubmission?>>(
            () => ExecuteCoreAsync(decision, clientOrderId, broker, cancellationToken),
            LazyThreadSafetyMode.ExecutionAndPublication);
        Lazy<Task<OrderSubmission?>> operation = _inFlight.GetOrAdd(clientOrderId, candidate);
        return AwaitAndReleaseAsync(clientOrderId, operation, cancellationToken);
    }

    private static async Task<OrderSubmission?> CancelOrderAsync(
        string brokerOrderId,
        ITradingBrokerClient broker,
        CancellationToken cancellationToken)
    {
        await broker.Orders.CancelOrderAsync(brokerOrderId, cancellationToken).ConfigureAwait(false);
        return null;
    }

    public Task<ProtectiveStopAmendmentResult> AmendProtectiveStopAsync(
        ProtectiveStopAmendmentCommand command,
        ITradingBrokerClient broker,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(broker);
        ValidateAmendmentCommand(command);

        decimal normalized = NormalizeRiskReducingPrice(
            command.ProposedStopPrice,
            command.MinimumPriceIncrement,
            command.PositionSide);
        string clientAmendmentId = string.IsNullOrWhiteSpace(command.ClientAmendmentId)
            ? CreateAmendmentId(command, normalized)
            : command.ClientAmendmentId;
        string fingerprint = CreateAmendmentFingerprint(command, normalized);

        var candidate = new AmendmentOperation(
            fingerprint,
            new Lazy<Task<ProtectiveStopAmendmentResult>>(
                () => AmendProtectiveStopCoreAsync(
                    command,
                    normalized,
                    clientAmendmentId,
                    broker,
                    cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));
        AmendmentOperation operation = _amendments.GetOrAdd(clientAmendmentId, candidate);
        if (!string.Equals(operation.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return Task.FromResult(RejectAmendment(
                command,
                clientAmendmentId,
                normalized,
                "The amendment ID was already used with a different payload."));
        }

        return AwaitAndReleaseAmendmentAsync(clientAmendmentId, operation, cancellationToken);
    }

    /// <summary>
    /// Mirrors <see cref="AwaitAndReleaseAsync"/>. Without this release the amendment map grew
    /// without bound: the fingerprint includes the request and effective sequences, so every single
    /// trailing-stop amendment minted a permanent entry holding a completed task. Payload-mismatch
    /// detection is therefore in-flight scoped, exactly like the order path; a stale replay is still
    /// caught by the broker-side stop-order state check in AmendProtectiveStopCoreAsync.
    /// </summary>
    private async Task<ProtectiveStopAmendmentResult> AwaitAndReleaseAmendmentAsync(
        string clientAmendmentId,
        AmendmentOperation operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await operation.Task.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (operation.Task.IsValueCreated && operation.Task.Value.IsCompleted)
            {
                _amendments.TryRemove(clientAmendmentId, out _);
            }
        }
    }

    private async Task<OrderSubmission?> AwaitAndReleaseAsync(
        string clientOrderId,
        Lazy<Task<OrderSubmission?>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await operation.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (operation.IsValueCreated && operation.Value.IsCompleted)
            {
                _inFlight.TryRemove(clientOrderId, out _);
            }
        }
    }

    private async Task<OrderSubmission?> ExecuteCoreAsync(
        AgentDecision decision,
        string clientOrderId,
        ITradingBrokerClient broker,
        CancellationToken cancellationToken)
    {
        try
        {
            decimal quantity = decision.SuggestedQuantity!.Value;
            Task<IReadOnlyList<AccountSnapshot>> accountsTask =
                broker.Accounts.GetAccountsAsync(cancellationToken);
            Task<IReadOnlyList<BrokerPosition>> positionsTask =
                broker.Positions.GetOpenPositionsAsync(cancellationToken);
            Task<IReadOnlyList<BrokerOrder>> ordersTask =
                broker.Orders.GetOpenOrdersAsync(decision.Instrument, cancellationToken);
            await Task.WhenAll(accountsTask, positionsTask, ordersTask).ConfigureAwait(false);

            IReadOnlyList<AccountSnapshot> accounts = await accountsTask.ConfigureAwait(false);
            IReadOnlyList<BrokerPosition> positions = await positionsTask.ConfigureAwait(false);
            IReadOnlyList<BrokerOrder> openOrders = await ordersTask.ConfigureAwait(false);

            decimal? estimatedLossAtStop = null;
            if (decision.Action == AgentAction.Close)
            {
                (_, decimal closableQuantity) = ResolveClosePosition(positions, decision.Instrument);
                quantity = Math.Min(quantity, closableQuantity);
                // Never cancel protective orders before the close fill is confirmed. Doing so
                // creates an unprotected interval if the close is delayed, rejected, or its
                // result is uncertain. The broker/simulator must reconcile or cancel attached
                // protectives atomically after the position quantity actually changes.
                // This rule applies to both partial and full closes.
            }
            else
            {
                BrokerExecutionSafetyAssessment brokerAssessment = _brokerSafety.Evaluate(
                    new BrokerExecutionSafetyContext
                    {
                        Instrument = decision.Instrument,
                        Accounts = accounts,
                        OpenOrders = openOrders
                    });
                if (!brokerAssessment.Approved)
                {
                    AppendJournal(
                        TradeJournalEventType.SignalRejected,
                        decision,
                        clientOrderId,
                        brokerAssessment.Summary);
                    return RejectWithoutSending(clientOrderId, brokerAssessment.Summary);
                }

                decimal quoteToAccountRate = ResolveQuoteToAccountRate(
                    decision,
                    broker,
                    accounts);
                InstrumentRiskSpec instrumentSpec = InstrumentRiskSpec.ForInstrument(decision.Instrument);
                AccountSnapshot? sizingAccount = accounts.FirstOrDefault(item => item.Balance is > 0m);
                decimal accountEquity = (sizingAccount?.Balance ?? 0m) +
                    (sizingAccount?.UnrealizedProfitLoss ?? 0m);
                if (decision.QuantityIsPortfolioApproved)
                {
                    if (string.IsNullOrWhiteSpace(decision.PortfolioReservationId) ||
                        decision.PortfolioAllocatedQuantity is not > 0m ||
                        decision.SuggestedQuantity != decision.PortfolioAllocatedQuantity)
                    {
                        return RejectWithoutSending(
                            clientOrderId,
                            "An authoritative portfolio quantity requires a matching reservation and allocated quantity.");
                    }

                    quantity = decision.PortfolioAllocatedQuantity.Value;
                    AppendJournal(
                        TradeJournalEventType.SignalEvaluated,
                        decision,
                        clientOrderId,
                        $"Using centrally reserved portfolio quantity {quantity:F8}; execution will not size it again.");
                }
                else
                {
                    RiskBudgetDecision riskBudget = _riskBudgetPolicy.Evaluate(new RiskBudgetContext
                    {
                        AccountEquity = accountEquity,
                        DrawdownPercent = _safety?.Snapshot.EquityProtection.DrawdownPercent ?? 0m,
                        VolatilityPercentile = decision.AtrPercentile,
                        RegimeMultiplier = decision.RegimeRiskMultiplier ?? 1m,
                        LiquidityMultiplier = decision.TradingConditionRiskMultiplier ?? 1m,
                        CorrelationMultiplier = decision.CorrelationRiskMultiplier ?? 1m,
                        StrategyAllocationMultiplier = decision.StrategyAllocationRiskMultiplier ?? 1m,
                        EquityProtectionMultiplier = decision.EquityProtectionRiskMultiplier ??
                            _safety?.Snapshot.EquityProtection.CurrentRiskMultiplier ?? 1m,
                        CalibrationMultiplier = decision.SetupCalibrationRiskMultiplier ?? 1m,
                        MetaLabelMultiplier = decision.MetaLabelRiskMultiplier ?? 1m,
                        NeoWaveMultiplier = decision.NeoWaveRiskMultiplier ?? 1m,
                        StructuralEvidenceMultiplier = decision.StructuralEvidenceRiskMultiplier ?? 1m
                    });
                    if (riskBudget.CombinedMultiplier <= 0m)
                    {
                        AppendJournal(
                            TradeJournalEventType.SignalRejected,
                            decision,
                            clientOrderId,
                            $"[{riskBudget.ReasonCode}] {riskBudget.Explanation}");
                        return RejectWithoutSending(clientOrderId, riskBudget.Explanation);
                    }
                    AppendJournal(
                        TradeJournalEventType.RiskBudgetAdjusted,
                        decision,
                        clientOrderId,
                        $"[{riskBudget.ReasonCode}] {riskBudget.Explanation}",
                        riskBudget.CombinedMultiplier);
                    PositionSizingResult sizing = _positionSizer.Calculate(
                        new PositionSizingContext
                        {
                            Decision = decision,
                            RequestedQuantity = quantity,
                            Accounts = accounts,
                            Positions = positions,
                            QuoteToAccountCurrencyRate = quoteToAccountRate,
                            InstrumentSpec = instrumentSpec,
                            RiskBudgetMultiplier = riskBudget.CombinedMultiplier,
                            RiskBudgetDecision = riskBudget
                        });
                    if (!sizing.Approved)
                    {
                        // Fail closed. Falling back to the strategy's requested quantity after a
                        // missing conversion, invalid stop, margin cap, or below-minimum result
                        // bypasses the very risk budget the selected sizing mode is meant to own.
                        // Fixed-quantity behavior remains available explicitly through
                        // PositionSizingMode.FixedQuantity.
                        AppendJournal(
                            TradeJournalEventType.SignalRejected,
                            decision,
                            clientOrderId,
                            $"Position sizing rejected the setup [{sizing.ReasonCode}]: {sizing.Reason}");
                        return RejectWithoutSending(clientOrderId, sizing.Reason);
                    }

                    quantity = decision.PortfolioAllocatedQuantity is decimal allocation
                        ? Math.Min(sizing.Quantity, allocation)
                        : sizing.Quantity;
                    if (quantity <= 0m)
                        return RejectWithoutSending(clientOrderId, "The portfolio allocation did not leave an executable quantity.");
                    AppendJournal(
                        TradeJournalEventType.SignalEvaluated,
                        decision,
                        clientOrderId,
                        $"Position sizing [{sizing.ReasonCode}]: {sizing.Reason}",
                        sizing.EstimatedLossAtStop);
                }

                var riskContext = new PreTradeRiskContext
                {
                    Decision = decision,
                    Quantity = quantity,
                    Accounts = accounts,
                    Positions = positions,
                    // Pass the unresolved rate through instead of substituting 1.0. Fabricating a
                    // rate made PreTradeRiskManager evaluate its monetary caps against a made-up
                    // conversion (the RSK-01 anti-pattern) and defeated that class's own
                    // missing-rate guard, which only engages on a non-positive rate. The risk
                    // manager now rejects explicitly when a monetary cap is configured and no
                    // conversion is available, and stays silent when none is configured.
                    QuoteToAccountCurrencyRate = quoteToAccountRate,
                    InstrumentSpec = instrumentSpec,
                    Safety = _safety?.Snapshot
                };
                RiskAssessment assessment = _riskManager.Evaluate(riskContext);
                if (!assessment.Approved)
                {
                    AppendJournal(
                        TradeJournalEventType.SignalRejected,
                        decision,
                        clientOrderId,
                        assessment.Summary,
                        assessment.EstimatedLossAtStop);
                    return RejectWithoutSending(clientOrderId, assessment.Summary);
                }
                estimatedLossAtStop = assessment.EstimatedLossAtStop;
            }

            PlaceOrderRequest request = CreateRequest(decision, quantity, clientOrderId, positions);
            AppendJournal(
                TradeJournalEventType.OrderSubmitted,
                decision,
                clientOrderId,
                $"Submitting {request.Side} {request.Quantity.Value} {request.Quantity.Unit}.",
                estimatedLossAtStop);
            OrderSubmission submission = await broker.Orders
                .PlaceOrderAsync(request, cancellationToken)
                .ConfigureAwait(false);
            AppendJournal(
                submission.Status == SubmissionStatus.Rejected
                    ? TradeJournalEventType.OrderRejected
                    : TradeJournalEventType.OrderAccepted,
                decision,
                submission.ClientOrderId,
                submission.RejectionReason ??
                $"Broker returned {submission.Status} with certainty {submission.Certainty}.",
                estimatedLossAtStop);
            return submission;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            AppendJournal(
                TradeJournalEventType.Error,
                decision,
                clientOrderId,
                $"Execution failed: {exception.Message}");
            if (_options.TripSafetyOnExecutionFailure)
            {
                _safety?.Trip(
                    SafetyTripReason.ExecutionFailure,
                    $"Order execution failed for {decision.Instrument}: {exception.Message}",
                    decision.CreatedAt);
            }
            throw;
        }
    }

    private async Task<ProtectiveStopAmendmentResult> AmendProtectiveStopCoreAsync(
        ProtectiveStopAmendmentCommand command,
        decimal normalizedStop,
        string clientAmendmentId,
        ITradingBrokerClient broker,
        CancellationToken cancellationToken)
    {
        string? priceError = ValidateRiskReduction(command, normalizedStop);
        if (priceError is not null)
        {
            ProtectiveStopAmendmentResult rejected = RejectAmendment(
                command,
                clientAmendmentId,
                normalizedStop,
                priceError);
            AppendAmendmentJournal(TradeJournalEventType.StopAmendmentRejected, command, rejected, priceError);
            return rejected;
        }

        if (broker is not IProtectiveOrderBrokerClient protectiveBroker ||
            (!protectiveBroker.TradingCapabilities.SupportsNativeStopAmendment &&
             !protectiveBroker.TradingCapabilities.SupportsAtomicOrderReplacement))
        {
            var unsupported = new ProtectiveStopAmendmentResult
            {
                Status = ProtectiveStopAmendmentStatus.Unsupported,
                ClientAmendmentId = clientAmendmentId,
                PreviousStopOrderId = command.ExistingStopOrderId,
                CurrentStopOrderId = command.ExistingStopOrderId,
                RequestedStopPrice = normalizedStop,
                RejectionReason = "The broker cannot guarantee a safe atomic protective-stop amendment.",
                Certainty = ExecutionCertainty.NotSent
            };
            AppendAmendmentJournal(
                TradeJournalEventType.StopAmendmentUnsupported,
                command,
                unsupported,
                unsupported.RejectionReason!);
            return unsupported;
        }

        Task<IReadOnlyList<BrokerPosition>> positionsTask =
            broker.Positions.GetOpenPositionsAsync(cancellationToken);
        Task<IReadOnlyList<BrokerOrder>> ordersTask =
            broker.Orders.GetOpenOrdersAsync(command.Instrument, cancellationToken);
        await Task.WhenAll(positionsTask, ordersTask).ConfigureAwait(false);
        BrokerPosition? position = (await positionsTask.ConfigureAwait(false))
            .FirstOrDefault(item => string.Equals(item.PositionId, command.PositionId, StringComparison.Ordinal));
        if (position is null || position.Instrument != command.Instrument ||
            position.Side != command.PositionSide || position.Quantity != command.PositionQuantity)
        {
            ProtectiveStopAmendmentResult rejected = RejectAmendment(
                command,
                clientAmendmentId,
                normalizedStop,
                "The open broker position does not match the amendment command.");
            AppendAmendmentJournal(
                TradeJournalEventType.StopAmendmentRejected,
                command,
                rejected,
                rejected.RejectionReason!);
            return rejected;
        }

        BrokerOrder? stopOrder = (await ordersTask.ConfigureAwait(false))
            .FirstOrDefault(order => string.Equals(
                order.BrokerOrderId,
                command.ExistingStopOrderId,
                StringComparison.Ordinal));
        if (stopOrder is null || stopOrder.Instrument != command.Instrument ||
            stopOrder.NormalizedStatus is not (OrderStatus.Open or OrderStatus.Pending or OrderStatus.PartiallyFilled) ||
            !string.Equals(stopOrder.Type, StandardOrderType.Stop.ToString(), StringComparison.OrdinalIgnoreCase) ||
            stopOrder.Quantity != command.PositionQuantity ||
            stopOrder.Side == command.PositionSide || stopOrder.Price != command.CurrentStopPrice)
        {
            ProtectiveStopAmendmentResult rejected = RejectAmendment(
                command,
                clientAmendmentId,
                normalizedStop,
                "The existing protective-stop order ID or state is stale.");
            AppendAmendmentJournal(
                TradeJournalEventType.StopAmendmentRejected,
                command,
                rejected,
                rejected.RejectionReason!);
            return rejected;
        }

        AppendAmendmentJournal(
            TradeJournalEventType.StopAmendmentRequested,
            command,
            new ProtectiveStopAmendmentResult
            {
                Status = ProtectiveStopAmendmentStatus.Pending,
                ClientAmendmentId = clientAmendmentId,
                PreviousStopOrderId = command.ExistingStopOrderId,
                CurrentStopOrderId = command.ExistingStopOrderId,
                RequestedStopPrice = normalizedStop,
                Certainty = ExecutionCertainty.NotSent
            },
            command.Reason);

        try
        {
            ProtectiveStopAmendmentResult result = await protectiveBroker.ProtectiveOrders
                .AmendProtectiveStopAsync(new AmendProtectiveStopRequest
                {
                    Instrument = command.Instrument,
                    PositionId = command.PositionId,
                    ExistingStopOrderId = command.ExistingStopOrderId,
                    CurrentStopPrice = command.CurrentStopPrice,
                    NewStopPrice = normalizedStop,
                    CurrentExecutablePrice = command.CurrentExecutablePrice,
                    PositionQuantity = command.PositionQuantity,
                    PositionSide = command.PositionSide,
                    MinimumPriceIncrement = command.MinimumPriceIncrement,
                    ClientAmendmentId = clientAmendmentId,
                    Reason = command.Reason,
                    RequestedAt = command.CreatedAt,
                    EffectiveFromExecutionSequence = command.EffectiveFromExecutionSequence
                }, cancellationToken).ConfigureAwait(false);

            TradeJournalEventType journalType = result.Status switch
            {
                ProtectiveStopAmendmentStatus.Accepted or ProtectiveStopAmendmentStatus.Replaced =>
                    TradeJournalEventType.StopAmendmentAccepted,
                ProtectiveStopAmendmentStatus.Unsupported => TradeJournalEventType.StopAmendmentUnsupported,
                _ => TradeJournalEventType.StopAmendmentRejected
            };
            AppendAmendmentJournal(
                journalType,
                command,
                result,
                result.RejectionReason ?? command.Reason);
            if (result.Certainty == ExecutionCertainty.Unknown ||
                result.Status == ProtectiveStopAmendmentStatus.Unknown)
            {
                _safety?.Trip(
                    SafetyTripReason.ExecutionFailure,
                    $"Protective-stop amendment certainty is unknown for {command.PositionId}.",
                    command.CreatedAt);
            }
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A transport failure may have occurred after broker receipt. Preserve the
            // old local stop and trip safety so reconciliation occurs before new risk.
            _safety?.Trip(
                SafetyTripReason.ExecutionFailure,
                $"Protective-stop amendment outcome is unknown: {exception.Message}",
                command.CreatedAt);
            AppendAmendmentJournal(
                TradeJournalEventType.Error,
                command,
                new ProtectiveStopAmendmentResult
                {
                    Status = ProtectiveStopAmendmentStatus.Unknown,
                    ClientAmendmentId = clientAmendmentId,
                    PreviousStopOrderId = command.ExistingStopOrderId,
                    CurrentStopOrderId = command.ExistingStopOrderId,
                    RequestedStopPrice = normalizedStop,
                    RejectionReason = exception.Message,
                    Certainty = ExecutionCertainty.Unknown
                },
                exception.Message);
            throw;
        }
    }

    private string? GetSafetyRejection()
    {
        if (_safety?.Snapshot is { CanOpenNewTrades: false } safety)
        {
            return $"Trading safety is {safety.State}: " +
                $"{safety.Message ?? safety.Reason.ToString()}.";
        }

        return null;
    }

    private static PlaceOrderRequest CreateRequest(
        AgentDecision decision,
        decimal quantity,
        string clientOrderId,
        IReadOnlyList<BrokerPosition> positions)
    {
        OrderSide side;
        if (decision.Action == AgentAction.Close)
        {
            (OrderSide closeSide, decimal closableQuantity) =
                ResolveClosePosition(positions, decision.Instrument);
            side = closeSide;
            quantity = Math.Min(quantity, closableQuantity);
        }
        else
        {
            side = decision.Action == AgentAction.Buy ? OrderSide.Buy : OrderSide.Sell;
        }

        return new PlaceOrderRequest
        {
            Instrument = decision.Instrument,
            Side = side,
            Type = decision.Action == AgentAction.Close ? StandardOrderType.Market : decision.OrderType,
            Quantity = new OrderQuantity(quantity, decision.QuantityUnit),
            LimitPrice = decision.Action == AgentAction.Close ? null : decision.LimitPrice,
            StopPrice = decision.Action == AgentAction.Close ? null : decision.StopPrice,
            StopLoss = decision.Action == AgentAction.Close || decision.StopLossPrice is null
                ? null
                : new StopLossInstruction(decision.StopLossPrice.Value),
            TakeProfit = decision.Action == AgentAction.Close || decision.TakeProfitPrice is null
                ? null
                : new TakeProfitInstruction(decision.TakeProfitPrice.Value),
            ClientOrderId = clientOrderId,
            StrategyId = decision.StrategyId,
            DecisionId = decision.DecisionId,
            SetupId = decision.SetupId,
            PortfolioReservationId = decision.PortfolioReservationId,
            RiskClusterId = decision.RiskClusterId,
            ReduceOnly = decision.Action == AgentAction.Close
        };
    }

    /// <summary>
    /// Resolves the position a Close decision targets. A hedging account can hold both a long and a
    /// short in the same instrument, where FirstOrDefault picked whichever the broker happened to
    /// list first and thereby silently chose both the close side and the quantity cap. Same-side
    /// positions are aggregated; a mixed-side book is genuinely ambiguous and must not be guessed.
    /// </summary>
    private static (OrderSide CloseSide, decimal ClosableQuantity) ResolveClosePosition(
        IReadOnlyList<BrokerPosition> positions,
        InstrumentKey instrument)
    {
        BrokerPosition[] matching = positions
            .Where(item => item.Instrument == instrument && item.Quantity > 0m)
            .ToArray();
        if (matching.Length == 0)
            throw new InvalidOperationException("A close decision requires an open position.");
        if (matching.DistinctBy(item => item.Side).Count() > 1)
        {
            throw new InvalidOperationException(
                $"A close decision for {instrument} is ambiguous: the broker reports both long and " +
                "short positions for it. Close them individually by position id.");
        }

        return (
            matching[0].Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy,
            matching.Sum(item => item.Quantity));
    }

    private static void ValidateDecision(AgentDecision decision)
    {
        if (decision.Action is not (AgentAction.Buy or AgentAction.Sell or AgentAction.Close))
        {
            throw new NotSupportedException(
                $"The execution coordinator does not implement {decision.Action}.");
        }

        if (decision.SuggestedQuantity is not decimal quantity)
        {
            throw new InvalidOperationException("A buy, sell, or close decision requires a quantity.");
        }

        if (quantity <= 0m)
        {
            throw new InvalidOperationException("A buy, sell, or close decision requires a positive quantity.");
        }

        if (decision.Instrument.IsEmpty)
        {
            throw new InvalidOperationException("A buy, sell, or close decision requires an instrument.");
        }

        if (decision.Confidence is < 0m or > 100m)
        {
            throw new InvalidOperationException("Decision confidence must be from 0 to 100.");
        }
    }

    private static void ValidateOptions(ExecutionOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ClientOrderIdPrefix) ||
            options.ClientOrderIdPrefix.Any(character =>
                !char.IsLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Client order ID prefix must contain only letters, digits, '-' or '_'.");
        }
    }

    private static void ValidateAmendmentCommand(ProtectiveStopAmendmentCommand command)
    {
        if (command.Instrument.IsEmpty ||
            string.IsNullOrWhiteSpace(command.StrategyId) ||
            string.IsNullOrWhiteSpace(command.SetupId) ||
            string.IsNullOrWhiteSpace(command.PositionId) ||
            string.IsNullOrWhiteSpace(command.Reason))
        {
            throw new ArgumentException(
                "Instrument, strategy, setup, position, and reason are required.",
                nameof(command));
        }
        if (command.PositionSide is not (OrderSide.Buy or OrderSide.Sell))
            throw new ArgumentException("PositionSide must be Buy or Sell.", nameof(command));
        if (command.PositionQuantity <= 0m || command.EntryPrice <= 0m ||
            command.InitialStopPrice <= 0m || command.CurrentStopPrice <= 0m ||
            command.ProposedStopPrice <= 0m || command.CurrentExecutablePrice <= 0m ||
            command.MinimumPriceIncrement <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(command), "Amendment prices and quantity must be positive.");
        }
        if (command.EffectiveFromExecutionSequence <= command.RequestedSequence)
        {
            throw new ArgumentException(
                "The replacement stop must become effective after its request sequence.",
                nameof(command));
        }
        bool initialStopValid = command.PositionSide == OrderSide.Buy
            ? command.InitialStopPrice < command.EntryPrice
            : command.InitialStopPrice > command.EntryPrice;
        if (!initialStopValid)
            throw new ArgumentException("Initial stop is on the wrong side of entry.", nameof(command));
    }

    private static string? ValidateRiskReduction(
        ProtectiveStopAmendmentCommand command,
        decimal normalizedStop)
    {
        if (normalizedStop <= 0m)
            return "The normalized stop price is not positive.";
        return command.PositionSide switch
        {
            OrderSide.Buy when normalizedStop <= command.CurrentStopPrice =>
                "A long protective stop must move upward.",
            OrderSide.Buy when normalizedStop >= command.CurrentExecutablePrice =>
                "A long protective stop must remain below the current executable bid.",
            OrderSide.Sell when normalizedStop >= command.CurrentStopPrice =>
                "A short protective stop must move downward.",
            OrderSide.Sell when normalizedStop <= command.CurrentExecutablePrice =>
                "A short protective stop must remain above the current executable ask.",
            _ => null
        };
    }

    private static decimal NormalizeRiskReducingPrice(
        decimal price,
        decimal increment,
        OrderSide positionSide)
    {
        try
        {
            decimal units = price / increment;
            return positionSide == OrderSide.Buy
                ? decimal.Floor(units) * increment
                : decimal.Ceiling(units) * increment;
        }
        catch (OverflowException)
        {
            // A price/increment ratio outside decimal's range cannot be normalized. Returning zero
            // routes it into ValidateRiskReduction's non-positive rejection rather than throwing
            // out of the amendment path.
            return 0m;
        }
    }

    private static string CreateAmendmentId(
        ProtectiveStopAmendmentCommand command,
        decimal normalizedStop) =>
        $"stop-{CreateAmendmentFingerprint(command, normalizedStop)[..24]}";

    private static string CreateAmendmentFingerprint(
        ProtectiveStopAmendmentCommand command,
        decimal normalizedStop)
    {
        string identity = string.Join(
            '|',
            command.Instrument.Value,
            command.StrategyId,
            command.SetupId,
            command.PositionId,
            command.ExistingStopOrderId,
            command.PositionSide,
            command.PositionQuantity.ToString(CultureInfo.InvariantCulture),
            command.InitialStopPrice.ToString(CultureInfo.InvariantCulture),
            command.CurrentStopPrice.ToString(CultureInfo.InvariantCulture),
            normalizedStop.ToString(CultureInfo.InvariantCulture),
            command.CurrentExecutablePrice.ToString(CultureInfo.InvariantCulture),
            command.AmendmentReason,
            command.RequestedSequence,
            command.EffectiveFromExecutionSequence);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
    }

    private static ProtectiveStopAmendmentResult RejectAmendment(
        ProtectiveStopAmendmentCommand command,
        string clientAmendmentId,
        decimal requestedStop,
        string reason) => new()
    {
        Status = ProtectiveStopAmendmentStatus.Rejected,
        ClientAmendmentId = clientAmendmentId,
        PreviousStopOrderId = command.ExistingStopOrderId,
        CurrentStopOrderId = command.ExistingStopOrderId,
        RequestedStopPrice = requestedStop,
        RejectionReason = reason,
        Certainty = ExecutionCertainty.Rejected
    };

    private void AppendAmendmentJournal(
        TradeJournalEventType type,
        ProtectiveStopAmendmentCommand command,
        ProtectiveStopAmendmentResult result,
        string message)
    {
        _journal.Append(new TradeJournalEntry
        {
            Sequence = command.RequestedSequence,
            Timestamp = command.CreatedAt,
            Type = type,
            Instrument = command.Instrument,
            PositionId = command.PositionId,
            ClientOrderId = result.ClientAmendmentId,
            PreviousValue = command.CurrentStopPrice,
            Value = result.AcceptedStopPrice ?? result.RequestedStopPrice,
            Message = message
        });
    }

    private static decimal ResolveQuoteToAccountRate(
        AgentDecision decision,
        ITradingBrokerClient broker,
        IReadOnlyList<AccountSnapshot> accounts)
    {
        if (broker is IAccountCurrencyConversionProvider provider &&
            provider.TryGetQuoteToAccountCurrencyRate(decision.Instrument, out decimal provided) &&
            provided > 0m)
        {
            return provided;
        }

        string? accountCurrency = accounts
            .Select(account => account.Currency)
            .FirstOrDefault(currency => !string.IsNullOrWhiteSpace(currency));
        if (string.IsNullOrWhiteSpace(accountCurrency))
        {
            // Fixed-quantity mode can proceed without conversion. Risk-based sizers
            // receive zero and reject explicitly with MissingCurrencyConversion.
            return 0m;
        }

        string value = decision.Instrument.Value;
        int prefix = value.IndexOf(':');
        string pair = prefix >= 0 ? value[(prefix + 1)..] : value;
        int separator = pair.LastIndexOfAny(['/', '_', '-']);
        if (separator <= 0 || separator >= pair.Length - 1)
        {
            return 0m;
        }

        string baseCurrency = pair[..separator].Trim();
        string quoteCurrency = pair[(separator + 1)..].Trim();
        if (string.Equals(quoteCurrency, accountCurrency, StringComparison.OrdinalIgnoreCase))
        {
            return 1m;
        }

        decimal reference = decision.ReferencePrice ?? decision.LimitPrice ?? decision.StopPrice ?? 0m;
        if (reference > 0m &&
            string.Equals(baseCurrency, accountCurrency, StringComparison.OrdinalIgnoreCase))
        {
            return 1m / reference;
        }

        return 0m;
    }

    private static string CreateClientOrderId(AgentDecision decision, string prefix)
    {
        string identity = decision.DecisionId ?? string.Join(
            '|',
            decision.CreatedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            decision.Instrument.Value,
            decision.Action,
            decision.SuggestedQuantity?.ToString(CultureInfo.InvariantCulture),
            decision.QuantityUnit,
            decision.OrderType,
            decision.LimitPrice?.ToString(CultureInfo.InvariantCulture),
            decision.StopPrice?.ToString(CultureInfo.InvariantCulture),
            decision.StopLossPrice?.ToString(CultureInfo.InvariantCulture),
            decision.TakeProfitPrice?.ToString(CultureInfo.InvariantCulture));
        string hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant()[..16];
        return $"{prefix}-{decision.CreatedAt.ToUnixTimeMilliseconds()}-{hash}";
    }

    private void AppendJournal(
        TradeJournalEventType type,
        AgentDecision decision,
        string? clientOrderId,
        string message,
        decimal? value = null)
    {
        _journal.Append(new TradeJournalEntry
        {
            Sequence = 0,
            Timestamp = decision.CreatedAt,
            Type = type,
            Instrument = decision.Instrument,
            Action = decision.Action,
            DecisionId = decision.DecisionId,
            ClientOrderId = clientOrderId,
            Confidence = decision.Confidence,
            Value = value,
            Message = message
        });
    }

    private static OrderSubmission RejectWithoutSending(string clientOrderId, string reason) => new()
    {
        ClientOrderId = clientOrderId,
        Status = SubmissionStatus.Rejected,
        Certainty = ExecutionCertainty.NotSent,
        RejectionReason = reason
    };

    private sealed record AmendmentOperation(
        string Fingerprint,
        Lazy<Task<ProtectiveStopAmendmentResult>> Task);
}
