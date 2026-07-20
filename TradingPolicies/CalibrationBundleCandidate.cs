using System.Text.Json;
using RiskManager.Calibration;
using Simulator.Calibration;
using TradeManager;

namespace TradingPolicies;

public enum CalibrationBundleCandidateStatus
{
    PendingReview,
    Approved,
    Rejected,
    Superseded
}

/// <summary>
/// One compatible (setup, meta-model, management) artifact triple proposed by
/// <c>QuantResearch.Training</c>'s pipeline, together with the draft <see cref="TradingPolicyProfile"/>
/// it would become. Nothing in this repository ever auto-approves a candidate - it always starts
/// <see cref="CalibrationBundleCandidateStatus.PendingReview"/>, and only an explicit
/// <see cref="ICalibrationBundleApprovalStore.ApproveAsync"/> call (a human/operator action)
/// turns it into an actual <see cref="TradingPolicyProfileStatus.ApprovedForDemo"/> profile that
/// live execution is allowed to use.
/// </summary>
public sealed record CalibrationBundleCandidate
{
    public required Guid Id { get; init; }
    public required Guid SetupArtifactId { get; init; }
    public required Guid MetaModelArtifactId { get; init; }
    public required Guid ManagementArtifactId { get; init; }
    /// <summary>Draft profile (<see cref="TradingPolicyProfileStatus.Research"/>) - becomes the real, persisted, approved profile only once <see cref="ICalibrationBundleApprovalStore.ApproveAsync"/> is called.</summary>
    public required TradingPolicyProfile ProposedProfile { get; init; }
    public required CalibrationBundleCandidateStatus Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public string? ReviewedBy { get; init; }
    public DateTimeOffset? ReviewedAt { get; init; }
    public string? RejectionReason { get; init; }
    /// <summary>Set once approved - the id/revision of the final, persisted profile (may differ in Revision from <see cref="ProposedProfile"/>, which is recomputed at approval time against the latest approved profile for the strategy).</summary>
    public Guid? ApprovedProfileId { get; init; }
    public int? ApprovedProfileRevision { get; init; }
}

public interface ICalibrationBundleApprovalStore
{
    Task<CalibrationBundleCandidate> AddAsync(
        Guid setupArtifactId,
        Guid metaModelArtifactId,
        Guid managementArtifactId,
        TradingPolicyProfile proposedProfile,
        CancellationToken cancellationToken = default);

    Task<CalibrationBundleCandidate> ApproveAsync(
        Guid candidateId, string approvedBy, CancellationToken cancellationToken = default);

    Task<CalibrationBundleCandidate> RejectAsync(
        Guid candidateId, string rejectedBy, string reason, CancellationToken cancellationToken = default);

    Task<CalibrationBundleCandidate?> GetAsync(Guid candidateId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CalibrationBundleCandidate>> ListAsync(
        CalibrationBundleCandidateStatus? filter = null, int take = 50, CancellationToken cancellationToken = default);
}

/// <summary>
/// File-backed <see cref="ICalibrationBundleApprovalStore"/>. On <see cref="ApproveAsync"/>:
/// marks all three underlying artifacts <see cref="CalibrationPromotionStatus.Approved"/> via
/// <see cref="ICalibrationArtifactRepository.UpdatePromotionStatusAsync"/>, computes the real
/// next revision against <see cref="ITradingPolicyProfileStore.GetLatestApprovedAsync"/> for the
/// strategy, and persists the final <see cref="TradingPolicyProfileStatus.ApprovedForDemo"/>
/// profile. Nothing here activates the profile into live execution - that is a separate,
/// explicit step (see <c>LiveTradingRuntimeCoordinator.ActivatePolicyRevisionAsync</c>).
/// </summary>
public sealed class FileCalibrationBundleApprovalStore : ICalibrationBundleApprovalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly string _root;
    private readonly ICalibrationArtifactRepository _artifacts;
    private readonly ITradingPolicyProfileStore _profiles;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileCalibrationBundleApprovalStore(
        string rootDirectory,
        ICalibrationArtifactRepository artifacts,
        ITradingPolicyProfileStore profiles,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new ArgumentException("Calibration bundle candidate directory is required.", nameof(rootDirectory));
        _root = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(_root);
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<CalibrationBundleCandidate> AddAsync(
        Guid setupArtifactId,
        Guid metaModelArtifactId,
        Guid managementArtifactId,
        TradingPolicyProfile proposedProfile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposedProfile);
        proposedProfile.Validate();
        if (proposedProfile.Status != TradingPolicyProfileStatus.Research)
        {
            throw new ArgumentException(
                "A freshly-proposed bundle candidate's draft profile must be Research status - promotion to ApprovedForDemo only happens through ApproveAsync.",
                nameof(proposedProfile));
        }

        var candidate = new CalibrationBundleCandidate
        {
            Id = Guid.NewGuid(),
            SetupArtifactId = setupArtifactId,
            MetaModelArtifactId = metaModelArtifactId,
            ManagementArtifactId = managementArtifactId,
            ProposedProfile = proposedProfile,
            Status = CalibrationBundleCandidateStatus.PendingReview,
            CreatedAt = _timeProvider.GetUtcNow()
        };
        await WriteAsync(candidate, cancellationToken).ConfigureAwait(false);
        return candidate;
    }

    public async Task<CalibrationBundleCandidate> ApproveAsync(
        Guid candidateId, string approvedBy, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedBy);
        CalibrationBundleCandidate candidate = await RequireAsync(candidateId, cancellationToken).ConfigureAwait(false);
        if (candidate.Status != CalibrationBundleCandidateStatus.PendingReview)
            throw new InvalidOperationException($"Candidate {candidateId:N} is {candidate.Status}, not PendingReview.");

        TradingPolicyProfile? latestApproved = await _profiles
            .GetLatestApprovedAsync(candidate.ProposedProfile.StrategyId, cancellationToken)
            .ConfigureAwait(false);
        int revision = (latestApproved?.Revision ?? 0) + 1;
        DateTimeOffset now = _timeProvider.GetUtcNow();

        TradingPolicyProfile approvedProfile = TradingPolicyProfile.Create(
            candidate.ProposedProfile.ProfileId,
            revision,
            candidate.ProposedProfile.StrategyId,
            candidate.ProposedProfile.StrategyVersion,
            candidate.ProposedProfile.EffectiveAgentDefinition(),
            TradingPolicyProfileStatus.ApprovedForDemo,
            candidate.ProposedProfile.FeaturePolicy,
            candidate.ProposedProfile.PositionSizing,
            candidate.ProposedProfile.AdaptiveRisk,
            candidate.ProposedProfile.PortfolioRisk,
            candidate.ProposedProfile.CorrelationRisk,
            candidate.ProposedProfile.TradingConditions,
            candidate.ProposedProfile.AccountSafety,
            candidate.ProposedProfile.LegacyManagement,
            candidate.ProposedProfile.ImprovedManagement,
            candidate.ProposedProfile.StructuralManagement,
            candidate.ProposedProfile.RegimeManagement,
            candidate.ProposedProfile.ManagementCalibration,
            candidate.ProposedProfile.MetaModelPolicy,
            now,
            candidate.SetupArtifactId,
            candidate.ManagementArtifactId,
            candidate.MetaModelArtifactId,
            candidate.ProposedProfile.Description);

        await _profiles.StoreAsync(approvedProfile, cancellationToken).ConfigureAwait(false);
        await MarkArtifactsAsync(candidate, CalibrationPromotionStatus.Approved, cancellationToken).ConfigureAwait(false);

        CalibrationBundleCandidate updated = candidate with
        {
            Status = CalibrationBundleCandidateStatus.Approved,
            ReviewedBy = approvedBy,
            ReviewedAt = now,
            ApprovedProfileId = approvedProfile.ProfileId,
            ApprovedProfileRevision = approvedProfile.Revision
        };
        await WriteAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task<CalibrationBundleCandidate> RejectAsync(
        Guid candidateId, string rejectedBy, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rejectedBy);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        CalibrationBundleCandidate candidate = await RequireAsync(candidateId, cancellationToken).ConfigureAwait(false);
        if (candidate.Status != CalibrationBundleCandidateStatus.PendingReview)
            throw new InvalidOperationException($"Candidate {candidateId:N} is {candidate.Status}, not PendingReview.");

        await MarkArtifactsAsync(candidate, CalibrationPromotionStatus.Rejected, cancellationToken).ConfigureAwait(false);
        CalibrationBundleCandidate updated = candidate with
        {
            Status = CalibrationBundleCandidateStatus.Rejected,
            ReviewedBy = rejectedBy,
            ReviewedAt = _timeProvider.GetUtcNow(),
            RejectionReason = reason
        };
        await WriteAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task<CalibrationBundleCandidate?> GetAsync(Guid candidateId, CancellationToken cancellationToken = default)
    {
        string path = GetPath(candidateId);
        if (!File.Exists(path))
            return null;
        await using FileStream stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<CalibrationBundleCandidate>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CalibrationBundleCandidate>> ListAsync(
        CalibrationBundleCandidateStatus? filter = null, int take = 50, CancellationToken cancellationToken = default)
    {
        if (take < 1)
            throw new ArgumentOutOfRangeException(nameof(take));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var items = new List<CalibrationBundleCandidate>();
            foreach (string path in Directory.EnumerateFiles(_root, "*.json").OrderByDescending(File.GetLastWriteTimeUtc))
            {
                cancellationToken.ThrowIfCancellationRequested();
                CalibrationBundleCandidate? candidate;
                try
                {
                    await using FileStream stream = File.OpenRead(path);
                    candidate = await JsonSerializer.DeserializeAsync<CalibrationBundleCandidate>(stream, JsonOptions, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (JsonException)
                {
                    continue;
                }
                if (candidate is null || (filter is not null && candidate.Status != filter))
                    continue;
                items.Add(candidate);
                if (items.Count >= take)
                    break;
            }
            return items;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<CalibrationBundleCandidate> RequireAsync(Guid candidateId, CancellationToken cancellationToken) =>
        await GetAsync(candidateId, cancellationToken).ConfigureAwait(false)
        ?? throw new KeyNotFoundException($"Calibration bundle candidate {candidateId:N} was not found.");

    private async Task MarkArtifactsAsync(
        CalibrationBundleCandidate candidate, CalibrationPromotionStatus status, CancellationToken cancellationToken)
    {
        await _artifacts.UpdatePromotionStatusAsync(candidate.SetupArtifactId, status, cancellationToken).ConfigureAwait(false);
        await _artifacts.UpdatePromotionStatusAsync(candidate.MetaModelArtifactId, status, cancellationToken).ConfigureAwait(false);
        await _artifacts.UpdatePromotionStatusAsync(candidate.ManagementArtifactId, status, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteAsync(CalibrationBundleCandidate candidate, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = GetPath(candidate.Id);
            string temporary = path + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await using (FileStream stream = new(
                    temporary, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 64 * 1024, useAsync: true))
                {
                    await JsonSerializer.SerializeAsync(stream, candidate, JsonOptions, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private string GetPath(Guid candidateId) => Path.Combine(_root, $"{candidateId:N}.json");
}
