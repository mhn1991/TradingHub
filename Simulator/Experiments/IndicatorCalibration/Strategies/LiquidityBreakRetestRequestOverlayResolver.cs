using Agent.Configuration;
using Agent.Strategies.StructuralConfluence;
using Simulator.Calibration;
using Simulator.Models;

namespace Simulator.Experiments.IndicatorCalibration.Strategies;

/// <summary>
/// Applies every structural-confluence <see cref="StrategyInstrumentAssignment"/>'s pinned
/// liquidity-break-retest calibration artifact (if any) across a whole <see cref="BacktestRequest"/> -
/// the <c>LiquidityBreakRetestOptions</c> counterpart to
/// <see cref="IndicatorConfluenceRequestOverlayResolver"/>. Both resolvers can run over the same
/// request's assignments independently since they touch different sub-objects of
/// <see cref="StructuralConfluenceStrategyOptions"/> (<c>IndicatorConfluence</c> vs
/// <c>LiquidityBreakRetest</c>) - an assignment could in principle pin both, applied in either order.
/// </summary>
public static class LiquidityBreakRetestRequestOverlayResolver
{
    public static async Task<BacktestRequest> ApplyToRequestAsync(
        BacktestRequest request, ICalibrationArtifactRepository artifacts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(artifacts);

        if (request.StrategyAssignments is not { Count: > 0 } assignments ||
            assignments.All(assignment => assignment.LiquidityBreakRetestCalibrationArtifactId is null))
        {
            return request;
        }

        var manifest = new LiquidityBreakRetestCalibrationManifest();
        var resolved = new List<StrategyInstrumentAssignment>(assignments.Count);
        foreach (StrategyInstrumentAssignment assignment in assignments)
        {
            if (assignment.LiquidityBreakRetestCalibrationArtifactId is null)
            {
                resolved.Add(assignment);
                continue;
            }
            if (!string.Equals(assignment.StrategyType, TradingAgentTypeIds.StructuralConfluence, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Assignment '{assignment.Id}' pins liquidity-break-retest calibration artifact " +
                    $"'{assignment.LiquidityBreakRetestCalibrationArtifactId}' but its StrategyType " +
                    $"'{assignment.StrategyType}' is not calibration-enabled yet.");
            }

            TradingAgentDefinition baselineDefinition = request.ResolveAgentDefinition(
                assignment.StrategyType, assignment.AgentDefinitionOverride, assignment.AgentOptionsOverride);
            StructuralConfluenceStrategyOptions baseline = baselineDefinition.StructuralConfluence!;
            TimeframeTopology topology = IndicatorConfluenceTimeframeTopology.Resolve(request.Runtime, baseline);

            TradingAgentDefinition overlaidDefinition = await IndicatorCalibrationOverlayResolver.ResolveAsync(
                assignment.LiquidityBreakRetestCalibrationArtifactId.Value,
                baseline.LiquidityBreakRetest,
                liquidityOptions => new TradingAgentDefinition
                {
                    Kind = TradingAgentKind.StructuralConfluence,
                    StructuralConfluence = baseline with { LiquidityBreakRetest = liquidityOptions }
                },
                manifest,
                LiquidityBreakRetestCalibrationCompatibility.Instance,
                topology,
                assignment.Instrument,
                artifacts,
                cancellationToken).ConfigureAwait(false);
            resolved.Add(assignment with { AgentDefinitionOverride = overlaidDefinition });
        }

        return request with { StrategyAssignments = resolved };
    }
}
