using System.Security.Cryptography;
using System.Text;
using LiveTrading.Portfolio;
using PortfolioManager.Risk;

namespace LiveTrading.ManualApproval;

public enum ManualApprovalState
{
    Pending,
    Approved,
    Rejected,
    Expired,
    Consumed
}

public sealed record ManualApprovalCandidate
{
    public required PortfolioApprovedDecision Decision { get; init; }
    public required string CandidateFingerprint { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required ManualApprovalState State { get; init; }
    public string? ReviewedBy { get; init; }
    public DateTimeOffset? ReviewedAt { get; init; }
    public string? ReviewReason { get; init; }
}

public sealed record ManualApprovalRequest
{
    public required string CandidateId { get; init; }
    public required string CandidateFingerprint { get; init; }
    public required string ApprovedBy { get; init; }
}

public interface IManualApprovalStore
{
    ManualApprovalCandidate Add(PortfolioApprovedDecision decision, TimeSpan lifetime);
    ManualApprovalCandidate Approve(ManualApprovalRequest request);
    ManualApprovalCandidate Reject(string candidateId, string reviewedBy, string reason);
    bool TryConsume(string candidateId, string fingerprint, out PortfolioApprovedDecision decision);
    void Restore(IReadOnlyList<ManualApprovalCandidate> candidates);
    IReadOnlyList<ManualApprovalCandidate> Snapshot { get; }
}

/// <summary>In-memory approval gate with fingerprint, expiry, and exactly-once consumption.</summary>
public sealed class ManualApprovalStore(
    TimeProvider timeProvider,
    IPortfolioReservationBook reservations) : IManualApprovalStore
{
    private readonly object _sync = new();
    private readonly Dictionary<string, ManualApprovalCandidate> _items = new(StringComparer.Ordinal);

    public ManualApprovalCandidate Add(PortfolioApprovedDecision decision, TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (lifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lifetime));
        string fingerprint = Fingerprint(decision);
        var item = new ManualApprovalCandidate
        {
            Decision = decision,
            CandidateFingerprint = fingerprint,
            ExpiresAt = timeProvider.GetUtcNow() + lifetime,
            State = ManualApprovalState.Pending
        };
        lock (_sync)
        {
            if (!_items.TryAdd(decision.Candidate.CandidateId, item))
                throw new InvalidOperationException($"Candidate '{decision.Candidate.CandidateId}' already exists.");
        }
        return item;
    }

    public ManualApprovalCandidate Approve(ManualApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CandidateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CandidateFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ApprovedBy);
        lock (_sync)
        {
            DateTimeOffset reviewedAt = timeProvider.GetUtcNow();
            ManualApprovalCandidate item = GetCurrent(request.CandidateId, reviewedAt);
            if (!string.Equals(item.CandidateFingerprint, request.CandidateFingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("The candidate fingerprint no longer matches the approval request.");
            if (item.State != ManualApprovalState.Pending)
                throw new InvalidOperationException($"Candidate is {item.State}, not pending.");
            item = item with
            {
                State = ManualApprovalState.Approved,
                ReviewedBy = request.ApprovedBy,
                ReviewedAt = reviewedAt
            };
            _items[request.CandidateId] = item;
            return item;
        }
    }

    public ManualApprovalCandidate Reject(string candidateId, string reviewedBy, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewedBy);
        lock (_sync)
        {
            DateTimeOffset reviewedAt = timeProvider.GetUtcNow();
            ManualApprovalCandidate item = GetCurrent(candidateId, reviewedAt);
            if (item.State is not (ManualApprovalState.Pending or ManualApprovalState.Approved))
                return item;
            item = item with
            {
                State = ManualApprovalState.Rejected,
                ReviewedBy = reviewedBy,
                ReviewedAt = reviewedAt,
                ReviewReason = reason
            };
            _items[candidateId] = item;
            reservations.Release(item.Decision.ReservationId, PortfolioReleaseReason.Manual);
            return item;
        }
    }

    public bool TryConsume(string candidateId, string fingerprint, out PortfolioApprovedDecision decision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        lock (_sync)
        {
            ManualApprovalCandidate item = GetCurrent(candidateId, timeProvider.GetUtcNow());
            if (item.State != ManualApprovalState.Approved ||
                !string.Equals(item.CandidateFingerprint, fingerprint, StringComparison.Ordinal))
            {
                decision = null!;
                return false;
            }
            _items[candidateId] = item with { State = ManualApprovalState.Consumed };
            decision = item.Decision;
            return true;
        }
    }

    public void Restore(IReadOnlyList<ManualApprovalCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        lock (_sync)
        {
            _items.Clear();
            foreach (ManualApprovalCandidate item in candidates)
            {
                if (item.State is ManualApprovalState.Pending or ManualApprovalState.Approved)
                    _items[item.Decision.Candidate.CandidateId] = item;
            }
        }
        _ = Snapshot; // expire stale entries using the server clock and release their reservations
    }

    public IReadOnlyList<ManualApprovalCandidate> Snapshot
    {
        get
        {
            lock (_sync)
            {
                DateTimeOffset now = timeProvider.GetUtcNow();
                foreach ((string id, ManualApprovalCandidate item) in _items.ToArray())
                {
                    if (IsExpirable(item.State) && item.ExpiresAt <= now)
                    {
                        _items[id] = item with { State = ManualApprovalState.Expired };
                        reservations.Release(item.Decision.ReservationId, PortfolioReleaseReason.Expired);
                    }
                }
                return _items.Values
                    .OrderByDescending(i => i.Decision.Candidate.DecisionTime)
                    .ToArray();
            }
        }
    }

    private ManualApprovalCandidate GetCurrent(string candidateId, DateTimeOffset now)
    {
        if (!_items.TryGetValue(candidateId, out ManualApprovalCandidate? item))
            throw new KeyNotFoundException($"Candidate '{candidateId}' was not found.");
        if (IsExpirable(item.State) && item.ExpiresAt <= now)
        {
            item = item with { State = ManualApprovalState.Expired };
            _items[candidateId] = item;
            reservations.Release(item.Decision.ReservationId, PortfolioReleaseReason.Expired);
        }
        return item;
    }

    private static bool IsExpirable(ManualApprovalState state) =>
        state is ManualApprovalState.Pending or ManualApprovalState.Approved;

    public static string Fingerprint(PortfolioApprovedDecision decision)
    {
        string canonical = string.Join("|",
            decision.Candidate.CandidateId,
            decision.Candidate.DecisionId,
            decision.Candidate.Instrument.Value,
            decision.Candidate.Action,
            decision.Decision.SuggestedQuantity,
            decision.Decision.ReferencePrice,
            decision.Decision.StopLossPrice,
            decision.Decision.TakeProfitPrice,
            decision.ReservationId,
            decision.PolicyBundleId,
            decision.PolicyRevision,
            decision.ConfigurationHash);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
