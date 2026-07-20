using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingPolicies;

/// <summary>Stable JSON boundary used to export a reviewed simulator policy and import it into a host deployment.</summary>
public static class TradingPolicyProfileJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(TradingPolicyProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        return JsonSerializer.Serialize(profile, Options);
    }

    public static TradingPolicyProfile Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        TradingPolicyProfile profile = JsonSerializer.Deserialize<TradingPolicyProfile>(json, Options)
            ?? throw new InvalidOperationException("The trading policy profile JSON was empty or invalid.");
        profile.Validate();
        return profile;
    }
}

/// <summary>
/// Reads both the schema-3 generic definition and the schema-2 typed definition emitted during
/// the controlled migration. Writing always emits the generic allow-listed representation.
/// </summary>
public sealed class AgentDefinitionCompatibilityJsonConverter : JsonConverter<Agent.Configuration.AgentDefinition>
{
    public override Agent.Configuration.AgentDefinition Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        JsonElement root = document.RootElement;
        if (root.TryGetProperty("agentTypeId", out JsonElement agentTypeId) ||
            root.TryGetProperty("AgentTypeId", out agentTypeId))
        {
            string canonical = Agent.Configuration.TradingAgentTypeIds.Normalize(
                agentTypeId.GetString() ?? string.Empty);
            JsonElement schemaElement = root.TryGetProperty("schemaVersion", out JsonElement camelSchema)
                ? camelSchema
                : root.GetProperty("SchemaVersion");
            JsonElement optionsElement = root.TryGetProperty("options", out JsonElement camelOptions)
                ? camelOptions
                : root.GetProperty("Options");
            var result = new Agent.Configuration.AgentDefinition
            {
                AgentTypeId = canonical,
                SchemaVersion = schemaElement.GetInt32(),
                Options = optionsElement.Clone()
            };
            result.Validate();
            return result;
        }

        Agent.Configuration.TradingAgentDefinition legacy =
            JsonSerializer.Deserialize<Agent.Configuration.TradingAgentDefinition>(root.GetRawText(), options)
            ?? throw new JsonException("Legacy Agent definition was empty.");
        legacy.Validate();
        return legacy.ToAgentDefinition();
    }

    public override void Write(
        Utf8JsonWriter writer,
        Agent.Configuration.AgentDefinition value,
        JsonSerializerOptions options)
    {
        value.Validate();
        writer.WriteStartObject();
        writer.WriteString("agentTypeId", value.AgentTypeId);
        writer.WriteNumber("schemaVersion", value.SchemaVersion);
        writer.WritePropertyName("options");
        value.Options.WriteTo(writer);
        writer.WriteEndObject();
    }
}
