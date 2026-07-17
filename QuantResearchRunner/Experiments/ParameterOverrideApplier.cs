using Simulator.Models;

namespace QuantResearchRunner.Experiments;

/// <summary>
/// Applies a sensitivity/walk-forward parameter grid point onto a <see cref="BacktestRequest"/>.
/// Supports a fixed, explicit vocabulary of real, already-reachable tunables - an unrecognized
/// key throws rather than being silently ignored, so a typo in a research plan fails loudly
/// instead of quietly running the baseline.
/// </summary>
public static class ParameterOverrideApplier
{
    public static BacktestRequest Apply(BacktestRequest baseline, IReadOnlyDictionary<string, decimal> parameters)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(parameters);

        BacktestRequest request = baseline;
        foreach ((string key, decimal value) in parameters)
        {
            request = key switch
            {
                "MinimumRewardRisk" => request with { MinimumRewardRisk = value },
                "MinimumPriceActionConfidence" => request with { MinimumPriceActionConfidence = value },
                "RiskPercentOfEquity" => request with
                {
                    Runtime = request.Runtime with
                    {
                        PositionSizing = request.Runtime.PositionSizing with { RiskPercentOfEquity = value }
                    }
                },
                "BreakEvenActivationR" => request with
                {
                    Runtime = request.Runtime with
                    {
                        LegacyPositionManagement = request.Runtime.LegacyPositionManagement with
                        {
                            BreakEvenActivationR = value
                        },
                        ImprovedPositionManagement = request.Runtime.ImprovedPositionManagement with
                        {
                            BreakEvenActivationR = value
                        }
                    }
                },
                "StructureTrailActivationR" => request with
                {
                    Runtime = request.Runtime with
                    {
                        LegacyPositionManagement = request.Runtime.LegacyPositionManagement with
                        {
                            StructureTrailActivationR = value
                        },
                        ImprovedPositionManagement = request.Runtime.ImprovedPositionManagement with
                        {
                            StructureTrailActivationR = value
                        }
                    }
                },
                _ => throw new ArgumentException(
                    $"Unrecognized parameter grid key '{key}'. Supported keys: MinimumRewardRisk, " +
                    "MinimumPriceActionConfidence, RiskPercentOfEquity, BreakEvenActivationR, " +
                    "StructureTrailActivationR.")
            };
        }

        return request;
    }
}
