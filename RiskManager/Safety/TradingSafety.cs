using System.Globalization;
using System.Text.Json.Serialization;

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
    External,
    EquityProtectionTier
}

/// <summary>
/// Action a persistent (non-daily-resetting) equity-protection tier takes once its
/// activation profit and maximum-giveback conditions are both met. PauseNewEntries and
/// ReduceFutureRisk are enforced entirely inside <see cref="TradingSafetyController"/>;
/// ReduceOpenPositions/FlattenAllPositions have no position visibility here and are
/// surfaced via <see cref="EquityHighWatermarkSnapshot.ActivatedTierIds"/> for the
/// caller to translate into a directive for the position-management layer.
/// </summary>
public enum EquityProtectionAction
{
    PauseNewEntries,
    ReduceOpenPositions,
    FlattenAllPositions,
    ReduceFutureRisk
}

/// <summary>
/// A single persistent equity-protection threshold. Activates once realised+unrealised
/// equity has reached <see cref="ActivationProfitAmount"/>/<see cref="ActivationProfitPercent"/>
/// profit above the starting equity AND has given back
/// <see cref="MaximumGivebackAmount"/>/<see cref="MaximumGivebackPercent"/> from its peak.
/// Unlike the daily giveback pair above, this never resets on a day boundary.
/// </summary>
public sealed record EquityProtectionTier
{
    public required string TierId { get; init; }
    public decimal? ActivationProfitAmount { get; init; }
    public decimal? ActivationProfitPercent { get; init; }
    public decimal? MaximumGivebackAmount { get; init; }
    public decimal? MaximumGivebackPercent { get; init; }
    public required EquityProtectionAction Action { get; init; }
    public decimal ReductionFraction { get; init; }
    public decimal FutureRiskMultiplier { get; init; } = 1m;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(TierId) ||
            ActivationProfitAmount is <= 0m ||
            ActivationProfitPercent is <= 0m ||
            (MaximumGivebackAmount is null && MaximumGivebackPercent is null) ||
            MaximumGivebackAmount is <= 0m ||
            MaximumGivebackPercent is <= 0m ||
            !Enum.IsDefined(Action) ||
            (Action == EquityProtectionAction.ReduceOpenPositions &&
                ReductionFraction is <= 0m or >= 1m) ||
            // Phase 1 constraint (spec §19.6/§49): adaptive risk must never exceed base risk.
            FutureRiskMultiplier is < 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(EquityProtectionTier),
                "Each tier requires a unique id, at least one positive maximum-giveback " +
                "threshold, a valid action, a (0,1) reduction fraction when reducing open " +
                "positions, and a future-risk multiplier within [0, 1].");
        }
    }
}

/// <summary>Configuration for persistent (non-daily) equity high-watermark protection.</summary>
public sealed record EquityProtectionOptions
{
    public bool Enabled { get; init; }
    public IReadOnlyList<EquityProtectionTier> Tiers { get; init; } = [];

    /// <summary>Consecutive equity observations inside the recovery zone before a paused/reduced state resumes.</summary>
    public int RecoveryConfirmationBars { get; init; } = 3;

    /// <summary>Drawdown-from-peak percent (0-100) that counts as "recovered".</summary>
    public decimal RecoveryDrawdownPercentThreshold { get; init; } = 0m;

    /// <summary>When true, full recovery additionally requires a new equity high, not just a reduced drawdown.</summary>
    public bool RequireNewEquityHighForFullRecovery { get; init; }

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Tiers);
        if (RecoveryConfirmationBars < 1 ||
            RecoveryDrawdownPercentThreshold is < 0m or > 100m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(EquityProtectionOptions),
                "RecoveryConfirmationBars must be >= 1 and RecoveryDrawdownPercentThreshold must be 0-100.");
        }

        foreach (EquityProtectionTier tier in Tiers)
        {
            tier.Validate();
        }

        if (Tiers.Select(tier => tier.TierId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Tiers.Count)
        {
            throw new ArgumentException("Equity-protection tier ids must be unique.");
        }
    }
}

/// <summary>
/// Live persistent equity-protection state. Snapshotted every frame alongside the
/// existing daily <see cref="TradingSafetySnapshot"/> fields.
/// </summary>
public sealed record EquityHighWatermarkSnapshot
{
    public static EquityHighWatermarkSnapshot Empty { get; } = new()
    {
        StartingEquity = 0m,
        CurrentEquity = 0m,
        PeakEquity = 0m,
        DrawdownFromPeak = 0m,
        DrawdownPercent = 0m,
        ProtectedFloor = 0m,
        ActivatedTierIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        CurrentRiskMultiplier = 1m
    };

    public required decimal StartingEquity { get; init; }
    public required decimal CurrentEquity { get; init; }
    public required decimal PeakEquity { get; init; }
    public required decimal DrawdownFromPeak { get; init; }
    public required decimal DrawdownPercent { get; init; }
    public required decimal ProtectedFloor { get; init; }
    [JsonConverter(typeof(ReadOnlyStringSetJsonConverter))]
    public required IReadOnlySet<string> ActivatedTierIds { get; init; }
    public decimal CurrentRiskMultiplier { get; init; } = 1m;

    /// <summary>
    /// The most severe currently-active position-level action (ReduceOpenPositions or
    /// FlattenAllPositions), if any tier calls for one. Null when no active tier requests
    /// a position action - PauseNewEntries/ReduceFutureRisk are enforced entirely inside
    /// the safety controller and don't need this.
    /// </summary>
    public EquityProtectionAction? PendingPositionAction { get; init; }
    public string? PendingPositionTierId { get; init; }
    public decimal PendingPositionReductionFraction { get; init; }

    /// <summary>Distinct tiers activated at least once so far (cumulative - never shrinks on recovery).</summary>
    public int TotalActivatedTierCount { get; init; }
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

    /// <summary>Persistent (non-daily-resetting) equity high-watermark protection.</summary>
    public EquityProtectionOptions EquityProtection { get; init; } = new();

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

        ArgumentNullException.ThrowIfNull(EquityProtection);
        EquityProtection.Validate();
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
    public EquityHighWatermarkSnapshot EquityProtection { get; init; } = EquityHighWatermarkSnapshot.Empty;

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
    TradingSafetySnapshot Restore(TradingSafetySnapshot snapshot, DateTimeOffset timestamp);
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
    private decimal? _startingEquity;
    private decimal _peakEquity;
    private decimal _currentEquity;
    private readonly HashSet<string> _activatedEquityProtectionTierIds = new(StringComparer.OrdinalIgnoreCase);
    private int _equityProtectionRecoveryBars;
    private bool _pausedForEquityProtection;
    private decimal _equityProtectionRiskMultiplier = 1m;
    private EquityProtectionTier? _pendingPositionTier;
    private readonly HashSet<string> _everActivatedEquityProtectionTierIds = new(StringComparer.OrdinalIgnoreCase);

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

            EvaluateEquityProtection(equity, timestamp);

            return CreateSnapshot();
        }
    }

    /// <summary>
    /// Persistent (non-daily-resetting) equity high-watermark protection. Runs every
    /// <see cref="ObserveEquity"/> call, after the existing daily logic above, using the
    /// same cadence but never resetting on a day boundary.
    ///
    /// Activated tiers latch: once a tier's threshold is breached it stays in
    /// <see cref="_activatedEquityProtectionTierIds"/> even on a bar where the raw
    /// threshold is momentarily no longer met, and only clears once
    /// <see cref="EquityProtectionOptions.RecoveryConfirmationBars"/> consecutive bars
    /// confirm recovery - a single better bar must not instantly restore full risk.
    /// </summary>
    private void EvaluateEquityProtection(decimal equity, DateTimeOffset timestamp)
    {
        if (!_options.EquityProtection.Enabled)
        {
            return;
        }

        _startingEquity ??= equity;
        _peakEquity = Math.Max(_peakEquity, equity);
        _currentEquity = equity;

        decimal profitAboveStart = _peakEquity - _startingEquity.Value;
        decimal givebackFromPeak = _peakEquity - _currentEquity;

        var rawActive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (EquityProtectionTier tier in _options.EquityProtection.Tiers)
        {
            decimal? activationThreshold = tier.ActivationProfitAmount ??
                (tier.ActivationProfitPercent is decimal activationPercent
                    ? _startingEquity.Value * activationPercent / 100m
                    : null);
            decimal? givebackThreshold = tier.MaximumGivebackAmount ??
                (tier.MaximumGivebackPercent is decimal givebackPercent
                    ? _peakEquity * givebackPercent / 100m
                    : null);

            bool activated = (activationThreshold is null || profitAboveStart >= activationThreshold.Value) &&
                givebackThreshold is decimal threshold && givebackFromPeak >= threshold;
            if (activated)
            {
                rawActive.Add(tier.TierId);
            }
        }

        if (rawActive.Count > 0)
        {
            // A fresh or renewed breach: latch it in and reset the recovery counter.
            _activatedEquityProtectionTierIds.UnionWith(rawActive);
            _everActivatedEquityProtectionTierIds.UnionWith(rawActive);
            _equityProtectionRecoveryBars = 0;
        }
        else if (_activatedEquityProtectionTierIds.Count > 0)
        {
            decimal drawdownPercent = _peakEquity > 0m ? givebackFromPeak / _peakEquity * 100m : 0m;
            bool recovered = drawdownPercent <= _options.EquityProtection.RecoveryDrawdownPercentThreshold &&
                (!_options.EquityProtection.RequireNewEquityHighForFullRecovery || _currentEquity >= _peakEquity);

            if (!recovered)
            {
                _equityProtectionRecoveryBars = 0;
            }
            else if (++_equityProtectionRecoveryBars >= _options.EquityProtection.RecoveryConfirmationBars)
            {
                _activatedEquityProtectionTierIds.Clear();
                _equityProtectionRecoveryBars = 0;
                if (_pausedForEquityProtection && _state == TradingSafetyState.Paused)
                {
                    _state = TradingSafetyState.Active;
                    _reason = SafetyTripReason.None;
                    _message = null;
                    _changedAt = timestamp;
                    _pausedForEquityProtection = false;
                }
            }
        }

        // Recompute the effective pause/multiplier/position-action state from whichever
        // tiers are currently latched active (freshly breached or still pending recovery).
        bool anyPause = false;
        decimal riskMultiplier = 1m;
        EquityProtectionTier? mostSeverePositionTier = null;
        foreach (EquityProtectionTier tier in _options.EquityProtection.Tiers)
        {
            if (!_activatedEquityProtectionTierIds.Contains(tier.TierId))
            {
                continue;
            }

            switch (tier.Action)
            {
                case EquityProtectionAction.PauseNewEntries:
                    anyPause = true;
                    break;
                case EquityProtectionAction.ReduceFutureRisk:
                    riskMultiplier *= Math.Clamp(tier.FutureRiskMultiplier, 0m, 1m);
                    break;
                case EquityProtectionAction.ReduceOpenPositions:
                case EquityProtectionAction.FlattenAllPositions:
                    // FlattenAllPositions is strictly more severe than ReduceOpenPositions;
                    // only the single most severe active position action is surfaced.
                    if (mostSeverePositionTier is null ||
                        (tier.Action == EquityProtectionAction.FlattenAllPositions &&
                            mostSeverePositionTier.Action != EquityProtectionAction.FlattenAllPositions))
                    {
                        mostSeverePositionTier = tier;
                    }

                    break;
            }
        }

        _equityProtectionRiskMultiplier = riskMultiplier;
        _pendingPositionTier = mostSeverePositionTier;

        if (anyPause && _state == TradingSafetyState.Active)
        {
            string tierList = string.Join(", ", _activatedEquityProtectionTierIds);
            PauseCore(
                SafetyTripReason.EquityProtectionTier,
                $"Equity gave back {givebackFromPeak:F2} from its persistent peak of {_peakEquity:F2}; " +
                $"tier(s) [{tierList}] paused new entries pending recovery.",
                timestamp);
            _pausedForEquityProtection = true;
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

    public TradingSafetySnapshot Restore(TradingSafetySnapshot snapshot, DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_sync)
        {
            _state = snapshot.State;
            _reason = snapshot.Reason;
            _message = snapshot.Message;
            _changedAt = snapshot.ChangedAt ?? timestamp;
            DateTime utc = timestamp.UtcDateTime;
            _currentDay = DateOnly.FromDateTime(utc);
            _currentIsoYear = ISOWeek.GetYear(utc);
            _currentIsoWeek = ISOWeek.GetWeekOfYear(utc);
            _dailyRealizedProfitLoss = snapshot.DailyRealizedProfitLoss;
            _weeklyRealizedProfitLoss = snapshot.WeeklyRealizedProfitLoss;
            _consecutiveLosses = snapshot.ConsecutiveLosses;
            _dailyEquityProfitLoss = snapshot.DailyEquityProfitLoss;
            _peakDailyEquityProfitLoss = snapshot.PeakDailyEquityProfitLoss;
            _currentEquity = snapshot.EquityProtection.CurrentEquity;
            _dailyEquityBaseline = _currentEquity - _dailyEquityProfitLoss;
            _startingEquity = snapshot.EquityProtection.StartingEquity > 0m
                ? snapshot.EquityProtection.StartingEquity
                : null;
            _peakEquity = snapshot.EquityProtection.PeakEquity;
            _activatedEquityProtectionTierIds.Clear();
            _activatedEquityProtectionTierIds.UnionWith(snapshot.EquityProtection.ActivatedTierIds);
            _everActivatedEquityProtectionTierIds.Clear();
            _everActivatedEquityProtectionTierIds.UnionWith(snapshot.EquityProtection.ActivatedTierIds);
            _equityProtectionRiskMultiplier = Math.Clamp(
                snapshot.EquityProtection.CurrentRiskMultiplier, 0m, 1m);
            _pausedForEquityProtection = snapshot.Reason == SafetyTripReason.EquityProtectionTier;
            _pendingPositionTier = snapshot.EquityProtection.PendingPositionTierId is string tierId
                ? _options.EquityProtection.Tiers.FirstOrDefault(tier => tier.TierId == tierId)
                : null;
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
            _startingEquity = null;
            _peakEquity = 0m;
            _currentEquity = 0m;
            _activatedEquityProtectionTierIds.Clear();
            _everActivatedEquityProtectionTierIds.Clear();
            _equityProtectionRecoveryBars = 0;
            _pausedForEquityProtection = false;
            _equityProtectionRiskMultiplier = 1m;
            _pendingPositionTier = null;
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
        PeakDailyEquityProfitLoss = _peakDailyEquityProfitLoss,
        EquityProtection = CreateEquityProtectionSnapshot()
    };

    private EquityHighWatermarkSnapshot CreateEquityProtectionSnapshot()
    {
        if (_startingEquity is not decimal startingEquity)
        {
            return EquityHighWatermarkSnapshot.Empty;
        }

        decimal givebackFromPeak = _peakEquity - _currentEquity;
        decimal drawdownPercent = _peakEquity > 0m ? givebackFromPeak / _peakEquity * 100m : 0m;
        decimal? givebackFloor = _pendingPositionTier?.MaximumGivebackAmount ??
            (_pendingPositionTier?.MaximumGivebackPercent is decimal percent
                ? _peakEquity * percent / 100m
                : null);

        return new EquityHighWatermarkSnapshot
        {
            StartingEquity = startingEquity,
            CurrentEquity = _currentEquity,
            PeakEquity = _peakEquity,
            DrawdownFromPeak = givebackFromPeak,
            DrawdownPercent = drawdownPercent,
            ProtectedFloor = givebackFloor is decimal floor ? _peakEquity - floor : _currentEquity,
            ActivatedTierIds = new HashSet<string>(_activatedEquityProtectionTierIds, StringComparer.OrdinalIgnoreCase),
            CurrentRiskMultiplier = _equityProtectionRiskMultiplier,
            PendingPositionAction = _pendingPositionTier?.Action,
            PendingPositionTierId = _pendingPositionTier?.TierId,
            PendingPositionReductionFraction = _pendingPositionTier?.ReductionFraction ?? 0m,
            TotalActivatedTierCount = _everActivatedEquityProtectionTierIds.Count
        };
    }
}
