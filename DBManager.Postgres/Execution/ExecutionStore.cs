using DBManager.Abstractions;
using DBManager.Abstractions.Decision;
using DBManager.Abstractions.Execution;
using DBManager.Abstractions.Risk;
using DBManager.Postgres.Decision;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace DBManager.Postgres.Execution;

/// <summary>
/// Raw-Npgsql implementation for the critical atomic transactions (section 14.3: broker-event
/// atomic application, submission intent, cursor pagination — "commands where exact SQL locking
/// semantics matter"), plus an EF Core recovery read. Writes through
/// <see cref="PersistenceLane.Critical"/> since only <c>trading_runtime</c> can write the
/// <c>execution</c>/<c>risk</c>/<c>operations</c> schemas (section 25.1).
/// </summary>
public sealed class ExecutionStore(
    IDbContextFactory<TradingHubDbContext> contextFactory,
    [FromKeyedServices(PersistenceLane.Critical)] NpgsqlDataSource criticalDataSource)
    : IExecutionStore
{
    private static readonly ReservationState[] ActiveReservationStates =
    [
        ReservationState.Created, ReservationState.SubmissionPending,
        ReservationState.BrokerAccepted, ReservationState.PartiallyFilled
    ];

    private static readonly OrderState[] ActiveOrderStates =
    [
        OrderState.SubmissionPending, OrderState.SubmissionUnknown,
        OrderState.Accepted, OrderState.PartiallyFilled, OrderState.Filled
    ];

    public async Task<OrderSubmissionIntentResult> CreateSubmissionIntentAsync(
        CreateOrderSubmissionIntent command, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await criticalDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        Guid? existingOrderId = await FindOrderIdByIdempotencyKeyAsync(
            connection, transaction, command.IdempotencyKey, cancellationToken).ConfigureAwait(false);
        if (existingOrderId is { } orderId)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new OrderSubmissionIntentResult
            {
                Result = DurableResult.Duplicate("submission_intent_already_exists"),
                OrderId = orderId,
                CommandId = command.CommandId
            };
        }

        ReservationState? reservationState = await GetReservationStateAsync(
            connection, transaction, command.ReservationId, cancellationToken).ConfigureAwait(false);
        if (reservationState is null || !ActiveReservationStates.Contains(reservationState.Value))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new OrderSubmissionIntentResult
            {
                Result = DurableResult.PermanentFailure("reservation_not_active"),
                OrderId = Guid.Empty,
                CommandId = command.CommandId
            };
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid newOrderId = Guid.NewGuid();

        await ExecuteAsync(connection, transaction, cancellationToken,
            """
            INSERT INTO execution.order_commands
                (command_id, idempotency_key, candidate_id, reservation_id, command_type, client_order_id,
                 requested_quantity, requested_stop, requested_target, status, created_at, started_at,
                 attempt_count, version)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $11, 1, 1)
            """,
            command.CommandId, command.IdempotencyKey, command.CandidateId, command.ReservationId,
            (short)command.CommandType, command.ClientOrderId, command.RequestedQuantity,
            (object?)command.RequestedStop ?? DBNull.Value, (object?)command.RequestedTarget ?? DBNull.Value,
            (short)OrderCommandStatus.InFlight, P(now, NpgsqlDbType.TimestampTz));

        await ExecuteAsync(connection, transaction, cancellationToken,
            """
            INSERT INTO execution.orders
                (order_id, command_id, candidate_id, reservation_id, broker_account_id, instrument_id,
                 strategy_id, client_order_id, direction, requested_quantity, filled_quantity,
                 initial_stop_price, initial_target_price, state, submission_certainty, created_at, version)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, 0, $11, $12, $13, $14, $15, 1)
            """,
            newOrderId, command.CommandId, command.CandidateId, command.ReservationId, command.BrokerAccountId,
            command.InstrumentId, command.StrategyId, command.ClientOrderId, (short)command.Direction,
            command.RequestedQuantity, command.InitialStopPrice,
            (object?)command.InitialTargetPrice ?? DBNull.Value, (short)OrderState.SubmissionPending,
            (short)SubmissionCertainty.Certain, P(now, NpgsqlDbType.TimestampTz));

        await InsertOrderEventAsync(connection, transaction, newOrderId, now, OrderEventType.Created,
            null, OrderState.SubmissionPending, null, null, null, null, "{}", cancellationToken)
            .ConfigureAwait(false);

        await UpdateReservationStateAsync(connection, transaction, command.ReservationId,
            reservationState.Value, ReservationState.SubmissionPending, "submission_intent_created", newOrderId,
            now, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new OrderSubmissionIntentResult
        {
            Result = DurableResult.Committed(),
            OrderId = newOrderId,
            CommandId = command.CommandId
        };
    }

    public async Task<DurableResult> ApplyImmediateSubmissionResultAsync(
        ApplyImmediateSubmissionResult command, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await criticalDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        (Guid ReservationId, OrderState State)? order = await GetOrderForUpdateAsync(
            connection, transaction, command.OrderId, cancellationToken).ConfigureAwait(false);
        if (order is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return DurableResult.PermanentFailure("order_not_found");
        }

        ReservationState? reservationState = await GetReservationStateAsync(
            connection, transaction, order.Value.ReservationId, cancellationToken).ConfigureAwait(false);

        (OrderState newOrderState, SubmissionCertainty certainty, ReservationState? newReservationState, OrderEventType eventType) =
            command.Outcome switch
            {
                SubmissionOutcome.Accepted =>
                    (OrderState.Accepted, SubmissionCertainty.Certain, (ReservationState?)ReservationState.BrokerAccepted, OrderEventType.Accepted),
                SubmissionOutcome.Rejected =>
                    (OrderState.Rejected, SubmissionCertainty.Certain, (ReservationState?)ReservationState.Rejected, OrderEventType.Rejected),
                // Unknown retains the reservation (section 12.5: "never retry blindly").
                _ => (OrderState.SubmissionUnknown, SubmissionCertainty.Unknown, (ReservationState?)null, OrderEventType.SubmissionUnknown)
            };

        await ExecuteAsync(connection, transaction, cancellationToken,
            "UPDATE execution.orders SET state = $1, submission_certainty = $2, " +
            "broker_order_id = COALESCE($3, broker_order_id), accepted_at = CASE WHEN $1 = $4 THEN $5 ELSE accepted_at END, " +
            "version = version + 1 WHERE order_id = $6",
            (short)newOrderState, (short)certainty, (object?)command.BrokerOrderId ?? DBNull.Value,
            (short)OrderState.Accepted, P(command.OccurredAt, NpgsqlDbType.TimestampTz), command.OrderId);

        await InsertOrderEventAsync(connection, transaction, command.OrderId, command.OccurredAt, eventType,
            order.Value.State, newOrderState, null, null, null, command.ReasonCode, "{}", cancellationToken)
            .ConfigureAwait(false);

        if (newReservationState is { } toState && reservationState is { } fromState)
        {
            await UpdateReservationStateAsync(connection, transaction, order.Value.ReservationId, fromState,
                toState, command.ReasonCode ?? command.Outcome.ToString(), command.OrderId, command.OccurredAt,
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return DurableResult.Committed();
    }

    /// <summary>The atomic broker-event applier from section 13.1.</summary>
    public async Task<BrokerEventApplyResult> ApplyBrokerEventAsync(
        ApplyBrokerEvent command, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await criticalDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await ExecuteAsync(connection, transaction, cancellationToken,
                "INSERT INTO execution.broker_transaction_keys (broker_account_id, broker_transaction_id, broker_time) " +
                "VALUES ($1, $2, $3)",
                command.BrokerAccountId, command.BrokerTransactionId, P(command.BrokerTime, NpgsqlDbType.TimestampTz));

            DateTimeOffset now = DateTimeOffset.UtcNow;
            await ExecuteAsync(connection, transaction, cancellationToken,
                """
                INSERT INTO execution.broker_transactions
                    (broker_account_id, broker_transaction_id, broker_time, received_at, transaction_type,
                     client_order_id, broker_order_id, broker_trade_id, instrument_id, quantity, price,
                     reason_code, raw_payload, applied_at)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13::jsonb, $4)
                """,
                command.BrokerAccountId, command.BrokerTransactionId, P(command.BrokerTime, NpgsqlDbType.TimestampTz),
                P(now, NpgsqlDbType.TimestampTz), (short)command.TransactionType,
                (object?)command.ClientOrderId ?? DBNull.Value, (object?)command.BrokerOrderId ?? DBNull.Value,
                (object?)command.BrokerTradeId ?? DBNull.Value, (object?)command.InstrumentId ?? DBNull.Value,
                (object?)command.Quantity ?? DBNull.Value, (object?)command.Price ?? DBNull.Value,
                (object?)command.ReasonCode ?? DBNull.Value, command.RawPayloadJson);

            Guid? orderId = null;
            Guid? positionId = null;

            if (command.ClientOrderId is { } clientOrderId)
            {
                (Guid OrderId, Guid ReservationId, Guid CandidateId, OrderState State, decimal FilledQuantity,
                    decimal RequestedQuantity, decimal? AverageFillPrice, TradeDirection Direction,
                    decimal InitialStopPrice, decimal? InitialTargetPrice, Guid BrokerAccountId, long InstrumentId,
                    string StrategyId)? order =
                    await GetOrderByClientOrderIdAsync(connection, transaction, clientOrderId, cancellationToken)
                        .ConfigureAwait(false);

                if (order is not null)
                {
                    orderId = order.Value.OrderId;
                    positionId = command.TransactionType switch
                    {
                        BrokerTransactionType.OrderFill when command.Quantity is { } qty && command.Price is { } price =>
                            await ApplyFillAsync(connection, transaction, order.Value, qty, price, command,
                                now, cancellationToken).ConfigureAwait(false),
                        BrokerTransactionType.OrderReject or BrokerTransactionType.OrderCancel =>
                            await ApplyTerminalOrderOutcomeAsync(connection, transaction, order.Value, command,
                                now, cancellationToken).ConfigureAwait(false),
                        _ => null
                    };
                }
            }

            await UpsertCursorAsync(connection, transaction, command.BrokerAccountId, command.BrokerTransactionId,
                command.BrokerTime, now, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new BrokerEventApplyResult { Result = DurableResult.Committed(), OrderId = orderId, PositionId = positionId };
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new BrokerEventApplyResult { Result = DurableResult.Duplicate("broker_transaction_already_applied") };
        }
    }

    public async Task<RecoveryState> LoadRecoveryStateAsync(Guid brokerAccountId, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        List<OrderEntity> orders = await context.Set<OrderEntity>()
            .AsNoTracking()
            .Where(o => o.BrokerAccountId == brokerAccountId && ActiveOrderStates.Contains(o.State))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        List<PositionEntity> positions = await context.Set<PositionEntity>()
            .AsNoTracking()
            .Where(p => p.BrokerAccountId == brokerAccountId && p.State != PositionState.Closed)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        Operations.BrokerStreamCursorEntity? cursor = await context.Set<Operations.BrokerStreamCursorEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.BrokerAccountId == brokerAccountId, cancellationToken)
            .ConfigureAwait(false);

        return new RecoveryState
        {
            BrokerAccountId = brokerAccountId,
            OpenOrders = orders.Select(o => new OrderDetail
            {
                OrderId = o.OrderId,
                CandidateId = o.CandidateId,
                ReservationId = o.ReservationId,
                BrokerAccountId = o.BrokerAccountId,
                InstrumentId = o.InstrumentId,
                StrategyId = o.StrategyId,
                ClientOrderId = o.ClientOrderId,
                BrokerOrderId = o.BrokerOrderId,
                BrokerTradeId = o.BrokerTradeId,
                Direction = o.Direction,
                RequestedQuantity = o.RequestedQuantity,
                FilledQuantity = o.FilledQuantity,
                AverageFillPrice = o.AverageFillPrice,
                State = o.State,
                SubmissionCertainty = o.SubmissionCertainty,
                Version = o.Version
            }).ToArray(),
            OpenPositions = positions.Select(p => new PositionDetail
            {
                PositionId = p.PositionId,
                BrokerAccountId = p.BrokerAccountId,
                InstrumentId = p.InstrumentId,
                StrategyId = p.StrategyId,
                BrokerTradeId = p.BrokerTradeId,
                Direction = p.Direction,
                State = p.State,
                RemainingQuantity = p.RemainingQuantity,
                AverageEntryPrice = p.AverageEntryPrice,
                RealisedPnl = p.RealisedPnl,
                Version = p.Version
            }).ToArray(),
            Cursor = cursor is null ? null : new BrokerStreamCursorDetail
            {
                BrokerAccountId = cursor.BrokerAccountId,
                LastAppliedTransactionId = cursor.LastAppliedTransactionId,
                LastAppliedBrokerTime = cursor.LastAppliedBrokerTime,
                ConnectionGeneration = cursor.ConnectionGeneration
            }
        };
    }

    private async Task<Guid?> ApplyFillAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        (Guid OrderId, Guid ReservationId, Guid CandidateId, OrderState State, decimal FilledQuantity,
            decimal RequestedQuantity, decimal? AverageFillPrice, TradeDirection Direction, decimal InitialStopPrice,
            decimal? InitialTargetPrice, Guid BrokerAccountId, long InstrumentId, string StrategyId) order,
        decimal quantity, decimal price, ApplyBrokerEvent command, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        decimal newFilledQuantity = order.FilledQuantity + quantity;
        decimal newAveragePrice = order.FilledQuantity == 0 || order.AverageFillPrice is null
            ? price
            : ((order.FilledQuantity * order.AverageFillPrice.Value) + (quantity * price)) / newFilledQuantity;
        bool fullyFilled = newFilledQuantity >= order.RequestedQuantity;
        OrderState newOrderState = fullyFilled ? OrderState.Filled : OrderState.PartiallyFilled;

        await ExecuteAsync(connection, transaction, cancellationToken,
            "UPDATE execution.orders SET filled_quantity = $1, average_fill_price = $2, state = $3, " +
            "broker_trade_id = COALESCE($4, broker_trade_id), " +
            "filled_at = CASE WHEN $3 = $5 THEN $6 ELSE filled_at END, version = version + 1 " +
            "WHERE order_id = $7",
            newFilledQuantity, newAveragePrice, (short)newOrderState, (object?)command.BrokerTradeId ?? DBNull.Value,
            (short)OrderState.Filled, P(now, NpgsqlDbType.TimestampTz), order.OrderId);

        await InsertOrderEventAsync(connection, transaction, order.OrderId, now,
            fullyFilled ? OrderEventType.Filled : OrderEventType.PartialFill, order.State, newOrderState,
            command.BrokerTransactionId, quantity, price, command.ReasonCode, "{}", cancellationToken)
            .ConfigureAwait(false);

        string? brokerTradeId = command.BrokerTradeId;
        Guid? positionId = null;
        if (brokerTradeId is not null)
        {
            positionId = await UpsertPositionOnFillAsync(connection, transaction, order, brokerTradeId, quantity,
                price, now, cancellationToken).ConfigureAwait(false);
        }

        ReservationState? currentReservationState = await GetReservationStateAsync(
            connection, transaction, order.ReservationId, cancellationToken).ConfigureAwait(false);
        if (currentReservationState is { } fromState)
        {
            ReservationState toState = fullyFilled ? ReservationState.Filled : ReservationState.PartiallyFilled;
            if (fromState != toState)
            {
                await UpdateReservationStateAsync(connection, transaction, order.ReservationId, fromState, toState,
                    fullyFilled ? "order_filled" : "order_partially_filled", order.OrderId, now, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return positionId;
    }

    private static async Task<Guid> UpsertPositionOnFillAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        (Guid OrderId, Guid ReservationId, Guid CandidateId, OrderState State, decimal FilledQuantity,
            decimal RequestedQuantity, decimal? AverageFillPrice, TradeDirection Direction, decimal InitialStopPrice,
            decimal? InitialTargetPrice, Guid BrokerAccountId, long InstrumentId, string StrategyId) order,
        string brokerTradeId, decimal quantity, decimal price, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand findCommand = new(
            "SELECT position_id, remaining_quantity, average_entry_price, original_quantity, version " +
            "FROM execution.positions WHERE broker_account_id = $1 AND broker_trade_id = $2 AND strategy_id = $3",
            connection, transaction);
        findCommand.Parameters.Add(new NpgsqlParameter { Value = order.BrokerAccountId });
        findCommand.Parameters.Add(new NpgsqlParameter { Value = brokerTradeId });
        findCommand.Parameters.Add(new NpgsqlParameter { Value = order.StrategyId });

        await using (NpgsqlDataReader reader = await findCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                Guid existingPositionId = reader.GetGuid(0);
                decimal remaining = reader.GetDecimal(1);
                decimal averageEntry = reader.GetDecimal(2);
                decimal original = reader.GetDecimal(3);
                await reader.CloseAsync().ConfigureAwait(false);

                decimal newRemaining = remaining + quantity;
                decimal newAverage = ((remaining * averageEntry) + (quantity * price)) / newRemaining;

                await ExecuteAsync(connection, transaction, cancellationToken,
                    "UPDATE execution.positions SET remaining_quantity = $1, original_quantity = $2, " +
                    "average_entry_price = $3, version = version + 1 WHERE position_id = $4",
                    newRemaining, original + quantity, newAverage, existingPositionId);

                await InsertPositionEventAsync(connection, transaction, existingPositionId, now,
                    PositionEventType.Increased, quantity, price, null, null, cancellationToken)
                    .ConfigureAwait(false);

                return existingPositionId;
            }
        }

        Guid newPositionId = Guid.NewGuid();
        Guid policyRevisionId = await GetPolicyRevisionIdForCandidateAsync(
            connection, transaction, order.CandidateId, cancellationToken).ConfigureAwait(false);
        decimal reservedRisk = await GetReservationRiskAsync(
            connection, transaction, order.ReservationId, cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, transaction, cancellationToken,
            """
            INSERT INTO execution.positions
                (position_id, broker_account_id, instrument_id, strategy_id, policy_revision_id, broker_trade_id,
                 direction, state, original_quantity, remaining_quantity, average_entry_price,
                 initial_stop_price, current_stop_price, target_price, original_risk, remaining_risk,
                 realised_pnl, unrealised_pnl, commission, financing, opened_at, version)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $9, $10, $11, $11, $12, $13, $13, 0, 0, 0, 0, $14, 1)
            """,
            newPositionId, order.BrokerAccountId, order.InstrumentId, order.StrategyId, policyRevisionId,
            brokerTradeId, (short)order.Direction, (short)Abstractions.Execution.PositionState.Open, quantity, price,
            order.InitialStopPrice, (object?)order.InitialTargetPrice ?? DBNull.Value, reservedRisk,
            P(now, NpgsqlDbType.TimestampTz));

        await InsertPositionEventAsync(connection, transaction, newPositionId, now, PositionEventType.Opened,
            quantity, price, null, null, cancellationToken).ConfigureAwait(false);

        return newPositionId;
    }

    private async Task<Guid?> ApplyTerminalOrderOutcomeAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        (Guid OrderId, Guid ReservationId, Guid CandidateId, OrderState State, decimal FilledQuantity,
            decimal RequestedQuantity, decimal? AverageFillPrice, TradeDirection Direction, decimal InitialStopPrice,
            decimal? InitialTargetPrice, Guid BrokerAccountId, long InstrumentId, string StrategyId) order,
        ApplyBrokerEvent command, DateTimeOffset now, CancellationToken cancellationToken)
    {
        OrderState newState = command.TransactionType == BrokerTransactionType.OrderReject
            ? OrderState.Rejected
            : OrderState.Cancelled;

        await ExecuteAsync(connection, transaction, cancellationToken,
            "UPDATE execution.orders SET state = $1, closed_at = $2, version = version + 1 WHERE order_id = $3",
            (short)newState, P(now, NpgsqlDbType.TimestampTz), order.OrderId);

        await InsertOrderEventAsync(connection, transaction, order.OrderId, now,
            command.TransactionType == BrokerTransactionType.OrderReject ? OrderEventType.Rejected : OrderEventType.Closed,
            order.State, newState, command.BrokerTransactionId, null, null, command.ReasonCode, "{}",
            cancellationToken).ConfigureAwait(false);

        ReservationState? fromState = await GetReservationStateAsync(
            connection, transaction, order.ReservationId, cancellationToken).ConfigureAwait(false);
        if (fromState is { } from && ActiveReservationStates.Contains(from))
        {
            await UpdateReservationStateAsync(connection, transaction, order.ReservationId, from,
                ReservationState.Released, "order_" + newState.ToString().ToLowerInvariant(), order.OrderId, now,
                cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private static async Task<Guid?> FindOrderIdByIdempotencyKeyAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(
            "SELECT o.order_id FROM execution.order_commands c " +
            "JOIN execution.orders o ON o.command_id = c.command_id " +
            "WHERE c.idempotency_key = $1",
            connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = idempotencyKey });
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is Guid guid ? guid : null;
    }

    private static async Task<ReservationState?> GetReservationStateAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid reservationId,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(
            "SELECT state FROM risk.reservations WHERE reservation_id = $1", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = reservationId });
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is short s ? (ReservationState)s : null;
    }

    private static async Task<decimal> GetReservationRiskAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid reservationId,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(
            "SELECT reserved_risk FROM risk.reservations WHERE reservation_id = $1", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = reservationId });
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is decimal d ? d : 0m;
    }

    private static async Task<Guid> GetPolicyRevisionIdForCandidateAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid candidateId,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(
            "SELECT policy_revision_id FROM decision.trade_candidates WHERE candidate_id = $1 LIMIT 1",
            connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = candidateId });
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is Guid guid ? guid : Guid.Empty;
    }

    private static async Task<(Guid OrderId, Guid ReservationId, Guid CandidateId, OrderState State,
        decimal FilledQuantity, decimal RequestedQuantity, decimal? AverageFillPrice, TradeDirection Direction,
        decimal InitialStopPrice, decimal? InitialTargetPrice, Guid BrokerAccountId, long InstrumentId,
        string StrategyId)?>
        GetOrderByClientOrderIdAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, string clientOrderId,
            CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(
            "SELECT order_id, reservation_id, candidate_id, state, filled_quantity, requested_quantity, " +
            "average_fill_price, direction, initial_stop_price, initial_target_price, broker_account_id, " +
            "instrument_id, strategy_id FROM execution.orders WHERE client_order_id = $1",
            connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = clientOrderId });
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return (
            reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), (OrderState)reader.GetInt16(3),
            reader.GetDecimal(4), reader.GetDecimal(5), reader.IsDBNull(6) ? null : reader.GetDecimal(6),
            (TradeDirection)reader.GetInt16(7), reader.GetDecimal(8),
            reader.IsDBNull(9) ? null : reader.GetDecimal(9),
            reader.GetGuid(10), reader.GetInt64(11), reader.GetString(12));
    }

    private static async Task<(Guid ReservationId, OrderState State)?> GetOrderForUpdateAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid orderId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(
            "SELECT reservation_id, state FROM execution.orders WHERE order_id = $1", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = orderId });
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        return (reader.GetGuid(0), (OrderState)reader.GetInt16(1));
    }

    private static async Task UpdateReservationStateAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid reservationId, ReservationState fromState,
        ReservationState toState, string reasonCode, Guid? relatedOrderId, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, cancellationToken,
            "UPDATE risk.reservations SET state = $1, updated_at = $2, version = version + 1 WHERE reservation_id = $3",
            (short)toState, P(now, NpgsqlDbType.TimestampTz), reservationId);

        await using NpgsqlCommand eventCommand = new(
            "INSERT INTO risk.reservation_events (reservation_id, occurred_at, from_state, to_state, " +
            "reason_code, related_order_id, details) VALUES ($1, $2, $3, $4, $5, $6, $7::jsonb)",
            connection, transaction);
        eventCommand.Parameters.Add(new NpgsqlParameter { Value = reservationId });
        eventCommand.Parameters.Add(new NpgsqlParameter { Value = now, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        eventCommand.Parameters.Add(new NpgsqlParameter { Value = (short)fromState });
        eventCommand.Parameters.Add(new NpgsqlParameter { Value = (short)toState });
        eventCommand.Parameters.Add(new NpgsqlParameter { Value = reasonCode });
        eventCommand.Parameters.Add(new NpgsqlParameter { Value = (object?)relatedOrderId ?? DBNull.Value });
        eventCommand.Parameters.Add(new NpgsqlParameter { Value = "{}" });
        await eventCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertOrderEventAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid orderId, DateTimeOffset occurredAt,
        OrderEventType eventType, OrderState? fromState, OrderState? toState, string? brokerTransactionId,
        decimal? quantity, decimal? price, string? reasonCode, string detailsJson, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(
            "INSERT INTO execution.order_events (order_id, occurred_at, event_type, from_state, to_state, " +
            "broker_transaction_id, quantity, price, reason_code, details) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10::jsonb)",
            connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = orderId });
        command.Parameters.Add(new NpgsqlParameter { Value = occurredAt, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        command.Parameters.Add(new NpgsqlParameter { Value = (short)eventType });
        command.Parameters.Add(new NpgsqlParameter { Value = fromState is null ? DBNull.Value : (short)fromState.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = toState is null ? DBNull.Value : (short)toState.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)brokerTransactionId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)quantity ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)price ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)reasonCode ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = detailsJson });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertPositionEventAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid positionId, DateTimeOffset occurredAt,
        PositionEventType eventType, decimal? quantityDelta, decimal? price, string? reasonCode,
        string? brokerTransactionId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(
            "INSERT INTO execution.position_events (position_id, occurred_at, event_type, quantity_delta, " +
            "price, reason_code, broker_transaction_id, details) VALUES ($1, $2, $3, $4, $5, $6, $7, $8::jsonb)",
            connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = positionId });
        command.Parameters.Add(new NpgsqlParameter { Value = occurredAt, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        command.Parameters.Add(new NpgsqlParameter { Value = (short)eventType });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)quantityDelta ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)price ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)reasonCode ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)brokerTransactionId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = "{}" });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertCursorAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid brokerAccountId, string transactionId,
        DateTimeOffset brokerTime, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(
            """
            INSERT INTO operations.broker_stream_cursors
                (broker_account_id, last_applied_transaction_id, last_applied_broker_time, connection_generation,
                 updated_at, version)
            VALUES ($1, $2, $3, 1, $4, 1)
            ON CONFLICT (broker_account_id) DO UPDATE SET
                last_applied_transaction_id = EXCLUDED.last_applied_transaction_id,
                last_applied_broker_time = EXCLUDED.last_applied_broker_time,
                updated_at = EXCLUDED.updated_at,
                version = operations.broker_stream_cursors.version + 1
            """,
            connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = brokerAccountId });
        command.Parameters.Add(new NpgsqlParameter { Value = transactionId });
        command.Parameters.Add(new NpgsqlParameter { Value = brokerTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        command.Parameters.Add(new NpgsqlParameter { Value = now, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static object P(DateTimeOffset value, NpgsqlDbType _) => value;

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken,
        string sql, params object[] parameters)
    {
        await using NpgsqlCommand command = new(sql, connection, transaction);
        foreach (object parameter in parameters)
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
