using System.Text.Json;
using LiveTrading.Configuration;
using PortfolioManager.Risk;
using RiskManager.Safety;

namespace LiveTradingHost.Configuration;

internal static class LiveSharedPolicyOptionsResolver
{
    public static TradingSafetyOptions ResolveSafety(
        LiveMarketUniverseOptions markets,
        IReadOnlyDictionary<string, LivePolicyBundleOptions> bundles) =>
        ResolveShared(markets, bundles, options => options.Profile.AccountSafety,
            new TradingSafetyOptions(), "account safety");

    public static PortfolioRiskOptions ResolvePortfolioRisk(
        LiveMarketUniverseOptions markets,
        IReadOnlyDictionary<string, LivePolicyBundleOptions> bundles) =>
        ResolveShared(markets, bundles, options => options.Profile.PortfolioRisk,
            new PortfolioRiskOptions(), "portfolio risk");

    private static T ResolveShared<T>(
        LiveMarketUniverseOptions markets,
        IReadOnlyDictionary<string, LivePolicyBundleOptions> bundles,
        Func<LivePolicyBundleOptions, T> selector,
        T fallback,
        string description)
    {
        string[] ids = markets.Markets
            .Where(market => market.Enabled)
            .SelectMany(market => market.Strategies)
            .Where(assignment => assignment.Enabled &&
                assignment.Mode is StrategyActivationMode.ManualApproval or StrategyActivationMode.Automatic)
            .Select(assignment => assignment.PolicyBundleId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0)
            return fallback;

        var values = new List<T>(ids.Length);
        foreach (string id in ids)
        {
            if (!bundles.TryGetValue(id, out LivePolicyBundleOptions? options))
                throw new InvalidOperationException($"Executable policy bundle '{id}' was not found.");
            options.Profile.Validate();
            values.Add(selector(options));
        }

        string[] distinct = values
            .Select(value => JsonSerializer.Serialize(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (distinct.Length != 1)
        {
            throw new InvalidOperationException(
                $"All executable Agents must share one authoritative {description} policy in the first live release.");
        }
        return values[0];
    }
}
