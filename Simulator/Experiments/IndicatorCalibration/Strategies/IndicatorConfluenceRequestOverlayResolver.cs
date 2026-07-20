using Agent.Configuration;
using Agent.Strategies.StructuralConfluence;
using Simulator.Calibration;
using Simulator.Models;

namespace Simulator.Experiments.IndicatorCalibration.Strategies;

/// <summary>
/// Applies every structural-confluence <see cref="StrategyInstrumentAssignment"/>'s pinned
/// indicator-calibration artifact (if any) across a whole <see cref="BacktestRequest"/> - the
/// real consumption-side entry point (blueprint §19 Phase 7) that a CLI, API, or live host calls
/// right after building a request and before it reaches the engine. A request with no
/// <see cref="BacktestRequest.StrategyAssignments"/>, or none of them pinning an artifact, is
/// returned completely unchanged and never touches the artifact repository at all.
/// </summary>
public static class IndicatorConfluenceRequestOverlayResolver
{
    public static async Task<BacktestRequest> ApplyToRequestAsync(
        BacktestRequest request, ICalibrationArtifactRepository artifacts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(artifacts);

        if (request.StrategyAssignments is not { Count: > 0 } assignments ||
            assignments.All(assignment => assignment.IndicatorCalibrationArtifactId is null))
        {
            return request;
        }

        var manifest = new IndicatorConfluenceCalibrationManifest();
        var resolved = new List<StrategyInstrumentAssignment>(assignments.Count);
        foreach (StrategyInstrumentAssignment assignment in assignments)
        {
            if (assignment.IndicatorCalibrationArtifactId is null)
            {
                resolved.Add(assignment);
                continue;
            }
            if (!string.Equals(assignment.StrategyType, TradingAgentTypeIds.StructuralConfluence, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Assignment '{assignment.Id}' pins indicator-calibration artifact " +
                    $"'{assignment.IndicatorCalibrationArtifactId}' but its StrategyType " +
                    $"'{assignment.StrategyType}' is not calibration-enabled yet.");
            }

            TradingAgentDefinition baselineDefinition = request.ResolveAgentDefinition(
                assignment.StrategyType, assignment.AgentDefinitionOverride, assignment.AgentOptionsOverride);
            StructuralConfluenceStrategyOptions baseline = baselineDefinition.StructuralConfluence!;
            TimeframeTopology topology = IndicatorConfluenceTimeframeTopology.Resolve(request.Runtime, baseline);

            StrategyInstrumentAssignment overlaid = await IndicatorCalibrationOverlayResolver.ApplyIfPinnedAsync(
                assignment,
                baseline.IndicatorConfluence,
                indicatorOptions => new TradingAgentDefinition
                {
                    Kind = TradingAgentKind.StructuralConfluence,
                    StructuralConfluence = baseline with { IndicatorConfluence = indicatorOptions }
                },
                manifest,
                IndicatorConfluenceCalibrationCompatibility.Instance,
                topology,
                artifacts,
                cancellationToken).ConfigureAwait(false);
            resolved.Add(overlaid);
        }

        return request with { StrategyAssignments = resolved };
    }
}
