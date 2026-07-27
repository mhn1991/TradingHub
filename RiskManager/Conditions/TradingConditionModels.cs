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

/// <summary>
/// What to do once spread/ATR reaches <see cref="TradingConditionOptions.SoftMaximumSpreadAtr"/>,
/// short of the hard limit that always rejects.
/// </summary>
public enum SoftSpreadLimitAction
{
    /// <summary>Allow the entry with risk reduced by <see cref="TradingConditionOptions.SoftSpreadRiskMultiplier"/>.</summary>
    ReduceRisk,
    /// <summary>Reject the entry outright rather than merely sizing it down.</summary>
    RejectEntry
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
    // AGENT-03: bare default must be true — every trading-condition safety mechanism (session
    // gating, rollover blackout, pre-weekend protection, spread/ATR gates, stale-data rejection)
    // was silently off for any caller that did not explicitly opt in, and only the Dashboard's
    // Vue frontend happened to always send an explicit `true`.
    public bool Enabled { get; init; } = true;
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
    public decimal SoftMaximumSpreadAtr { get; init; } = SpreadAtrSafetyDefaults.SoftMaximum;
    public decimal HardMaximumSpreadAtr { get; init; } = SpreadAtrSafetyDefaults.HardMaximum;
    public decimal SoftSpreadRiskMultiplier { get; init; } = 0.50m;
    /// <summary>
    /// Defaults to <see cref="Conditions.SoftSpreadLimitAction.ReduceRisk"/> so existing callers
    /// keep today's behavior. The 2026-07-27 trade-log review found soft-limit trades were
    /// persistently negative-expectancy even at half risk (see
    /// Dashboard/public/data/backtests/simulations/8b1435996f4a4b64bf23218d8e1ccef2/
    /// AGENT_IMPROVEMENT_RECOMMENDATIONS.md section 5) - configure
    /// <see cref="Conditions.SoftSpreadLimitAction.RejectEntry"/> for strategies that should
    /// skip these entries entirely rather than merely size them down.
    /// </summary>
    public SoftSpreadLimitAction SoftSpreadLimitAction { get; init; } = SoftSpreadLimitAction.ReduceRisk;
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
            !Enum.IsDefined(SoftSpreadLimitAction) ||
            (Enabled && AllowedSessions.Count == 0) ||
            AllowedSessions.Any(session => !Enum.IsDefined(session)) ||
            InstrumentAllowedSessions.Any(pair => string.IsNullOrWhiteSpace(pair.Key) ||
                pair.Value is null || pair.Value.Count == 0 ||
                pair.Value.Any(session => !Enum.IsDefined(session))))
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
