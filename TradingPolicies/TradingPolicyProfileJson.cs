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
