using Agent.Configuration;
using Brokers.Models;
using Microsoft.Extensions.Logging;
using Simulator.Models;
using TradingPolicies;

namespace QuantResearch.Training.Pipeline;

public interface ILiveCalibrationTrainingScheduler
{
    /// <summary>
    /// Runs any configured, currently-due retraining. Never activates anything into live
    /// execution - a successful run only ever produces a new PendingReview
    /// <see cref="CalibrationBundleCandidate"/>; promotion stays a separate, explicit,
    /// human action (see <see cref="ICalibrationBundleApprovalStore.ApproveAsync"/>) and
    /// activation a further one still (<c>LiveTradingRuntimeCoordinator.ActivatePolicyRevisionAsync</c>).
    /// Every failure is caught and logged rather than propagated - this must never take the host
    /// down; the host keeps running ObserveOnly/Shadow (or whatever it was already doing) either way.
    /// </summary>
    Task RunDueRetrainingAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Fire-and-forget-friendly training scheduler: intended to be started as a detached task from
/// <c>LiveTradingHost.LiveEngineHostedService.ExecuteAsync</c>, explicitly NOT added to that
/// method's <c>workerTasks</c> list (which treats any task completion, including success, as
/// host-fatal - a finite training run must never live there). Mirrors
/// <c>DashboardLive.ResearchCalibrationService</c>'s single-flight discipline with a semaphore,
/// so an operator-triggered manual run and the automatic scheduler can never train concurrently
/// and thrash the same backtest/artifact resources. Lives here (not in LiveTradingHost) so it's
/// testable against the same fakes <c>CalibrationBundleWorkflowTests</c> already uses.
/// </summary>
public sealed class LiveCalibrationTrainingScheduler(
    IReadOnlyList<CalibrationRetrainingPolicy> policies,
    CalibrationBundleWorkflow workflow,
    ITradingPolicyProfileStore profiles,
    TimeProvider timeProvider,
    ILogger<LiveCalibrationTrainingScheduler>? logger = null) : ILiveCalibrationTrainingScheduler
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task RunDueRetrainingAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (CalibrationRetrainingPolicy policy in policies)
            {
                if (!policy.Enabled)
                    continue;
                cancellationToken.ThrowIfCancellationRequested();
                await RunOneAsync(policy, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RunOneAsync(CalibrationRetrainingPolicy policy, CancellationToken cancellationToken)
    {
        try
        {
            policy.Validate();
            TradingPolicyProfile? latest = await profiles
                .GetLatestApprovedAsync(policy.StrategyId, cancellationToken)
                .ConfigureAwait(false);
            DateTimeOffset now = timeProvider.GetUtcNow();
            bool due = policy.ForceRetrainOnStartup ||
                latest is null ||
                now - latest.CreatedAt >= policy.MinimumArtifactAgeBeforeRetrain;
            if (!due)
            {
                logger?.LogInformation(
                    "Calibration for {StrategyId} is still fresh (last approved {CreatedAt:O}); skipping automatic retrain.",
                    policy.StrategyId, latest?.CreatedAt);
                return;
            }

            logger?.LogInformation(
                "Starting automatic calibration retraining for {StrategyId}/{Instrument}.",
                policy.StrategyId, policy.Instrument);

            var request = new CalibrationBundlePromotionRequest
            {
                Training = new CalibrationTrainingRequest
                {
                    Instruments = [new InstrumentKey(policy.Instrument)],
                    Strategies = [policy.StrategyId],
                    From = now.AddDays(-policy.TrainingWindowDays),
                    To = now,
                    // A bare-default runtime is a deliberate placeholder for a first version -
                    // a real deployment should source this from the strategy's own
                    // research-validated BacktestRuntimeOptions rather than framework defaults.
                    Runtime = new BacktestRuntimeOptions(),
                    Folds = policy.Folds,
                    Embargo = TimeSpan.FromHours(policy.EmbargoHours),
                    Description = $"automatic retrain · {policy.StrategyId} · {policy.Instrument} · {now:O}"
                },
                StrategyVersion = policy.StrategyVersion
            };

            CalibrationBundlePromotionResult result = await workflow
                .RunAndProposeAsync(request, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!result.Training.Success)
            {
                logger?.LogWarning(
                    "Automatic calibration retraining for {StrategyId} failed: {Reason}",
                    policy.StrategyId, result.Training.FailureReason);
                return;
            }
            if (result.Candidate is null)
            {
                logger?.LogWarning(
                    "Automatic calibration retraining for {StrategyId} produced mutually incompatible artifacts: {Reason}",
                    policy.StrategyId, result.IncompatibilityReason);
                return;
            }

            logger?.LogInformation(
                "Automatic calibration retraining for {StrategyId} produced candidate {CandidateId}, pending review.",
                policy.StrategyId, result.Candidate.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Training failures must never take the host down or affect its current
            // ObserveOnly/Shadow/ManualApproval/Automatic behaviour - only log and move on.
            logger?.LogError(ex, "Automatic calibration retraining for {StrategyId} failed unexpectedly.", policy.StrategyId);
        }
    }
}
