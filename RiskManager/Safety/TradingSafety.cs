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
    DailyProfitTarget,
    DailyProfitGiveback,
    External
}

public sealed record TradingSafetyOptions
{
    public decimal? MaximumDailyLoss { get; init; }
    public decimal? MaximumWeeklyLoss { get; init; }
    public int? MaximumConsecutiveLosses { get; init; }
    /// <summary>Optional account-currency daily equity profit at which new entries are paused.</summary>
    public decimal? DailyEquityProfitTarget { get; init; }
    /// <summary>Peak daily equity profit required before giveback protection becomes active.</summary>
    public decimal? DailyEquityGivebackActivation { get; init; }
    /// <summary>Maximum account-currency giveback from the daily equity peak.</summary>
    public decimal? MaximumDailyEquityGiveback { get; init; }
    public bool TripOnCriticalDataQualityIssue { get; init; } = true;

    public void Validate()
    {
        if (MaximumDailyLoss is <= 0m ||
            MaximumWeeklyLoss is <= 0m ||
            MaximumConsecutiveLosses is <= 0 ||
            DailyEquityProfitTarget is <= 0m ||
            DailyEquityGivebackActivation is <= 0m ||
            MaximumDailyEquityGiveback is <= 0m ||
            ((DailyEquityGivebackActivation is null) !=
             (MaximumDailyEquityGiveback is null)) ||
            (DailyEquityGivebackActivation is decimal activation &&
             MaximumDailyEquityGiveback is decimal giveback &&
             giveback > activation))
        {
            throw new ArgumentOutOfRangeException(
                nameof(TradingSafetyOptions),
                "Configured safety limits must be positive, daily equity giveback " +
                "activation/amount must either both be supplied or both be omitted, " +
                "and maximum giveback cannot exceed its activation profit.");
        }
    }
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
    public decimal DailyEquityProfitLoss { get; init; }
    public decimal PeakDailyEquityProfitLoss { get; init; }

    public bool CanOpenNewTrades => State == TradingSafetyState.Active;
}

public interface ITradingSafetyController
{
    TradingSafetySnapshot Snapshot { get; }
    bool CanOpenNewTrades { get; }
    bool TripOnCriticalDataQualityIssue { get; }

    TradingSafetySnapshot RecordClosedTrade(decimal realizedProfitLoss, DateTimeOffset timestamp);
    TradingSafetySnapshot ObserveEquity(decimal equity, DateTimeOffset timestamp);
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
    private decimal? _dailyEquityBaseline;
    private decimal _dailyEquityProfitLoss;
    private decimal _peakDailyEquityProfitLoss;

    public TradingSafetyController(TradingSafetyOptions? options = null)
    {
        _options = options ?? new TradingSafetyOptions();
        _options.Validate();
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

    public TradingSafetySnapshot ObserveEquity(decimal equity, DateTimeOffset timestamp)
    {
        lock (_sync)
        {
            RollPeriods(timestamp);
            _dailyEquityBaseline ??= equity;
            _dailyEquityProfitLoss = equity - _dailyEquityBaseline.Value;
            _peakDailyEquityProfitLoss = Math.Max(
                _peakDailyEquityProfitLoss,
                _dailyEquityProfitLoss);

            if (_options.DailyEquityProfitTarget is decimal target &&
                _dailyEquityProfitLoss >= target)
            {
                PauseCore(
                    SafetyTripReason.DailyProfitTarget,
                    $"Daily equity profit {_dailyEquityProfitLoss:F2} reached the configured " +
                    $"target of {target:F2}; new entries are paused until the next UTC day.",
                    timestamp);
            }
            else if (_options.DailyEquityGivebackActivation is decimal activation &&
                     _options.MaximumDailyEquityGiveback is decimal maximumGiveback &&
                     _peakDailyEquityProfitLoss >= activation &&
                     _peakDailyEquityProfitLoss - _dailyEquityProfitLoss >= maximumGiveback)
            {
                PauseCore(
                    SafetyTripReason.DailyProfitGiveback,
                    $"Daily equity gave back {_peakDailyEquityProfitLoss - _dailyEquityProfitLoss:F2} " +
                    $"from its peak after profit protection activated at {activation:F2}; " +
                    "new entries are paused until the next UTC day.",
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
            PauseCore(SafetyTripReason.Manual, message, timestamp);

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
            _dailyEquityBaseline = null;
            _dailyEquityProfitLoss = 0m;
            _peakDailyEquityProfitLoss = 0m;
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
            _dailyEquityBaseline = null;
            _dailyEquityProfitLoss = 0m;
            _peakDailyEquityProfitLoss = 0m;
            if (_state == TradingSafetyState.Paused &&
                _reason is SafetyTripReason.DailyProfitTarget or SafetyTripReason.DailyProfitGiveback)
            {
                _state = TradingSafetyState.Active;
                _reason = SafetyTripReason.None;
                _message = null;
                _changedAt = timestamp;
            }
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

    private void PauseCore(
        SafetyTripReason reason,
        string message,
        DateTimeOffset timestamp)
    {
        if (_state == TradingSafetyState.Tripped)
            return;

        _state = TradingSafetyState.Paused;
        _reason = reason;
        _message = message;
        _changedAt = timestamp;
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
        ConsecutiveLosses = _consecutiveLosses,
        DailyEquityProfitLoss = _dailyEquityProfitLoss,
        PeakDailyEquityProfitLoss = _peakDailyEquityProfitLoss
    };
}
