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
    private readonly IBrokerExecutionSafety _brokerSafety;
    private readonly ITradingSafetyController? _safety;
    private readonly ITradeJournal _journal;
    private readonly ConcurrentDictionary<string, Lazy<Task<OrderSubmission?>>> _inFlight = [];

    public ExecutionCoordinator(
        ExecutionOptions? options = null,
        IPreTradeRiskManager? riskManager = null,
        IBrokerExecutionSafety? brokerSafety = null,
        ITradingSafetyController? safety = null,
        ITradeJournal? journal = null)
    {
        _options = options ?? new ExecutionOptions();
        ValidateOptions(_options);
        _riskManager = riskManager ?? new PreTradeRiskManager();
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

        ValidateDecision(decision);
        string clientOrderId = CreateClientOrderId(decision, _options.ClientOrderIdPrefix);
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
                foreach (BrokerOrder order in openOrders)
                {
                    await broker.Orders.CancelOrderAsync(order.BrokerOrderId, cancellationToken)
                        .ConfigureAwait(false);
                }
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

                var riskContext = new PreTradeRiskContext
                {
                    Decision = decision,
                    Quantity = quantity,
                    Accounts = accounts,
                    Positions = positions,
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
            BrokerPosition position = positions.FirstOrDefault(item => item.Instrument == decision.Instrument)
                ?? throw new InvalidOperationException("A close decision requires an open position.");
            side = position.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
            quantity = Math.Min(quantity, position.Quantity);
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
            ClientOrderId = clientOrderId
        };
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
}
