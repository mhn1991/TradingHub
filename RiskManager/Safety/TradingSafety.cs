using System.Globalization;

namespace RiskManager.Safety;

public enum TradingSafetyState
{
    Active,
    Paused,
    Tripped
}

public enum SafetyTripReason
{
    None,
    Manual,
    DataQuality,
    DailyLossLimit,
    WeeklyLossLimit,
    ConsecutiveLossLimit,
    BrokerStateMismatch,
    ExecutionFailure,
    External
}

public sealed record TradingSafetyOptions
{
    public decimal? MaximumDailyLoss { get; init; }
    public decimal? MaximumWeeklyLoss { get; init; }
    public int? MaximumConsecutiveLosses { get; init; }
    public bool TripOnCriticalDataQualityIssue { get; init; } = true;
}

public sealed record TradingSafetySnapshot
{
    public required TradingSafetyState State { get; init; }
    public required SafetyTripReason Reason { get; init; }
    public string? Message { get; init; }
    public DateTimeOffset? ChangedAt { get; init; }
    public decimal DailyRealizedProfitLoss { get; init; }
    public decimal WeeklyRealizedProfitLoss { get; init; }
    public int ConsecutiveLosses { get; init; }

    public bool CanOpenNewTrades => State == TradingSafetyState.Active;
}

public interface ITradingSafetyController
{
    TradingSafetySnapshot Snapshot { get; }
    bool CanOpenNewTrades { get; }
    bool TripOnCriticalDataQualityIssue { get; }

    TradingSafetySnapshot RecordClosedTrade(decimal realizedProfitLoss, DateTimeOffset timestamp);
    TradingSafetySnapshot Pause(string message, DateTimeOffset timestamp);
    TradingSafetySnapshot Resume(DateTimeOffset timestamp);
    TradingSafetySnapshot Trip(SafetyTripReason reason, string message, DateTimeOffset timestamp);
    TradingSafetySnapshot Reset(DateTimeOffset timestamp);
}

public sealed class TradingSafetyController : ITradingSafetyController
{
    private readonly object _sync = new();
    private readonly TradingSafetyOptions _options;
    private TradingSafetyState _state = TradingSafetyState.Active;
    private SafetyTripReason _reason = SafetyTripReason.None;
    private string? _message;
    private DateTimeOffset? _changedAt;
    private DateOnly? _currentDay;
    private int? _currentIsoYear;
    private int? _currentIsoWeek;
    private decimal _dailyRealizedProfitLoss;
    private decimal _weeklyRealizedProfitLoss;
    private int _consecutiveLosses;

    public TradingSafetyController(TradingSafetyOptions? options = null)
    {
        _options = options ?? new TradingSafetyOptions();
        if (_options.MaximumDailyLoss is <= 0m ||
            _options.MaximumWeeklyLoss is <= 0m ||
            _options.MaximumConsecutiveLosses is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Configured safety limits must be positive.");
        }
    }

    public TradingSafetySnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                return CreateSnapshot();
            }
        }
    }

    public bool TripOnCriticalDataQualityIssue => _options.TripOnCriticalDataQualityIssue;

    public bool CanOpenNewTrades
    {
        get
        {
            lock (_sync)
            {
                return _state == TradingSafetyState.Active;
            }
        }
    }

    public TradingSafetySnapshot RecordClosedTrade(
        decimal realizedProfitLoss,
        DateTimeOffset timestamp)
    {
        lock (_sync)
        {
            RollPeriods(timestamp);
            _dailyRealizedProfitLoss += realizedProfitLoss;
            _weeklyRealizedProfitLoss += realizedProfitLoss;
            if (realizedProfitLoss < 0m)
            {
                _consecutiveLosses++;
            }
            else if (realizedProfitLoss > 0m)
            {
                _consecutiveLosses = 0;
            }

            if (_options.MaximumDailyLoss is decimal maximumDailyLoss &&
                _dailyRealizedProfitLoss <= -maximumDailyLoss)
            {
                TripCore(
                    SafetyTripReason.DailyLossLimit,
                    $"Daily realised loss {_dailyRealizedProfitLoss:F2} reached the configured limit of " +
                    $"-{maximumDailyLoss:F2}.",
                    timestamp);
            }
            else if (_options.MaximumWeeklyLoss is decimal maximumWeeklyLoss &&
                     _weeklyRealizedProfitLoss <= -maximumWeeklyLoss)
            {
                TripCore(
                    SafetyTripReason.WeeklyLossLimit,
                    $"Weekly realised loss {_weeklyRealizedProfitLoss:F2} reached the configured limit of " +
                    $"-{maximumWeeklyLoss:F2}.",
                    timestamp);
            }
            else if (_options.MaximumConsecutiveLosses is int maximumConsecutiveLosses &&
                     _consecutiveLosses >= maximumConsecutiveLosses)
            {
                TripCore(
                    SafetyTripReason.ConsecutiveLossLimit,
                    $"Consecutive losses reached the configured limit of {maximumConsecutiveLosses}.",
                    timestamp);
            }

            return CreateSnapshot();
        }
    }

    public TradingSafetySnapshot Pause(string message, DateTimeOffset timestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        lock (_sync)
        {
            if (_state != TradingSafetyState.Tripped)
            {
                _state = TradingSafetyState.Paused;
                _reason = SafetyTripReason.Manual;
                _message = message;
                _changedAt = timestamp;
            }

            return CreateSnapshot();
        }
    }

    public TradingSafetySnapshot Resume(DateTimeOffset timestamp)
    {
        lock (_sync)
        {
            if (_state == TradingSafetyState.Paused)
            {
                _state = TradingSafetyState.Active;
                _reason = SafetyTripReason.None;
                _message = null;
                _changedAt = timestamp;
            }

            return CreateSnapshot();
        }
    }

    public TradingSafetySnapshot Trip(
        SafetyTripReason reason,
        string message,
        DateTimeOffset timestamp)
    {
        if (reason == SafetyTripReason.None)
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        lock (_sync)
        {
            TripCore(reason, message, timestamp);
            return CreateSnapshot();
        }
    }

    public TradingSafetySnapshot Reset(DateTimeOffset timestamp)
    {
        lock (_sync)
        {
            _state = TradingSafetyState.Active;
            _reason = SafetyTripReason.None;
            _message = null;
            _changedAt = timestamp;
            _currentDay = null;
            _currentIsoYear = null;
            _currentIsoWeek = null;
            _dailyRealizedProfitLoss = 0m;
            _weeklyRealizedProfitLoss = 0m;
            _consecutiveLosses = 0;
            return CreateSnapshot();
        }
    }

    private void RollPeriods(DateTimeOffset timestamp)
    {
        DateTime utc = timestamp.UtcDateTime;
        DateOnly day = DateOnly.FromDateTime(utc);
        if (_currentDay != day)
        {
            _currentDay = day;
            _dailyRealizedProfitLoss = 0m;
        }

        int isoYear = ISOWeek.GetYear(utc);
        int isoWeek = ISOWeek.GetWeekOfYear(utc);
        if (_currentIsoYear != isoYear || _currentIsoWeek != isoWeek)
        {
            _currentIsoYear = isoYear;
            _currentIsoWeek = isoWeek;
            _weeklyRealizedProfitLoss = 0m;
        }
    }

    private void TripCore(
        SafetyTripReason reason,
        string message,
        DateTimeOffset timestamp)
    {
        _state = TradingSafetyState.Tripped;
        _reason = reason;
        _message = message;
        _changedAt = timestamp;
    }

    private TradingSafetySnapshot CreateSnapshot() => new()
    {
        State = _state,
        Reason = _reason,
        Message = _message,
        ChangedAt = _changedAt,
        DailyRealizedProfitLoss = _dailyRealizedProfitLoss,
        WeeklyRealizedProfitLoss = _weeklyRealizedProfitLoss,
        ConsecutiveLosses = _consecutiveLosses
    };
}
