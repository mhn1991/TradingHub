using TrendStatistics.Detection;
using TrendStatistics.Runtime;

namespace TrendStatistics.Trading;

/// <summary>Blueprint section 36's position lifecycle.</summary>
public enum SwingPositionStage { Open, NormalHold, ProfitProtection, Trail, Exit }

public enum SwingExitReason { None, StructuralInvalidation, TrendEnded, ExhaustionTrail, StopLoss }

public sealed record SwingPositionOptions
{
    /// <summary>Section 32: begin monitoring more aggressively here.</summary>
    public decimal ProtectionPercentile { get; init; } = 0.75m;

    /// <summary>Section 32: profit protection.</summary>
    public decimal TrailPercentile { get; init; } = 0.90m;

    /// <summary>
    /// Fraction of the best excursion that may be surrendered once trailing. Section 33 forbids a
    /// hard quantile exit, so the tail is given room to run and is only closed on giveback.
    /// </summary>
    public decimal TrailGivebackFraction { get; init; } = 0.40m;

    /// <summary>Section 37's backstop, in percent from entry. Structural invalidation is preferred.</summary>
    public decimal MaximumAdversePct { get; init; } = 3.0m;

    public void Validate()
    {
        if (ProtectionPercentile is < 0m or > 1m) throw new ArgumentOutOfRangeException(nameof(ProtectionPercentile));
        if (TrailPercentile is < 0m or > 1m) throw new ArgumentOutOfRangeException(nameof(TrailPercentile));
        if (TrailPercentile < ProtectionPercentile)
            throw new ArgumentException("TrailPercentile must be at or above ProtectionPercentile.");
        if (TrailGivebackFraction is <= 0m or > 1m) throw new ArgumentOutOfRangeException(nameof(TrailGivebackFraction));
        if (MaximumAdversePct <= 0m) throw new ArgumentOutOfRangeException(nameof(MaximumAdversePct));
    }
}

public sealed record SwingPositionUpdate(
    SwingPositionStage Stage,
    SwingExitReason ExitReason,
    decimal BestExcursionPct,
    string Reason)
{
    public bool ShouldExit => ExitReason != SwingExitReason.None;
}

/// <summary>
/// Section 36's swing position state machine, with section 37's invalidation-based stop.
/// <para>
/// The staged design is the point. Section 36 explicitly rejects "P95 reached, close immediately",
/// and section 33 explains why: the largest trends may supply most of the profit, so a quantile
/// must tighten management rather than terminate the trade. This machine escalates
/// NormalHold -> ProfitProtection -> Trail and only exits on giveback, structural invalidation, the
/// detector ending the trend, or the backstop.
/// </para>
/// <para>
/// Section 37 prefers invalidation over fixed geometry, so <see cref="SwingPositionOptions.MaximumAdversePct"/>
/// is a backstop rather than the primary stop: the trend detector leaving the trade's direction is
/// what normally closes it.
/// </para>
/// </summary>
public sealed class SwingPositionManager
{
    private readonly SwingPositionOptions _options;
    private SwingExitReason _exitReason;
    private string _exitReasonText = string.Empty;

    public SwingPositionManager(SwingPositionOptions? options = null)
    {
        _options = options ?? new SwingPositionOptions();
        _options.Validate();
    }

    public SwingPositionStage Stage { get; private set; } = SwingPositionStage.Open;
    public decimal BestExcursionPct { get; private set; }

    public SwingPositionUpdate Update(
        TrendDirection direction,
        decimal entryPrice,
        decimal currentPrice,
        TrendState state,
        TrendExhaustionState exhaustion) =>
        Update(direction, entryPrice, currentPrice, currentPrice, currentPrice, state, exhaustion);

    /// <summary>
    /// Updates from a completed candle. High/low, rather than close alone, feed MFE so an
    /// intrabar favorable excursion cannot disappear from the trailing state.
    /// </summary>
    public SwingPositionUpdate Update(
        TrendDirection direction,
        decimal entryPrice,
        decimal currentClose,
        decimal currentHigh,
        decimal currentLow,
        TrendState state,
        TrendExhaustionState exhaustion)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (entryPrice <= 0m)
            throw new ArgumentOutOfRangeException(nameof(entryPrice));
        if (currentClose <= 0m || currentHigh <= 0m || currentLow <= 0m
            || currentHigh < currentClose || currentLow > currentClose || currentLow > currentHigh)
            throw new ArgumentException("Current candle prices are inconsistent.", nameof(currentClose));

        if (Stage == SwingPositionStage.Exit)
            return new SwingPositionUpdate(Stage, _exitReason, BestExcursionPct, _exitReasonText);

        decimal openPct = direction == TrendDirection.Bullish
            ? (currentClose - entryPrice) / entryPrice * 100m
            : (entryPrice - currentClose) / entryPrice * 100m;
        decimal favorablePct = direction == TrendDirection.Bullish
            ? (currentHigh - entryPrice) / entryPrice * 100m
            : (entryPrice - currentLow) / entryPrice * 100m;
        BestExcursionPct = Math.Max(BestExcursionPct, favorablePct);

        // Section 37: the detector leaving this direction IS the invalidation. Checked before any
        // profit logic, because a trade whose thesis is gone should not be managed as if it holds.
        if (state.Direction != direction || state.Phase == TrendPhase.Neutral)
        {
            return Exit(SwingExitReason.StructuralInvalidation,
                $"Detector no longer reports {direction} (phase {state.Phase}); section 37 invalidation.");
        }

        if (openPct <= -_options.MaximumAdversePct)
        {
            return Exit(SwingExitReason.StopLoss,
                $"Open {openPct:F2}% breached the {_options.MaximumAdversePct:F2}% backstop.");
        }

        decimal axis = Math.Max(
            Math.Max(exhaustion.PricePercentile, exhaustion.TimePercentile),
            Math.Max(exhaustion.JointRarity, exhaustion.ExhaustionScore));

        SwingPositionStage desired = axis >= _options.TrailPercentile
            ? SwingPositionStage.Trail
            : axis >= _options.ProtectionPercentile
                ? SwingPositionStage.ProfitProtection
                : SwingPositionStage.NormalHold;
        if (desired > Stage)
            Stage = desired;

        if (Stage == SwingPositionStage.Trail)
        {
            decimal floor = BestExcursionPct * (1m - _options.TrailGivebackFraction);
            // Only trails once actually in profit: a trade at -1% has no gains to protect, and
            // trailing there would exit on noise.
            if (BestExcursionPct > 0m && openPct <= floor)
            {
                return Exit(SwingExitReason.ExhaustionTrail,
                    $"Gave back to {openPct:F2}% from a best of {BestExcursionPct:F2}% while in the " +
                    $"{exhaustion.Level} exhaustion band.");
            }

            return new SwingPositionUpdate(Stage, SwingExitReason.None, BestExcursionPct,
                $"Trailing: axis percentile {axis:F2}, best {BestExcursionPct:F2}%, floor {floor:F2}%.");
        }

        return new SwingPositionUpdate(Stage, SwingExitReason.None, BestExcursionPct,
            $"{Stage}: axis percentile {axis:F2}, open {openPct:F2}%.");
    }

    /// <summary>Called when the detector completes the trend - the natural end of a swing.</summary>
    public SwingPositionUpdate CloseOnTrendEnd()
    {
        if (Stage == SwingPositionStage.Exit)
            return new SwingPositionUpdate(Stage, _exitReason, BestExcursionPct, _exitReasonText);
        return Exit(SwingExitReason.TrendEnded, "Detector completed the trend.");
    }

    private SwingPositionUpdate Exit(SwingExitReason reason, string text)
    {
        Stage = SwingPositionStage.Exit;
        _exitReason = reason;
        _exitReasonText = text;
        return new SwingPositionUpdate(Stage, reason, BestExcursionPct, text);
    }
}
