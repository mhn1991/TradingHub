namespace Agent.Configuration;

public static class TradingAgentTypeIds
{
    public const string LegacyProgressive = "legacy-progressive";
    public const string ImprovedProgressive = "improved-progressive";
    public const string StructuralConfluence = "structural-confluence";

    public const string LegacyAlias = "legacy";
    public const string ImprovedAlias = "improved";

    public static TradingAgentKind Parse(string value)
    {
        if (!TryParse(value, out TradingAgentKind kind))
            throw new ArgumentException(
                $"Unknown trading agent '{value}'. Expected legacy-progressive, improved-progressive, or structural-confluence.",
                nameof(value));
        return kind;
    }

    public static bool TryParse(string? value, out TradingAgentKind kind)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case LegacyAlias:
            case LegacyProgressive:
                kind = TradingAgentKind.LegacyProgressive;
                return true;
            case ImprovedAlias:
            case ImprovedProgressive:
                kind = TradingAgentKind.ImprovedProgressive;
                return true;
            case StructuralConfluence:
                kind = TradingAgentKind.StructuralConfluence;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    public static string Format(TradingAgentKind kind) => kind switch
    {
        TradingAgentKind.LegacyProgressive => LegacyProgressive,
        TradingAgentKind.ImprovedProgressive => ImprovedProgressive,
        TradingAgentKind.StructuralConfluence => StructuralConfluence,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static string Normalize(string value) => Format(Parse(value));
}
