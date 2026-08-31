using Agent.Abstractions;
using Agent.Configuration;
using Agent.Strategies;
using Agent.Strategies.StructuralConfluence;

namespace Agent.Factories;

public static class TradingAgentFactory
{
    private static readonly Lazy<ITradingAgentCatalog> DefaultCatalog = new(TradingAgentCatalog.CreateDefault);
    private static ITradingAgentCatalog? _installed;

    /// <summary>
    /// Replaces the catalogue used by every <c>Create</c> call.
    /// <para>
    /// Exists because the ML agents need a trained <c>ITradingModel</c>, and models are produced by
    /// <c>TradingClassifier.ML</c> — a Microsoft.ML dependency that must not enter this assembly's
    /// AOT graph. Without an installed catalogue the builders fall back to a no-trade model, so a
    /// backtest of <c>trading-classification</c> or <c>trend-tactical</c> at rung C reports
    /// <b>zero trades and no error</b>, which is indistinguishable from "the strategy found
    /// nothing". PROJECT_STATE §3.23 records that this is how every ML agent behaved in the trading
    /// pipeline until a resolver was wired.
    /// </para>
    /// <para>
    /// Hosts that can take the ML dependency install a catalogue built with model resolvers; the
    /// default stays model-free.
    /// </para>
    /// </summary>
    public static void Install(ITradingAgentCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _installed = catalog;
    }

    /// <summary>Reverts to the model-free default. Chiefly for tests.</summary>
    public static void Reset() => _installed = null;

    private static ITradingAgentCatalog Catalog => _installed ?? DefaultCatalog.Value;

    public static ITradingAgent Create(TradingAgentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();
        return Catalog.Create(definition.ToAgentDefinition());
    }

    public static ITradingAgent Create(AgentDefinition definition) => Catalog.Create(definition);

    public static ITradingAgentCatalog SharedCatalog => Catalog;
}
