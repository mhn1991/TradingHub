using Agent.Abstractions;
using Agent.Configuration;
using Agent.Strategies;
using Agent.Strategies.StructuralConfluence;

namespace Agent.Factories;

public static class TradingAgentFactory
{
    private static readonly Lazy<ITradingAgentCatalog> Catalog = new(TradingAgentCatalog.CreateDefault);

    public static ITradingAgent Create(TradingAgentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();
        return Catalog.Value.Create(definition.ToAgentDefinition());
    }

    public static ITradingAgent Create(AgentDefinition definition) => Catalog.Value.Create(definition);

    public static ITradingAgentCatalog SharedCatalog => Catalog.Value;
}
