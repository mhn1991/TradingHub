using Brokers.Models;
using ChartAnnotator.Regime;

namespace RiskManager.Conditions;

public enum TradingConditionAction
{
    Allow,
    AllowWithReducedRisk,
    DelayEntry,
    RejectEntry,
    ReduceOpenExposure
}

public enum TradingSession
{
    Closed,
    Asian,
    London,
    NewYork,
    LondonNewYorkOverlap,
    BrokerRollover,
    Weekend,
    PreWeekend
}

public enum EconomicEventFilterState
{
    Unavailable,
    Clear,
    Blackout
}

public enum EconomicEventImportance
{
    Low,
    Medium,
    High
}

public sealed record EconomicEvent
{
    public required string EventId { get; init; }
    public required DateTimeOffset ScheduledAt { get; init; }
    public required DateTimeOffset KnownAt { get; init; }
    public required string Currency { get; init; }
    public required EconomicEventImportance Importance { get; init; }
    public required string Name { get; init; }
    public string? ProviderVersion { get; init; }
}

public interface IEconomicEventProvider
{
    IReadOnlyList<EconomicEvent> GetKnownEvents(
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlySet<string> currencies);
}

public sealed record TradingConditionOptions
{
    public bool Enabled { get; init; }
    public IReadOnlyList<TradingSession> AllowedSessions { get; init; } =
    [
        TradingSession.Asian,
        TradingSession.London,
        TradingSession.NewYork,
        TradingSession.LondonNewYorkOverlap
    ];
    public IReadOnlyDictionary<string, IReadOnlyList<TradingSession>> InstrumentAllowedSessions { get; init; } =
        new Dictionary<string, IReadOnlyList<TradingSession>>(StringComparer.OrdinalIgnoreCase);
    public string BrokerTimeZoneId { get; init; } = "America/New_York";
    public TimeOnly BrokerRolloverLocalTime { get; init; } = new(17, 0);
    public int RolloverBlackoutMinutesBefore { get; init; } = 15;
    public int RolloverBlackoutMinutesAfter { get; init; } = 15;
    public int FridayCloseHourUtc { get; init; } = 21;
    public int PreWeekendMinutes { get; init; } = 60;
    public decimal SoftMaximumSpreadAtr { get; init; } = 0.15m;
    public decimal HardMaximumSpreadAtr { get; init; } = 0.30m;
    public decimal SoftSpreadRiskMultiplier { get; init; } = 0.50m;
    public TimeSpan MaximumDataAge { get; init; } = TimeSpan.FromMinutes(2);
    public bool RejectWhenAtrUnavailable { get; init; } = true;
    public bool ReduceRiskWhenSpreadUnavailable { get; init; } = true;
    public decimal MissingSpreadRiskMultiplier { get; init; } = 0.50m;
    public bool EconomicEventFilterEnabled { get; init; }
    public int EventBlackoutMinutesBefore { get; init; } = 30;
    public int EventBlackoutMinutesAfter { get; init; } = 15;
    public EconomicEventImportance MinimumBlockedEventImportance { get; init; } = EconomicEventImportance.High;

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(AllowedSessions);
        ArgumentNullException.ThrowIfNull(InstrumentAllowedSessions);
        if (string.IsNullOrWhiteSpace(BrokerTimeZoneId))
            throw new ArgumentException("A broker timezone is required.", nameof(BrokerTimeZoneId));

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(BrokerTimeZoneId);
        }
        catch (TimeZoneNotFoundException exception)
        {
            throw new ArgumentException($"Unknown IANA timezone '{BrokerTimeZoneId}'.", nameof(BrokerTimeZoneId), exception);
        }

        if (RolloverBlackoutMinutesBefore < 0 || RolloverBlackoutMinutesAfter < 0 ||
            FridayCloseHourUtc is < 0 or > 23 || PreWeekendMinutes < 0 ||
            SoftMaximumSpreadAtr <= 0m || HardMaximumSpreadAtr <= SoftMaximumSpreadAtr ||
            SoftSpreadRiskMultiplier is < 0m or > 1m || MaximumDataAge <= TimeSpan.Zero ||
            MissingSpreadRiskMultiplier is < 0m or > 1m ||
            EventBlackoutMinutesBefore < 0 || EventBlackoutMinutesAfter < 0 ||
            !Enum.IsDefined(MinimumBlockedEventImportance) ||
            AllowedSessions.Any(session => !Enum.IsDefined(session)) ||
            InstrumentAllowedSessions.Any(pair => string.IsNullOrWhiteSpace(pair.Key) ||
                pair.Value is null || pair.Value.Any(session => !Enum.IsDefined(session))))
        {
            throw new ArgumentOutOfRangeException(nameof(TradingConditionOptions));
        }
    }
}

public sealed record TradingConditionContext
{
    public required DateTimeOffset Timestamp { get; init; }
    public required InstrumentKey Instrument { get; init; }
    public decimal? CurrentSpread { get; init; }
    public decimal? Atr { get; init; }
    public required DateTimeOffset MarketDataAvailableAt { get; init; }
    public MarketRegime Regime { get; init; } = MarketRegime.Unknown;
    public bool IsNewEntry { get; init; } = true;
}

public sealed record TradingConditionDecision
{
    public required TradingConditionAction Action { get; init; }
    public required decimal RiskMultiplier { get; init; }
    public required string ReasonCode { get; init; }
    public required string Explanation { get; init; }
    public required TradingSession Session { get; init; }
    public decimal? SpreadAtr { get; init; }
    public EconomicEventFilterState EconomicEventState { get; init; } = EconomicEventFilterState.Unavailable;
    public IReadOnlyList<string> EventIds { get; init; } = [];
}

public interface ITradingConditionFilter
{
    TradingConditionDecision Evaluate(TradingConditionContext context);
}
