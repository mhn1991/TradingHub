using LiveTrading.ManualApproval;
using LiveTrading.Oanda;
using LiveTrading.Registry;
using LiveTrading.Reconciliation;
using LiveTrading.Runtime;
using LiveTradingHost.Status;

namespace LiveTradingHost.Api;

public sealed record LivePauseRequest
{
    public string Reason { get; init; } = "Operator pause";
}

public sealed record LiveApprovalRequest
{
    public required string CandidateFingerprint { get; init; }
    public required string ApprovedBy { get; init; }
}

public sealed record LiveRejectionRequest
{
    public required string ReviewedBy { get; init; }
    public string Reason { get; init; } = "Operator rejected";
}

public sealed record LivePositionReductionRequest
{
    public required decimal Quantity { get; init; }
    public required DateTimeOffset ExpectedPositionUpdatedAt { get; init; }
    public string Reason { get; init; } = "Operator reduction";
}

public sealed record LivePositionCloseRequest
{
    public required DateTimeOffset ExpectedPositionUpdatedAt { get; init; }
    public string Reason { get; init; } = "Operator full close";
}

public sealed record LivePositionStopRequest
{
    public required decimal StopPrice { get; init; }
    public required DateTimeOffset ExpectedPositionUpdatedAt { get; init; }
    public string Reason { get; init; } = "Operator stop amendment";
}

public static class LiveStatusApi
{
    public static void MapLiveStatusEndpoints(this WebApplication app)
    {
        app.MapGet("/api/live-host/status", (LiveEngineState state) => Results.Ok(state.Snapshot()));
        app.MapGet("/api/live/status", (LiveEngineState state) => Results.Ok(state.Snapshot()));
        app.MapGet("/api/live/health", (LiveEngineState state) =>
        {
            LiveEngineStatusDto status = state.Snapshot();
            bool healthy = status.EngineState == "Running" &&
                status.Connections.RestReachable &&
                status.Connections.QuoteStreamConnected &&
                status.Connections.LeaseState == "Acquired";
            return Results.Ok(new { Healthy = healthy, Status = status });
        });

        app.MapGet("/api/live/runtime", (ILiveTradingRuntimeCoordinator runtime) =>
            Results.Ok(runtime.Snapshot));
        app.MapGet("/api/live/candidates", (ILiveTradingRuntimeCoordinator runtime) =>
            Results.Ok(runtime.Snapshot.ManualCandidates.Select(MapCandidate).ToArray()));
        app.MapGet("/api/live/orders", (ILiveTradingRuntimeCoordinator runtime) =>
            Results.Ok(runtime.Snapshot.Registry.Orders.Select(MapOrder).ToArray()));
        app.MapGet("/api/live/positions", (ILiveTradingRuntimeCoordinator runtime) =>
            Results.Ok(runtime.Snapshot.Registry.Positions.Select(MapPosition).ToArray()));
        app.MapGet("/api/live/portfolio", (ILiveTradingRuntimeCoordinator runtime) =>
            Results.Ok(new
            {
                runtime.Snapshot.Account,
                runtime.Snapshot.Reservations,
                runtime.Snapshot.RecentPortfolioDecisions
            }));
        app.MapGet("/api/live/reconciliation", (ILiveTradingRuntimeCoordinator runtime) =>
            Results.Ok(runtime.Snapshot.LastReconciliation));
        app.MapGet("/api/live/capabilities", (LiveExecutionRuntimeOptions options) =>
        {
            LiveBrokerCapabilityMatrix matrix = OandaBrokerClientFactory.CapabilityMatrix;
            return Results.Ok(new LiveCapabilitiesDto
            {
                AtomicStopOnFillImplemented = matrix.AtomicStopOnFill,
                ExplicitFullCloseImplemented = matrix.ReduceOnlyClose,
                PartialCloseImplemented = matrix.PartialClose,
                PartialClosePracticeCertified = matrix.PartialClosePracticeCertified,
                PartialCloseEnabled = options.PartialCloseEnabled,
                DynamicStopReplacementImplemented = matrix.SafeProtectiveStopReplacement,
                DynamicStopReplacementPracticeCertified = matrix.ProtectiveStopReplacementPracticeCertified,
                DynamicStopReplacementEnabled = options.DynamicStopReplacementEnabled
            });
        });

        app.MapPost("/api/live/control/pause", async (
            LivePauseRequest request,
            ILiveTradingRuntimeCoordinator runtime,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(async () =>
            {
                await runtime.PauseAsync(request.Reason, cancellationToken).ConfigureAwait(false);
                return runtime.Snapshot;
            }).ConfigureAwait(false));

        app.MapPost("/api/live/control/resume", async (
            ILiveTradingRuntimeCoordinator runtime,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(async () =>
            {
                await runtime.ResumeAsync(cancellationToken).ConfigureAwait(false);
                return runtime.Snapshot;
            }).ConfigureAwait(false));

        app.MapPost("/api/live/control/reconcile", async (
            ILiveTradingRuntimeCoordinator runtime,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(() => runtime.ReconcileAsync(ReconciliationTrigger.Manual, cancellationToken))
                .ConfigureAwait(false));

        app.MapPost("/api/live/control/cancel-pending", async (
            ILiveTradingRuntimeCoordinator runtime,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(async () =>
            {
                await runtime.CancelPendingEntriesAsync(cancellationToken).ConfigureAwait(false);
                return runtime.Snapshot;
            }).ConfigureAwait(false));

        app.MapPost("/api/live/control/flatten", async (
            LivePauseRequest request,
            ILiveTradingRuntimeCoordinator runtime,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(async () =>
            {
                await runtime.FlattenAsync(request.Reason, cancellationToken).ConfigureAwait(false);
                return runtime.Snapshot;
            }).ConfigureAwait(false));

        app.MapPost("/api/live/candidates/{candidateId}/approve", async (
            string candidateId,
            LiveApprovalRequest request,
            ILiveTradingRuntimeCoordinator runtime,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(() => runtime.ApproveAndExecuteAsync(
                new ManualApprovalRequest
                {
                    CandidateId = candidateId,
                    CandidateFingerprint = request.CandidateFingerprint,
                    ApprovedBy = request.ApprovedBy
                },
                cancellationToken)).ConfigureAwait(false));

        app.MapPost("/api/live/candidates/{candidateId}/reject", async (
            string candidateId,
            LiveRejectionRequest request,
            ILiveTradingRuntimeCoordinator runtime,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(() => runtime.RejectAsync(
                candidateId,
                request.ReviewedBy,
                request.Reason,
                cancellationToken)).ConfigureAwait(false));

        app.MapPost("/api/live/positions/{positionId}/reduce", async (
            string positionId,
            LivePositionReductionRequest request,
            ILiveTradingRuntimeCoordinator runtime,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(() => runtime.ReducePositionAsync(
                positionId,
                request.Quantity,
                request.ExpectedPositionUpdatedAt,
                request.Reason,
                cancellationToken)).ConfigureAwait(false));

        app.MapPost("/api/live/positions/{positionId}/close", async (
            string positionId,
            LivePositionCloseRequest request,
            ILiveTradingRuntimeCoordinator runtime,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(async () =>
            {
                var position = runtime.Snapshot.Registry.Positions.SingleOrDefault(item =>
                    item.PositionId == positionId || item.BrokerTradeId == positionId) ??
                    throw new KeyNotFoundException($"Owned position '{positionId}' was not found.");
                return await runtime.ReducePositionAsync(
                    positionId,
                    position.Quantity,
                    request.ExpectedPositionUpdatedAt,
                    request.Reason,
                    cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false));

        app.MapPost("/api/live/positions/{positionId}/stop", async (
            string positionId,
            LivePositionStopRequest request,
            ILiveTradingRuntimeCoordinator runtime,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(() => runtime.AmendProtectiveStopAsync(
                positionId,
                request.StopPrice,
                request.ExpectedPositionUpdatedAt,
                request.Reason,
                cancellationToken)).ConfigureAwait(false));
    }


    private static LiveManualCandidateDto MapCandidate(ManualApprovalCandidate item) => new()
    {
        CandidateId = item.Decision.Candidate.CandidateId,
        Fingerprint = item.CandidateFingerprint,
        State = item.State.ToString(),
        DecisionTime = item.Decision.Candidate.DecisionTime,
        ExpiresAt = item.ExpiresAt,
        StrategyId = item.Decision.Candidate.StrategyId,
        Instrument = item.Decision.Candidate.Instrument.Value,
        Action = item.Decision.Candidate.Action.ToString(),
        ReferencePrice = item.Decision.Candidate.ReferencePrice,
        StopLossPrice = item.Decision.Decision.StopLossPrice,
        TakeProfitPrice = item.Decision.Decision.TakeProfitPrice,
        Quantity = item.Decision.Decision.SuggestedQuantity ?? 0m,
        EstimatedStopRisk = item.Decision.PositionSizing.EstimatedStopLoss,
        EstimatedMargin = item.Decision.PositionSizing.EstimatedMargin,
        PortfolioScore = item.Decision.PortfolioScore,
        MetaProbability = item.Decision.Candidate.MetaLabel.Probability,
        CombinedRiskMultiplier = item.Decision.RiskBudget.CombinedMultiplier,
        ReviewedBy = item.ReviewedBy,
        ReviewReason = item.ReviewReason
    };

    private static LiveOrderDto MapOrder(LiveOrderRecord item) => new()
    {
        ClientOrderId = item.ClientOrderId,
        BrokerOrderId = item.BrokerOrderId,
        BrokerTradeId = item.BrokerTradeId,
        StrategyId = item.StrategyId,
        Instrument = item.Instrument.Value,
        Side = item.Side.ToString(),
        RequestedQuantity = item.RequestedQuantity,
        FilledQuantity = item.FilledQuantity,
        AverageFillPrice = item.AverageFillPrice,
        ProtectiveStopPrice = item.ProtectiveStopPrice,
        TakeProfitPrice = item.TakeProfitPrice,
        State = item.State.ToString(),
        UpdatedAt = item.UpdatedAt,
        LastReason = item.LastReason
    };

    private static LivePositionDto MapPosition(LivePositionRecord item) => new()
    {
        PositionId = item.PositionId,
        BrokerTradeId = item.BrokerTradeId,
        StrategyId = item.StrategyId,
        Instrument = item.Instrument.Value,
        Side = item.Side.ToString(),
        InitialQuantity = item.InitialQuantity,
        Quantity = item.Quantity,
        AveragePrice = item.AveragePrice,
        InitialStopPrice = item.InitialStopPrice,
        ProtectiveStopPrice = item.ProtectiveStopPrice,
        TakeProfitPrice = item.TakeProfitPrice,
        MaximumFavourableExcursionR = item.MaximumFavourableExcursionR,
        MaximumAdverseExcursionR = item.MaximumAdverseExcursionR,
        LastManagementAction = item.LastManagementAction,
        LastManagementReason = item.LastManagementReason,
        UpdatedAt = item.UpdatedAt
    };

    private static async Task<IResult> ExecuteAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return Results.Ok(await operation().ConfigureAwait(false));
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(new { Error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { Error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { Error = ex.Message });
        }
    }
}
