using ChartAnnotator.Regime;

namespace RiskManager.Conditions;

public sealed class TradingConditionFilter : ITradingConditionFilter
{
    // London/New York session hours are defined in LOCAL exchange time (matching the
    // original UTC-hour literals' implied local hours: 07:00-16:00 was correct only
    // during GMT/winter for London, 12:00-21:00 UTC was correct only during EDT/summer
    // for New York) and converted per-timestamp via IANA zones, so the session actually
    // tracks UK/US daylight-saving transitions instead of drifting an hour twice a year.
    private static readonly TimeZoneInfo LondonTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly TimeZoneInfo NewYorkTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly TimeSpan LondonSessionStart = TimeSpan.FromHours(7);
    private static readonly TimeSpan LondonSessionEnd = TimeSpan.FromHours(16);
    private static readonly TimeSpan NewYorkSessionStart = TimeSpan.FromHours(8);
    private static readonly TimeSpan NewYorkSessionEnd = TimeSpan.FromHours(17);

    private readonly TradingConditionOptions _options;
    private readonly IEconomicEventProvider? _events;
    private readonly TimeZoneInfo _brokerTimeZone;

    public TradingConditionFilter(TradingConditionOptions? options = null, IEconomicEventProvider? events = null)
    {
        _options = options ?? new TradingConditionOptions();
        _options.Validate();
        if (_options.EconomicEventFilterEnabled && events is null)
        {
            throw new ArgumentException(
                "EconomicEventFilterEnabled requires a real IEconomicEventProvider. No production " +
                "provider is currently configured, so silently continuing would report event " +
                "protection as active while never actually blocking an entry. Either supply a " +
                "provider or leave EconomicEventFilterEnabled disabled.",
                nameof(events));
        }
        _events = events;
        _brokerTimeZone = TimeZoneInfo.FindSystemTimeZoneById(_options.BrokerTimeZoneId);
    }

    public TradingConditionDecision Evaluate(TradingConditionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        TradingSession session = ClassifySession(context.Timestamp);
        if (!_options.Enabled)
            return Decision(TradingConditionAction.Allow, 1m, "ConditionsDisabled", "Trading-condition filtering is disabled.", session);

        TimeSpan age = context.Timestamp - context.MarketDataAvailableAt;
        if (age < TimeSpan.Zero || age > _options.MaximumDataAge)
            return Decision(TradingConditionAction.RejectEntry, 0m, "StaleMarketData", $"Market data age {age} exceeds {_options.MaximumDataAge}.", session);

        if (context.Regime == MarketRegime.IlliquidUnsafe)
            return Decision(context.IsNewEntry ? TradingConditionAction.RejectEntry : TradingConditionAction.ReduceOpenExposure,
                0m, "RegimeIlliquidUnsafe", "The authoritative market regime is illiquid/unsafe.", session);

        if (session is TradingSession.Weekend or TradingSession.Closed)
            return Decision(TradingConditionAction.RejectEntry, 0m, "MarketClosed", "The market session is closed.", session);
        if (session == TradingSession.BrokerRollover)
            return Decision(TradingConditionAction.DelayEntry, 0m, "BrokerRolloverBlackout", "Entry is delayed during broker rollover.", session);
        if (session == TradingSession.PreWeekend)
            return Decision(TradingConditionAction.DelayEntry, 0m, "PreWeekendBlackout", "Entry is delayed near the weekly close.", session);

        IReadOnlyList<TradingSession> allowed = _options.InstrumentAllowedSessions.TryGetValue(context.Instrument.Value, out IReadOnlyList<TradingSession>? specific)
            ? specific
            : _options.AllowedSessions;
        if (!allowed.Contains(session))
            return Decision(TradingConditionAction.DelayEntry, 0m, "SessionNotAllowed", $"{session} is not enabled for {context.Instrument}.", session);

        (EconomicEventFilterState eventState, string[] eventIds) = EvaluateEvents(context);
        if (eventState == EconomicEventFilterState.Blackout)
            return Decision(TradingConditionAction.DelayEntry, 0m, "EconomicEventBlackout",
                $"Entry is inside the configured event blackout ({string.Join(",", eventIds)}).",
                session, eventState: eventState, eventIds: eventIds);

        if (context.Atr is not > 0m)
            return _options.RejectWhenAtrUnavailable
                ? Decision(TradingConditionAction.RejectEntry, 0m, "AtrUnavailable", "A positive ATR is required to evaluate executable spread.", session, eventState: eventState)
                : Decision(TradingConditionAction.AllowWithReducedRisk, _options.MissingSpreadRiskMultiplier, "AtrUnavailableReducedRisk", "ATR is unavailable; risk was reduced.", session, eventState: eventState);

        if (context.CurrentSpread is not >= 0m)
            return _options.ReduceRiskWhenSpreadUnavailable
                ? Decision(TradingConditionAction.AllowWithReducedRisk, _options.MissingSpreadRiskMultiplier, "SpreadUnavailableReducedRisk", "Executable spread is unavailable; risk was reduced.", session, eventState: eventState)
                : Decision(TradingConditionAction.RejectEntry, 0m, "SpreadUnavailable", "Executable spread is unavailable.", session, eventState: eventState);

        decimal spreadAtr = context.CurrentSpread.Value / context.Atr.Value;
        if (spreadAtr >= _options.HardMaximumSpreadAtr)
            return Decision(TradingConditionAction.RejectEntry, 0m, "SpreadAtrHardLimit", $"Spread/ATR {spreadAtr:F4} reached hard limit {_options.HardMaximumSpreadAtr:F4}.", session, spreadAtr, eventState);
        if (spreadAtr >= _options.SoftMaximumSpreadAtr)
            return Decision(TradingConditionAction.AllowWithReducedRisk, _options.SoftSpreadRiskMultiplier, "SpreadAtrSoftLimit", $"Spread/ATR {spreadAtr:F4} reached the soft limit; risk was reduced.", session, spreadAtr, eventState);

        return Decision(TradingConditionAction.Allow, 1m, "TradingConditionsAllowed", $"{session} conditions and spread/ATR {spreadAtr:F4} are acceptable.", session, spreadAtr, eventState);
    }

    public TradingSession ClassifySession(DateTimeOffset timestamp)
    {
        DateTimeOffset utc = timestamp.ToUniversalTime();
        if (utc.DayOfWeek == DayOfWeek.Saturday || (utc.DayOfWeek == DayOfWeek.Sunday && utc.Hour < 21))
            return TradingSession.Weekend;
        if (utc.DayOfWeek == DayOfWeek.Friday)
        {
            DateTimeOffset close = new(utc.Year, utc.Month, utc.Day, _options.FridayCloseHourUtc, 0, 0, TimeSpan.Zero);
            if (utc >= close)
                return TradingSession.Weekend;
            if (utc >= close.AddMinutes(-_options.PreWeekendMinutes))
                return TradingSession.PreWeekend;
        }

        DateTime brokerLocal = TimeZoneInfo.ConvertTime(utc, _brokerTimeZone).DateTime;
        DateTime rollover = brokerLocal.Date + _options.BrokerRolloverLocalTime.ToTimeSpan();
        if (brokerLocal >= rollover.AddMinutes(-_options.RolloverBlackoutMinutesBefore) &&
            brokerLocal <= rollover.AddMinutes(_options.RolloverBlackoutMinutesAfter))
            return TradingSession.BrokerRollover;

        TimeSpan londonLocal = TimeZoneInfo.ConvertTime(utc, LondonTimeZone).TimeOfDay;
        TimeSpan newYorkLocal = TimeZoneInfo.ConvertTime(utc, NewYorkTimeZone).TimeOfDay;
        bool london = londonLocal >= LondonSessionStart && londonLocal < LondonSessionEnd;
        bool newYork = newYorkLocal >= NewYorkSessionStart && newYorkLocal < NewYorkSessionEnd;
        if (london && newYork) return TradingSession.LondonNewYorkOverlap;
        if (london) return TradingSession.London;
        if (newYork) return TradingSession.NewYork;
        return TradingSession.Asian;
    }

    private (EconomicEventFilterState State, string[] EventIds) EvaluateEvents(TradingConditionContext context)
    {
        if (!_options.EconomicEventFilterEnabled || _events is null)
            return (EconomicEventFilterState.Unavailable, []);

        IReadOnlySet<string> currencies = InstrumentCurrencies(context.Instrument.Value);
        DateTimeOffset from = context.Timestamp.AddMinutes(-_options.EventBlackoutMinutesAfter);
        DateTimeOffset to = context.Timestamp.AddMinutes(_options.EventBlackoutMinutesBefore);
        string[] ids = _events.GetKnownEvents(from, to, currencies)
            .Where(item => item.KnownAt <= context.Timestamp && item.Importance >= _options.MinimumBlockedEventImportance &&
                currencies.Contains(item.Currency) &&
                context.Timestamp >= item.ScheduledAt.AddMinutes(-_options.EventBlackoutMinutesBefore) &&
                context.Timestamp <= item.ScheduledAt.AddMinutes(_options.EventBlackoutMinutesAfter))
            .OrderBy(item => item.ScheduledAt)
            .ThenBy(item => item.EventId, StringComparer.Ordinal)
            .Select(item => item.EventId)
            .ToArray();
        return ids.Length == 0 ? (EconomicEventFilterState.Clear, ids) : (EconomicEventFilterState.Blackout, ids);
    }

    private static IReadOnlySet<string> InstrumentCurrencies(string value)
    {
        string symbol = value.Contains(':') ? value[(value.IndexOf(':') + 1)..] : value;
        string[] parts = symbol.Split(['/', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2
            ? new HashSet<string>(parts.Take(2), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private static TradingConditionDecision Decision(
        TradingConditionAction action,
        decimal multiplier,
        string code,
        string explanation,
        TradingSession session,
        decimal? spreadAtr = null,
        EconomicEventFilterState eventState = EconomicEventFilterState.Unavailable,
        IReadOnlyList<string>? eventIds = null) => new()
    {
        Action = action,
        RiskMultiplier = Math.Clamp(multiplier, 0m, 1m),
        ReasonCode = code,
        Explanation = explanation,
        Session = session,
        SpreadAtr = spreadAtr,
        EconomicEventState = eventState,
        EventIds = eventIds ?? []
    };
}
