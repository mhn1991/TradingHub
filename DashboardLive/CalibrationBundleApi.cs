using TradingPolicies;

namespace Dashboard.Live;

public sealed record ApproveCalibrationBundleRequest
{
    public required string ApprovedBy { get; init; }
}

public sealed record RejectCalibrationBundleRequest
{
    public required string RejectedBy { get; init; }
    public required string Reason { get; init; }
}

/// <summary>
/// Review surface for <see cref="CalibrationBundleCandidate"/>s: list pending candidates, and
/// explicitly approve or reject them. This is the replacement for today's fully-manual
/// "operator pastes JSON into host config" promotion step - approving here persists the final
/// <see cref="TradingPolicyProfileStatus.ApprovedForDemo"/> profile via
/// <see cref="ITradingPolicyProfileStore"/>, but does **not** activate it into any running live
/// host; that remains a separate, explicit step.
/// </summary>
public static class CalibrationBundleApi
{
    public static void MapCalibrationBundleEndpoints(this WebApplication app)
    {
        app.MapGet("/api/calibration-candidates", async (
            string? status,
            ICalibrationBundleApprovalStore store,
            CancellationToken cancellationToken) =>
        {
            CalibrationBundleCandidateStatus? filter = null;
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!Enum.TryParse(status, ignoreCase: true, out CalibrationBundleCandidateStatus parsed))
                    return Results.BadRequest(new { error = $"Unknown status '{status}'.", code = "InvalidStatus" });
                filter = parsed;
            }
            IReadOnlyList<CalibrationBundleCandidate> candidates = await store
                .ListAsync(filter, take: 100, cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(candidates);
        });

        app.MapGet("/api/calibration-candidates/{id:guid}", async (
            Guid id, ICalibrationBundleApprovalStore store, CancellationToken cancellationToken) =>
        {
            CalibrationBundleCandidate? candidate = await store.GetAsync(id, cancellationToken).ConfigureAwait(false);
            return candidate is null ? Results.NotFound() : Results.Ok(candidate);
        });

        app.MapPost("/api/calibration-candidates/{id:guid}/approve", async (
            Guid id,
            ApproveCalibrationBundleRequest body,
            ICalibrationBundleApprovalStore store,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(body.ApprovedBy))
                return Results.BadRequest(new { error = "ApprovedBy is required.", code = "MissingApprover" });
            try
            {
                CalibrationBundleCandidate approved = await store
                    .ApproveAsync(id, body.ApprovedBy, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Ok(approved);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message, code = "CandidateNotPendingReview" });
            }
        });

        app.MapPost("/api/calibration-candidates/{id:guid}/reject", async (
            Guid id,
            RejectCalibrationBundleRequest body,
            ICalibrationBundleApprovalStore store,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(body.RejectedBy) || string.IsNullOrWhiteSpace(body.Reason))
                return Results.BadRequest(new { error = "RejectedBy and Reason are required.", code = "MissingRejectionDetail" });
            try
            {
                CalibrationBundleCandidate rejected = await store
                    .RejectAsync(id, body.RejectedBy, body.Reason, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Ok(rejected);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message, code = "CandidateNotPendingReview" });
            }
        });

        app.MapGet("/api/trading-policy-profiles", async (
            string? strategyId,
            ITradingPolicyProfileStore store,
            CancellationToken cancellationToken) =>
        {
            IReadOnlyList<TradingPolicyProfile> profiles = await store
                .ListAsync(strategyId, take: 100, cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(profiles);
        });
    }
}
