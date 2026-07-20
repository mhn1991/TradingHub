using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DBManager.Postgres;
using DBManager.Postgres.Operations;
using LiveTrading.ManualApproval;
using LiveTrading.Portfolio;
using Microsoft.EntityFrameworkCore;
using PortfolioManager.Risk;

namespace TradingHub.Persistence.Postgres.Live;

/// <summary>Concurrency-checked PostgreSQL approval gate with an append-only transition audit.</summary>
public sealed class PostgresManualApprovalStore(
    IDbContextFactory<TradingHubDbContext> contextFactory,
    TimeProvider timeProvider,
    IPortfolioReservationBook reservations) : IManualApprovalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly object _sync = new();

    public ManualApprovalCandidate Add(PortfolioApprovedDecision decision, TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (lifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lifetime));
        lock (_sync)
        {
            using TradingHubDbContext context = contextFactory.CreateDbContext();
            string candidateId = decision.Candidate.CandidateId;
            if (context.ManualApprovalCandidates.Any(row => row.CandidateId == candidateId))
                throw new InvalidOperationException($"Candidate '{candidateId}' already exists.");
            var candidate = new ManualApprovalCandidate
            {
                Decision = decision,
                CandidateFingerprint = ManualApprovalStore.Fingerprint(decision),
                ExpiresAt = timeProvider.GetUtcNow() + lifetime,
                State = ManualApprovalState.Pending
            };
            context.ManualApprovalCandidates.Add(ToEntity(candidateId, candidate, 1));
            AddEvent(context, candidateId, ManualApprovalState.Pending, ManualApprovalState.Pending, 1, null, "created");
            context.SaveChanges();
            return candidate;
        }
    }

    public ManualApprovalCandidate Approve(ManualApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CandidateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CandidateFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ApprovedBy);
        lock (_sync)
        {
            using TradingHubDbContext context = contextFactory.CreateDbContext();
            ManualApprovalCandidateEntity row = GetCurrent(context, request.CandidateId);
            if (!string.Equals(row.CandidateFingerprint, request.CandidateFingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("The candidate fingerprint no longer matches the approval request.");
            if ((ManualApprovalState)row.State != ManualApprovalState.Pending)
                throw new InvalidOperationException($"Candidate is {(ManualApprovalState)row.State}, not pending.");
            DateTimeOffset now = timeProvider.GetUtcNow();
            Transition(context, row, ManualApprovalState.Approved, request.ApprovedBy, null, now);
            context.SaveChanges();
            return FromEntity(row);
        }
    }

    public ManualApprovalCandidate Reject(string candidateId, string reviewedBy, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewedBy);
        lock (_sync)
        {
            using TradingHubDbContext context = contextFactory.CreateDbContext();
            ManualApprovalCandidateEntity row = GetCurrent(context, candidateId);
            ManualApprovalState state = (ManualApprovalState)row.State;
            if (state is not (ManualApprovalState.Pending or ManualApprovalState.Approved))
                return FromEntity(row);
            Transition(context, row, ManualApprovalState.Rejected, reviewedBy, reason, timeProvider.GetUtcNow());
            context.SaveChanges();
            reservations.Release(row.ReservationId, PortfolioReleaseReason.Manual);
            return FromEntity(row);
        }
    }

    public bool TryConsume(string candidateId, string fingerprint, out PortfolioApprovedDecision decision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        lock (_sync)
        {
            using TradingHubDbContext context = contextFactory.CreateDbContext();
            using var transaction = context.Database.BeginTransaction();
            ManualApprovalCandidateEntity row = GetCurrent(context, candidateId);
            if ((ManualApprovalState)row.State != ManualApprovalState.Approved ||
                !string.Equals(row.CandidateFingerprint, fingerprint, StringComparison.Ordinal))
            {
                decision = null!;
                return false;
            }
            Transition(context, row, ManualApprovalState.Consumed, "runtime", null, timeProvider.GetUtcNow());
            row.ConsumedAt = timeProvider.GetUtcNow();
            context.SaveChanges();
            transaction.Commit();
            decision = FromEntity(row).Decision;
            return true;
        }
    }

    public void Restore(IReadOnlyList<ManualApprovalCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        lock (_sync)
        {
            using TradingHubDbContext context = contextFactory.CreateDbContext();
            foreach (ManualApprovalCandidate candidate in candidates.Where(item =>
                         item.State is ManualApprovalState.Pending or ManualApprovalState.Approved))
            {
                string id = candidate.Decision.Candidate.CandidateId;
                if (context.ManualApprovalCandidates.Any(row => row.CandidateId == id)) continue;
                context.ManualApprovalCandidates.Add(ToEntity(id, candidate, 1));
                AddEvent(context, id, candidate.State, candidate.State, 1, "checkpoint-import", "restored");
            }
            context.SaveChanges();
        }
        _ = Snapshot;
    }

    public IReadOnlyList<ManualApprovalCandidate> Snapshot
    {
        get
        {
            lock (_sync)
            {
                using TradingHubDbContext context = contextFactory.CreateDbContext();
                ManualApprovalCandidateEntity[] active = context.ManualApprovalCandidates
                    .Where(row => row.State == (short)ManualApprovalState.Pending ||
                                  row.State == (short)ManualApprovalState.Approved)
                    .OrderByDescending(row => row.ExpiresAt)
                    .ToArray();
                foreach (ManualApprovalCandidateEntity row in active.Where(row => row.ExpiresAt <= timeProvider.GetUtcNow()))
                    Expire(context, row);
                context.SaveChanges();
                return context.ManualApprovalCandidates.AsNoTracking()
                    .OrderByDescending(row => row.ExpiresAt)
                    .AsEnumerable()
                    .Select(FromEntity)
                    .ToArray();
            }
        }
    }

    private ManualApprovalCandidateEntity GetCurrent(TradingHubDbContext context, string candidateId)
    {
        ManualApprovalCandidateEntity row = context.ManualApprovalCandidates
            .SingleOrDefault(item => item.CandidateId == candidateId)
            ?? throw new KeyNotFoundException($"Candidate '{candidateId}' was not found.");
        if (row.ExpiresAt <= timeProvider.GetUtcNow() &&
            (ManualApprovalState)row.State is ManualApprovalState.Pending or ManualApprovalState.Approved)
        {
            Expire(context, row);
            context.SaveChanges();
        }
        return row;
    }

    private void Expire(TradingHubDbContext context, ManualApprovalCandidateEntity row)
    {
        Transition(context, row, ManualApprovalState.Expired, "system", "expired", timeProvider.GetUtcNow());
        reservations.Release(row.ReservationId, PortfolioReleaseReason.Expired);
    }

    private static void Transition(
        TradingHubDbContext context,
        ManualApprovalCandidateEntity row,
        ManualApprovalState next,
        string? actor,
        string? reason,
        DateTimeOffset occurredAt)
    {
        ManualApprovalState previous = (ManualApprovalState)row.State;
        row.State = (short)next;
        row.ReviewedBy = actor;
        row.ReviewedAt = occurredAt;
        row.ReviewReason = reason;
        row.Revision++;
        ManualApprovalCandidate candidate = FromEntity(row);
        row.CandidateJson = JsonSerializer.Serialize(candidate, JsonOptions);
        AddEvent(context, row.CandidateId, previous, next, row.Revision, actor, reason);
    }

    private static void AddEvent(
        TradingHubDbContext context,
        string candidateId,
        ManualApprovalState previous,
        ManualApprovalState next,
        long revision,
        string? actor,
        string? reason) => context.ManualApprovalEvents.Add(new ManualApprovalEventEntity
        {
            CandidateId = candidateId,
            FromState = (short)previous,
            ToState = (short)next,
            OccurredAt = DateTimeOffset.UtcNow,
            Actor = actor,
            Reason = reason,
            CandidateRevision = revision
        });

    private static ManualApprovalCandidateEntity ToEntity(
        string id,
        ManualApprovalCandidate candidate,
        long revision) => new()
    {
        CandidateId = id,
        CandidateFingerprint = candidate.CandidateFingerprint,
        ReservationId = candidate.Decision.ReservationId,
        PolicyRevision = candidate.Decision.PolicyRevision.ToString(CultureInfo.InvariantCulture),
        ConfigurationHash = candidate.Decision.ConfigurationHash,
        CandidateJson = JsonSerializer.Serialize(candidate, JsonOptions),
        State = (short)candidate.State,
        ExpiresAt = candidate.ExpiresAt,
        ReviewedBy = candidate.ReviewedBy,
        ReviewedAt = candidate.ReviewedAt,
        ReviewReason = candidate.ReviewReason,
        Revision = revision
    };

    private static ManualApprovalCandidate FromEntity(ManualApprovalCandidateEntity row)
    {
        ManualApprovalCandidate candidate = JsonSerializer.Deserialize<ManualApprovalCandidate>(row.CandidateJson, JsonOptions)
            ?? throw new InvalidDataException($"Manual approval '{row.CandidateId}' has an empty payload.");
        return candidate with
        {
            CandidateFingerprint = row.CandidateFingerprint,
            State = (ManualApprovalState)row.State,
            ExpiresAt = row.ExpiresAt,
            ReviewedBy = row.ReviewedBy,
            ReviewedAt = row.ReviewedAt,
            ReviewReason = row.ReviewReason
        };
    }
}
