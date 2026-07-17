using Agent.Strategies;
using QuantResearch.Validation;
using Simulator.Models;
using TradeManager;

namespace QuantResearch.Training.Mapping;

/// <summary>
/// Maps <see cref="FeatureSwitches"/> onto the real simulator options each flag controls.
/// Lives here (not in <c>QuantResearch</c>) so that project does not take a Simulator project
/// reference.
/// </summary>
/// <remarks>
/// RSI relationship and Bollinger context share one policy object
/// (<see cref="RsiBollingerSignalOptions"/>). When only one side is disabled, thresholds are
/// pushed so that side never contributes while the other still can. Disabling both turns the
/// whole signal block off.
/// </remarks>
public static class FeatureSwitchMapper
{
    public static BacktestRuntimeOptions Apply(BacktestRuntimeOptions baseline, FeatureSwitches switches)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(switches);

        BacktestRuntimeOptions result = baseline;

        if (!switches.AdxDmi)
            result = result with { DmiConfirmationEnabled = false };

        result = result with
        {
            RsiBollingerSignals = ApplyRsiBollinger(result.RsiBollingerSignals, switches)
        };

        if (!switches.RegimeRouting)
            result = result with { MarketRegimeRouting = result.MarketRegimeRouting with { Enabled = false } };

        if (!switches.SecondaryTrend)
        {
            result = result with
            {
                StrategyTimeframes = result.StrategyTimeframes with
                {
                    SecondaryTrendIntervals = [],
                    MinimumSecondaryTrendAlignments = 0
                }
            };
        }

        if (!switches.SetupIntervals)
        {
            result = result with
            {
                StrategyTimeframes = result.StrategyTimeframes with
                {
                    SetupIntervals = [],
                    MinimumSetupAlignments = 0
                }
            };
        }

        if (!switches.CurrencyStrength)
        {
            result = result with
            {
                CurrencyStrength = result.CurrencyStrength with { Enabled = false },
                CurrencyStrengthEvidence = result.CurrencyStrengthEvidence with { Enabled = false }
            };
        }

        if (!switches.SessionFilter)
            result = result with { TradingConditions = result.TradingConditions with { Enabled = false } };

        if (!switches.ScaleOut)
        {
            result = result with
            {
                LegacyPositionManagement = result.LegacyPositionManagement with { EnableScaleOut = false },
                ImprovedPositionManagement = result.ImprovedPositionManagement with { EnableScaleOut = false }
            };
        }

        if (!switches.ProfitFloor)
        {
            result = result with
            {
                LegacyPositionManagement = result.LegacyPositionManagement with { EnableProfitFloor = false },
                ImprovedPositionManagement = result.ImprovedPositionManagement with { EnableProfitFloor = false }
            };
        }

        if (!switches.MfeGiveback)
        {
            result = result with
            {
                LegacyPositionManagement = result.LegacyPositionManagement with { EnableMaximumGiveback = false },
                ImprovedPositionManagement = result.ImprovedPositionManagement with { EnableMaximumGiveback = false }
            };
        }

        if (!switches.StructuralTrailing)
        {
            result = result with
            {
                LegacyPositionManagement = result.LegacyPositionManagement with { Mode = TrailingStopMode.BreakEvenOnly },
                ImprovedPositionManagement = result.ImprovedPositionManagement with { Mode = TrailingStopMode.BreakEvenOnly }
            };
        }

        if (!switches.AdaptiveSizing)
            result = result with { AdaptiveRisk = result.AdaptiveRisk with { Enabled = false } };

        return result;
    }

    /// <summary>
    /// Also patches <see cref="BacktestRequest.PriceActionConfirmation"/> for the
    /// <see cref="FeatureSwitches.PriceAction"/> switch, which lives on the request rather
    /// than <see cref="BacktestRuntimeOptions"/>.
    /// </summary>
    public static BacktestRequest Apply(BacktestRequest baseline, FeatureSwitches switches)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(switches);

        BacktestRequest result = baseline with { Runtime = Apply(baseline.Runtime, switches) };

        if (!switches.PriceAction)
            result = result with { PriceActionConfirmation = PriceActionConfirmationMode.Disabled };

        return result;
    }

    private static RsiBollingerSignalOptions ApplyRsiBollinger(
        RsiBollingerSignalOptions baseline,
        FeatureSwitches switches)
    {
        if (!switches.RsiRelationship && !switches.BollingerContext)
            return baseline with { Enabled = false };

        RsiBollingerSignalOptions result = baseline;

        if (!switches.RsiRelationship)
        {
            // Age 0 + unreachable strength => relationships never usable; no RSI veto/boost.
            result = result with
            {
                MaximumRsiRelationshipAgeCandles = 0,
                MinimumRsiRelationshipStrength = 100m,
                OpposingRsiVetoStrength = 100m,
                VetoStrongOpposingRsiRelationship = false,
                RsiExtremeRelationshipBoost = 0m
            };
        }

        if (!switches.BollingerContext)
        {
            // Unreachable sample floor => bollingerReady is always false; no BB veto.
            result = result with
            {
                MinimumBollingerSamples = int.MaxValue,
                VetoOpposingBollingerExpansion = false
            };
        }

        return result;
    }
}
