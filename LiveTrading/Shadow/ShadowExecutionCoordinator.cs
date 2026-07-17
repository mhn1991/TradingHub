using Agent.Models;
using Brokers.Abstractions;
using Brokers.Models;
using ExecutionManager;

namespace LiveTrading.Shadow;

/// <summary>
/// The Agent pipeline's sanctioned execution surface: an <see cref="IExecutionCoordinator"/>
/// that never calls any order-placing/amending member of the broker it is handed. This is what
/// lets <see cref="TradingCore.Pipeline.SafeTradingPipeline"/> run unmodified in shadow mode
/// while staying strictly observe-only - see
/// <c>LiveTrading.Tests.NoOrderPlacementGuardTests</c>'s <c>ExcludedFileNames</c>, which allows
/// this file to reference <c>ITradingBrokerClient</c> only because it never calls its
/// write-capable members.
/// </summary>
public sealed class ShadowExecutionCoordinator : IExecutionCoordinator
{
    public Task<OrderSubmission?> ProcessAsync(
        AgentDecision decision, ITradingBrokerClient broker, CancellationToken cancellationToken) =>
        Task.FromResult<OrderSubmission?>(null);

    public Task<ProtectiveStopAmendmentResult> AmendProtectiveStopAsync(
        ProtectiveStopAmendmentCommand command, ITradingBrokerClient broker, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Shadow mode never amends protective stops.");
}
