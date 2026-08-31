using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Agent.Models;
using Brokers.Abstractions;
using Brokers.Models;

namespace Agent.Execution;

public sealed class ExecutionCoordinator : IExecutionCoordinator
{
    private readonly ExecutionOptions _options;

    public ExecutionCoordinator(ExecutionOptions? options = null)
    {
        _options = options ?? new ExecutionOptions();
        if (_options.MinimumConfidence is < 0m or > 100m)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Minimum confidence must be from 0 to 100.");
        }

        if (_options.MaximumAbsolutePositionQuantity is <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Maximum position quantity must be positive when configured.");
        }
    }

    public async Task<OrderSubmission?> ProcessAsync(
        AgentDecision decision,
        ITradingBrokerClient broker,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(broker);

        if (decision.Action == AgentAction.Observe)
        {
            return null;
        }

        if (decision.Action == AgentAction.Cancel)
        {
            if (decision.Instrument.IsEmpty)
                throw new InvalidOperationException("A cancel decision requires an instrument.");
            if (string.IsNullOrWhiteSpace(decision.BrokerOrderId))
                throw new InvalidOperationException("A cancel decision requires a broker order ID.");

            await broker.Orders.CancelOrderAsync(decision.BrokerOrderId, cancellationToken)
                .ConfigureAwait(false);
            return null;
        }

        if (decision.Action is not (AgentAction.Buy or AgentAction.Sell))
        {
            throw new NotSupportedException(
                $"The reference execution coordinator does not yet implement {decision.Action}.");
        }

        decimal quantity = decision.SuggestedQuantity
            ?? throw new InvalidOperationException("A buy or sell decision requires a quantity.");
        if (quantity <= 0m)
        {
            throw new InvalidOperationException("A buy or sell decision requires a positive quantity.");
        }

        if (decision.Instrument.IsEmpty)
        {
            throw new InvalidOperationException("A buy or sell decision requires an instrument.");
        }

        if (decision.Confidence is < 0m or > 100m)
        {
            throw new InvalidOperationException("Decision confidence must be from 0 to 100.");
        }

        string clientOrderId = CreateClientOrderId(decision);
        string? riskRejection = await EvaluateRiskAsync(decision, broker, quantity, cancellationToken)
            .ConfigureAwait(false);
        if (riskRejection is not null)
        {
            return RejectWithoutSending(clientOrderId, riskRejection);
        }

        PlaceOrderRequest request = new()
        {
            Instrument = decision.Instrument,
            Side = decision.Action == AgentAction.Buy ? OrderSide.Buy : OrderSide.Sell,
            Type = decision.OrderType,
            Quantity = new OrderQuantity(quantity, decision.QuantityUnit),
            LimitPrice = decision.LimitPrice,
            StopPrice = decision.StopPrice,
            StopLoss = decision.StopLossPrice is null
                ? null
                : new StopLossInstruction(decision.StopLossPrice.Value),
            TakeProfit = decision.TakeProfitPrice is null
                ? null
                : new TakeProfitInstruction(decision.TakeProfitPrice.Value),
            ClientOrderId = clientOrderId
        };

        return await broker.Orders.PlaceOrderAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> EvaluateRiskAsync(
        AgentDecision decision,
        ITradingBrokerClient broker,
        decimal quantity,
        CancellationToken cancellationToken)
    {
        if (decision.Confidence < _options.MinimumConfidence)
        {
            return $"Decision confidence {decision.Confidence:F2} is below the configured minimum " +
                $"of {_options.MinimumConfidence:F2}.";
        }

        Task<IReadOnlyList<AccountSnapshot>> accountsTask =
            broker.Accounts.GetAccountsAsync(cancellationToken);
        Task<IReadOnlyList<BrokerPosition>> positionsTask =
            broker.Positions.GetOpenPositionsAsync(cancellationToken);
        Task<IReadOnlyList<BrokerOrder>> ordersTask =
            broker.Orders.GetOpenOrdersAsync(decision.Instrument, cancellationToken);
        await Task.WhenAll(accountsTask, positionsTask, ordersTask).ConfigureAwait(false);

        IReadOnlyList<AccountSnapshot> accounts = await accountsTask.ConfigureAwait(false);
        IReadOnlyList<BrokerPosition> positions = await positionsTask.ConfigureAwait(false);
        IReadOnlyList<BrokerOrder> orders = await ordersTask.ConfigureAwait(false);

        if (_options.BlockWhenAccountCannotTrade &&
            accounts.Count == 0)
        {
            return "The broker did not return an account to authorize trading.";
        }

        if (_options.BlockWhenAccountCannotTrade &&
            accounts.All(account => account.CanTrade != true))
        {
            return "No broker account explicitly reports that trading is enabled.";
        }

        if (_options.BlockDuplicateOpenOrders && orders.Any(order =>
                order.Instrument == decision.Instrument &&
                order.NormalizedStatus is OrderStatus.Pending or
                    OrderStatus.Open or
                    OrderStatus.PartiallyFilled))
        {
            return $"An open order already exists for {decision.Instrument}.";
        }

        decimal signedPosition = positions
            .Where(position => position.Instrument == decision.Instrument)
            .Sum(position => position.Side switch
            {
                OrderSide.Buy => position.Quantity,
                OrderSide.Sell => -position.Quantity,
                _ => 0m
            });
        decimal signedOrder = decision.Action == AgentAction.Buy ? quantity : -quantity;

        if (!_options.AllowPyramiding &&
            signedPosition != 0m &&
            Math.Sign(signedPosition) == Math.Sign(signedOrder))
        {
            return $"Pyramiding is disabled and a same-direction position already exists for " +
                $"{decision.Instrument}.";
        }

        if (_options.MaximumAbsolutePositionQuantity is decimal maximum)
        {
            if (decision.QuantityUnit is not (QuantityUnit.Units or QuantityUnit.BaseAsset))
            {
                return "The configured position cap cannot evaluate this quantity unit.";
            }

            decimal projected = Math.Abs(signedPosition + signedOrder);
            if (projected > maximum)
            {
                return $"Projected position quantity {projected:F8} exceeds the configured maximum " +
                    $"of {maximum:F8}.";
            }
        }

        return null;
    }

    private static string CreateClientOrderId(AgentDecision decision)
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
            decision.StopPrice?.ToString(CultureInfo.InvariantCulture));
        string hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant()[..16];
        return $"agent-{decision.CreatedAt.ToUnixTimeMilliseconds()}-{hash}";
    }

    private static OrderSubmission RejectWithoutSending(string clientOrderId, string reason) => new()
    {
        ClientOrderId = clientOrderId,
        Status = SubmissionStatus.Rejected,
        Certainty = ExecutionCertainty.NotSent,
        RejectionReason = reason
    };
}
