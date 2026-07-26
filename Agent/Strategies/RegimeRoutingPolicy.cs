using ChartAnnotator.Regime;

namespace Agent.Strategies;

/// <summary>
/// Per-regime entry/risk policy. Structural strategies use EntryProfileId to route
/// setup families; ManagementProfileId remains available to trade management.
/// </summary>
public sealed record RegimeStrategyPolicy
{
    public required MarketRegime Regime { get; init; }
    public bool AllowNewEntries { get; init; }
    public decimal MinimumConfidenceAdjustment { get; init; }
    public decimal RiskMultiplier { get; init; } = 1m;
    public string EntryProfileId { get; init; } = "default";
    public string ManagementProfileId { get; init; } = "default";
}

/// <summary>
/// Master options for regime-based strategy routing (spec Part A, §9). Disabled by
/// default so routing is fully opt-in and independent of classification being on.
/// </summary>
public sealed record MarketRegimePolicyOptions
{
    public bool Enabled { get; init; }
    public decimal MinimumRegimeConfidence { get; init; } = 40m;
    public IReadOnlyDictionary<MarketRegime, RegimeStrategyPolicy> Policies { get; init; } = DefaultPolicies();

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Policies);
        if (MinimumRegimeConfidence is < 0m or > 100m)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumRegimeConfidence));
        }

        foreach (RegimeStrategyPolicy policy in Policies.Values)
        {
            if (policy.RiskMultiplier is < 0m or > 1m)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(Policies),
                    "Phase 1 risk multipliers must stay within [0, 1]; increasing above base risk requires later validated evidence.");
            }

            if (string.IsNullOrWhiteSpace(policy.EntryProfileId) ||
                string.IsNullOrWhiteSpace(policy.ManagementProfileId))
            {
                throw new ArgumentOutOfRangeException(nameof(Policies));
            }
        }
    }

    /// <summary>Seeded defaults from spec §9.2.</summary>
    public static IReadOnlyDictionary<MarketRegime, RegimeStrategyPolicy> DefaultPolicies() =>
        new Dictionary<MarketRegime, RegimeStrategyPolicy>
        {
            [MarketRegime.Unknown] = new()
            {
                Regime = MarketRegime.Unknown,
                AllowNewEntries = true,
                RiskMultiplier = 1m,
                EntryProfileId = "default",
                ManagementProfileId = "default"
            },
            [MarketRegime.TrendingUp] = new()
            {
                Regime = MarketRegime.TrendingUp,
                AllowNewEntries = true,
                RiskMultiplier = 1m,
                EntryProfileId = "trend-pullback",
                ManagementProfileId = "trend-runner"
            },
            [MarketRegime.TrendingDown] = new()
            {
                Regime = MarketRegime.TrendingDown,
                AllowNewEntries = true,
                RiskMultiplier = 1m,
                EntryProfileId = "trend-pullback",
                ManagementProfileId = "trend-runner"
            },
            [MarketRegime.Range] = new()
            {
                Regime = MarketRegime.Range,
                AllowNewEntries = true,
                RiskMultiplier = 0.6m,
                EntryProfileId = "range-boundary",
                ManagementProfileId = "range-early-reduction"
            },
            [MarketRegime.Compression] = new()
            {
                Regime = MarketRegime.Compression,
                AllowNewEntries = false,
                RiskMultiplier = 0m,
                EntryProfileId = "compression-wait",
                ManagementProfileId = "default"
            },
            [MarketRegime.BreakoutExpansionUp] = new()
            {
                Regime = MarketRegime.BreakoutExpansionUp,
                AllowNewEntries = true,
                RiskMultiplier = 0.85m,
                EntryProfileId = "breakout-displacement",
                ManagementProfileId = "breakout-retest"
            },
            [MarketRegime.BreakoutExpansionDown] = new()
            {
                Regime = MarketRegime.BreakoutExpansionDown,
                AllowNewEntries = true,
                RiskMultiplier = 0.85m,
                EntryProfileId = "breakout-displacement",
                ManagementProfileId = "breakout-retest"
            },
            [MarketRegime.HighVolatilityDisorder] = new()
            {
                Regime = MarketRegime.HighVolatilityDisorder,
                AllowNewEntries = false,
                RiskMultiplier = 0m,
                EntryProfileId = "unsafe-reject",
                ManagementProfileId = "disorder-defensive"
            },
            [MarketRegime.IlliquidUnsafe] = new()
            {
                Regime = MarketRegime.IlliquidUnsafe,
                AllowNewEntries = false,
                RiskMultiplier = 0m,
                EntryProfileId = "unsafe-reject",
                ManagementProfileId = "disorder-defensive"
            }
        };
}

public sealed record RegimeGateResult
{
    public required bool RoutingEnabled { get; init; }
    public required bool AllowNewEntries { get; init; }
    public required RegimeStrategyPolicy Policy { get; init; }
    public required string ReasonCode { get; init; }
    public required string Explanation { get; init; }
}

/// <summary>
/// Stateless: derives an allow/block + risk-multiplier decision from the current
/// regime snapshot and the configured per-regime policy map.
/// </summary>
public static class RegimeRoutingPolicy
{
    private static readonly RegimeStrategyPolicy NeutralPassthrough = new()
    {
        Regime = MarketRegime.Unknown,
        AllowNewEntries = true,
        RiskMultiplier = 1m
    };

    public static RegimeGateResult Evaluate(MarketRegimeSnapshot regime, MarketRegimePolicyOptions options)
    {
        ArgumentNullException.ThrowIfNull(regime);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return new RegimeGateResult
            {
                RoutingEnabled = false,
                AllowNewEntries = true,
                Policy = NeutralPassthrough,
                ReasonCode = "RegimeRoutingDisabled",
                Explanation = "Regime-based routing is disabled."
            };
        }

        RegimeStrategyPolicy policy = options.Policies.TryGetValue(regime.Regime, out RegimeStrategyPolicy? found)
            ? found
            : NeutralPassthrough;

        if (!regime.IsTradeable)
        {
            return new RegimeGateResult
            {
                RoutingEnabled = true,
                AllowNewEntries = false,
                Policy = policy,
                ReasonCode = $"RegimeBlocked:{regime.Regime}",
                Explanation = $"The current regime {regime.Regime} is not tradeable ({regime.ReasonCode})."
            };
        }

        if (regime.Confidence < options.MinimumRegimeConfidence)
        {
            return new RegimeGateResult
            {
                RoutingEnabled = true,
                AllowNewEntries = false,
                Policy = policy,
                ReasonCode = "RegimeConfidenceTooLow",
                Explanation = $"Regime confidence {regime.Confidence:F1} is below the minimum " +
                    $"{options.MinimumRegimeConfidence:F1} required to trust {regime.Regime}."
            };
        }

        if (!policy.AllowNewEntries)
        {
            return new RegimeGateResult
            {
                RoutingEnabled = true,
                AllowNewEntries = false,
                Policy = policy,
                ReasonCode = $"RegimeBlocked:{regime.Regime}",
                Explanation = $"The {regime.Regime} regime policy does not permit new entries."
            };
        }

        return new RegimeGateResult
        {
            RoutingEnabled = true,
            AllowNewEntries = true,
            Policy = policy,
            ReasonCode = $"RegimeAllowed:{regime.Regime}",
            Explanation = $"The {regime.Regime} regime policy permits new entries with risk multiplier {policy.RiskMultiplier:F2}."
        };
    }
}
