using System.Text.Json;
using System.Text.Json.Serialization;
using Agent.Strategies;
using Agent.Strategies.StructuralConfluence;

namespace Agent.Configuration;

/// <summary>
/// Versioned, environment-neutral serialized definition for one Agent. The type discriminator is
/// an allow-listed canonical ID; CLR type names and runtime reflection are never accepted.
/// </summary>
public sealed record AgentDefinition
{
    public const int CurrentSchemaVersion = 1;

    public required string AgentTypeId { get; init; }
    public required int SchemaVersion { get; init; }
    public required JsonElement Options { get; init; }

    public static AgentDefinition FromProgressive(
        ProgressiveAgentKind kind,
        ProgressiveStrategyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return new AgentDefinition
        {
            AgentTypeId = kind == ProgressiveAgentKind.Legacy
                ? TradingAgentTypeIds.LegacyProgressive
                : TradingAgentTypeIds.ImprovedProgressive,
            SchemaVersion = CurrentSchemaVersion,
            Options = JsonSerializer.SerializeToElement(
                options,
                AgentDefinitionJsonContext.Default.ProgressiveStrategyOptions)
        };
    }

    public static AgentDefinition FromStructuralConfluence(StructuralConfluenceStrategyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return new AgentDefinition
        {
            AgentTypeId = TradingAgentTypeIds.StructuralConfluence,
            SchemaVersion = CurrentSchemaVersion,
            Options = JsonSerializer.SerializeToElement(
                options,
                AgentDefinitionJsonContext.Default.StructuralConfluenceStrategyOptions)
        };
    }

    public ProgressiveStrategyOptions ReadProgressiveOptions()
    {
        EnsureSchemaAndType(
            TradingAgentTypeIds.LegacyProgressive,
            TradingAgentTypeIds.ImprovedProgressive);
        ProgressiveStrategyOptions result = Options.Deserialize(
            AgentDefinitionJsonContext.Default.ProgressiveStrategyOptions)
            ?? throw new ArgumentException("Progressive Agent options were empty.", nameof(Options));
        result.Validate();
        return result;
    }

    public StructuralConfluenceStrategyOptions ReadStructuralOptions()
    {
        EnsureSchemaAndType(TradingAgentTypeIds.StructuralConfluence);
        StructuralConfluenceStrategyOptions result = Options.Deserialize(
            AgentDefinitionJsonContext.Default.StructuralConfluenceStrategyOptions)
            ?? throw new ArgumentException("Structural-confluence Agent options were empty.", nameof(Options));
        result.Validate();
        return result;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(AgentTypeId))
            throw new ArgumentException("AgentTypeId is required.", nameof(AgentTypeId));
        if (AgentTypeId != TradingAgentTypeIds.Normalize(AgentTypeId))
            throw new ArgumentException("Persisted AgentTypeId must be canonical.", nameof(AgentTypeId));
        if (SchemaVersion != CurrentSchemaVersion)
            throw new NotSupportedException($"Agent definition schema {SchemaVersion} is not supported.");
        if (Options.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Agent options must be a JSON object.", nameof(Options));
    }

    private void EnsureSchemaAndType(params string[] allowedTypes)
    {
        Validate();
        if (!allowedTypes.Contains(AgentTypeId, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"Agent type '{AgentTypeId}' does not match the requested option type.",
                nameof(AgentTypeId));
        }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ProgressiveStrategyOptions))]
[JsonSerializable(typeof(StructuralConfluenceStrategyOptions))]
internal partial class AgentDefinitionJsonContext : JsonSerializerContext;
