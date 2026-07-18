using DBManager.Abstractions;
using DBManager.Abstractions.Risk;
using DBManager.Postgres.Decision;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace DBManager.Postgres.Risk;

/// <summary>
/// EF Core implementation for CRUD/read operations, plus a raw-Npgsql atomic epoch-admission
/// transaction (section 14.3 explicitly calls out "portfolio epoch admission" as a direct-Npgsql
/// case, not EF Core). Writes through <see cref="PersistenceLane.Critical"/> since only
/// <c>trading_runtime</c> has write access to the <c>risk</c> schema (section 25.1).
/// </summary>
public sealed class RiskStore(
    IDbContextFactory<TradingHubDbContext> contextFactory,
    [FromKeyedServices(PersistenceLane.Critical)] NpgsqlDataSource criticalDataSource)
    : IRiskStore
{
    private static readonly ReservationState[] ActiveStates =
    [
        ReservationState.Created,
        ReservationState.SubmissionPending,
        ReservationState.BrokerAccepted,
        ReservationState.PartiallyFilled
    ];

    public async Task<DurableResult> RecordPositionSizingEvaluationAsync(
        RecordPositionSizingEvaluation command, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        context.Add(new PositionSizingEvaluationEntity
        {
            SizingId = command.SizingId,
            CandidateId = command.CandidateId,
            BrokerAccountId = command.BrokerAccountId,
            AccountSnapshotVersion = command.AccountSnapshotVersion,
            InstrumentMetadataRevision = command.InstrumentMetadataRevision,
            EvaluatedAt = command.EvaluatedAt,
            Equity = command.Equity,
            BaseRiskAmount = command.BaseRiskAmount,
            StopDistance = command.StopDistance,
            ExpectedSpread = command.ExpectedSpread,
            ExpectedSlippage = command.ExpectedSlippage,
            ExpectedCommission = command.ExpectedCommission,
            ConversionRate = command.ConversionRate,
            ConversionPath = command.ConversionPath,
            RawQuantity = command.RawQuantity,
            NormalizedQuantity = command.NormalizedQuantity,
            EstimatedStopLoss = command.EstimatedStopLoss,
            EstimatedMargin = command.EstimatedMargin,
            Approved = command.Approved,
            ReasonCode = command.ReasonCode,
            AuditJson = command.AuditJson
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return DurableResult.Committed();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            return DurableResult.Duplicate("sizing_evaluation_already_exists");
        }
    }

    /// <summary>The atomic decision-epoch admission transaction from section 12.2.</summary>
    public async Task<PortfolioAdmissionResult> CommitPortfolioEpochAsync(
        CommitPortfolioEpoch command, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await criticalDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            long lockKey = BitConverter.ToInt64(command.BrokerAccountId.ToByteArray(), 0);
            await using (NpgsqlCommand lockCommand = new("SELECT pg_advisory_xact_lock($1)", connection, transaction))
            {
                lockCommand.Parameters.Add(new NpgsqlParameter { Value = lockKey });
                await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            decimal currentReservedRisk = await GetActiveReservedRiskAsync(
                connection, transaction, command.BrokerAccountId, cancellationToken).ConfigureAwait(false);

            List<CandidateAdmissionOutcome> outcomes = new(command.Candidates.Count);
            DateTimeOffset now = DateTimeOffset.UtcNow;

            foreach (PortfolioEpochCandidate candidate in command.Candidates.OrderBy(c => c.Rank))
            {
                Guid portfolioDecisionId = Guid.NewGuid();
                decimal projected = currentReservedRisk + candidate.RequestedRisk;
                bool approved = projected <= command.MaxAccountReservedRisk;
                string reasonCode = approved ? "admitted" : "risk_ceiling_exceeded";
                Guid? reservationId = null;

                await InsertPortfolioDecisionAsync(
                    connection, transaction, portfolioDecisionId, command, candidate, approved, reasonCode, now,
                    cancellationToken).ConfigureAwait(false);

                if (approved)
                {
                    reservationId = Guid.NewGuid();
                    await InsertReservationAsync(
                        connection, transaction, reservationId.Value, candidate, command.BrokerAccountId,
                        portfolioDecisionId, now, command.ReservationTtl, cancellationToken).ConfigureAwait(false);
                    currentReservedRisk = projected;
                }

                outcomes.Add(new CandidateAdmissionOutcome
                {
                    CandidateId = candidate.CandidateId,
                    Approved = approved,
                    ReservationId = reservationId,
                    ReasonCode = reasonCode
                });
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new PortfolioAdmissionResult { Result = DurableResult.Committed(), Outcomes = outcomes };
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new PortfolioAdmissionResult
            {
                Result = DurableResult.Duplicate("epoch_already_committed"),
                Outcomes = []
            };
        }
    }

    public async Task<DurableResult> TransitionReservationAsync(
        TransitionReservation command, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        ReservationEntity? reservation = await context.Set<ReservationEntity>()
            .FirstOrDefaultAsync(r => r.ReservationId == command.ReservationId, cancellationToken)
            .ConfigureAwait(false);
        if (reservation is null)
            return DurableResult.PermanentFailure("reservation_not_found");

        ReservationState fromState = reservation.State;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        reservation.State = command.ToState;
        reservation.UpdatedAt = now;
        reservation.Version += 1;

        context.Add(new ReservationEventEntity
        {
            ReservationId = command.ReservationId,
            OccurredAt = now,
            FromState = fromState,
            ToState = command.ToState,
            ReasonCode = command.ReasonCode,
            RelatedOrderId = command.RelatedOrderId,
            DetailsJson = command.DetailsJson
        });

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return DurableResult.Committed();
    }

    public async Task<IReadOnlyList<ReservationDetail>> GetActiveReservationsAsync(
        Guid brokerAccountId, CancellationToken cancellationToken)
    {
        await using TradingHubDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        List<ReservationEntity> reservations = await context.Set<ReservationEntity>()
            .AsNoTracking()
            .Where(r => r.BrokerAccountId == brokerAccountId && ActiveStates.Contains(r.State))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return reservations.Select(r => new ReservationDetail
        {
            ReservationId = r.ReservationId,
            CandidateId = r.CandidateId,
            BrokerAccountId = r.BrokerAccountId,
            State = r.State,
            ReservedRisk = r.ReservedRisk,
            ReservedMargin = r.ReservedMargin,
            ReservedQuantity = r.ReservedQuantity,
            CreatedAt = r.CreatedAt,
            ExpiresAt = r.ExpiresAt,
            Version = r.Version
        }).ToArray();
    }

    private static async Task<decimal> GetActiveReservedRiskAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid brokerAccountId,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(
            "SELECT COALESCE(SUM(reserved_risk), 0) FROM risk.reservations " +
            "WHERE broker_account_id = $1 AND state = ANY($2)",
            connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = brokerAccountId });
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = ActiveStates.Select(s => (short)s).ToArray(),
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Smallint
        });
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return (decimal)(result ?? 0m);
    }

    private static async Task InsertPortfolioDecisionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid portfolioDecisionId,
        CommitPortfolioEpoch epoch, PortfolioEpochCandidate candidate, bool approved, string reasonCode,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(
            """
            INSERT INTO risk.portfolio_decisions
                (portfolio_decision_id, decision_epoch, broker_account_id, candidate_id, rank,
                 requested_risk, approved_risk, requested_quantity, approved_quantity, estimated_margin,
                 approved, reason_code, portfolio_snapshot, created_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13::jsonb, $14)
            """,
            connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = portfolioDecisionId });
        command.Parameters.Add(new NpgsqlParameter { Value = epoch.DecisionEpoch, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        command.Parameters.Add(new NpgsqlParameter { Value = epoch.BrokerAccountId });
        command.Parameters.Add(new NpgsqlParameter { Value = candidate.CandidateId });
        command.Parameters.Add(new NpgsqlParameter { Value = candidate.Rank });
        command.Parameters.Add(new NpgsqlParameter { Value = candidate.RequestedRisk });
        command.Parameters.Add(new NpgsqlParameter { Value = approved ? candidate.RequestedRisk : 0m });
        command.Parameters.Add(new NpgsqlParameter { Value = candidate.RequestedQuantity });
        command.Parameters.Add(new NpgsqlParameter { Value = approved ? candidate.RequestedQuantity : 0m });
        command.Parameters.Add(new NpgsqlParameter { Value = candidate.EstimatedMargin });
        command.Parameters.Add(new NpgsqlParameter { Value = approved });
        command.Parameters.Add(new NpgsqlParameter { Value = reasonCode });
        command.Parameters.Add(new NpgsqlParameter { Value = candidate.PortfolioSnapshotJson });
        command.Parameters.Add(new NpgsqlParameter { Value = now, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertReservationAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid reservationId,
        PortfolioEpochCandidate candidate, Guid brokerAccountId, Guid portfolioDecisionId, DateTimeOffset now,
        TimeSpan reservationTtl, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(
            """
            INSERT INTO risk.reservations
                (reservation_id, candidate_id, broker_account_id, portfolio_decision_id, state,
                 reserved_risk, reserved_margin, reserved_quantity, created_at, expires_at, updated_at, version)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12)
            """,
            connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = reservationId });
        command.Parameters.Add(new NpgsqlParameter { Value = candidate.CandidateId });
        command.Parameters.Add(new NpgsqlParameter { Value = brokerAccountId });
        command.Parameters.Add(new NpgsqlParameter { Value = portfolioDecisionId });
        command.Parameters.Add(new NpgsqlParameter { Value = (short)ReservationState.Created });
        command.Parameters.Add(new NpgsqlParameter { Value = candidate.RequestedRisk });
        command.Parameters.Add(new NpgsqlParameter { Value = candidate.EstimatedMargin });
        command.Parameters.Add(new NpgsqlParameter { Value = candidate.RequestedQuantity });
        command.Parameters.Add(new NpgsqlParameter { Value = now, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        command.Parameters.Add(new NpgsqlParameter { Value = now + reservationTtl, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        command.Parameters.Add(new NpgsqlParameter { Value = now, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        command.Parameters.Add(new NpgsqlParameter { Value = 1L });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
