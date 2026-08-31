namespace Agent.Strategies.Alfonso.Sequence;

/// <summary>The role a timeframe plays in a sequence (module 8).</summary>
public enum SequenceRole
{
    /// <summary>"The top timeframe will give us direction ... We will only trade in the direction of this chart."</summary>
    Top,

    /// <summary>"The middle timeframe will give us an intermediate direction."</summary>
    Middle,

    /// <summary>"The lowest timeframe in the sequence will be the execution timeframe."</summary>
    Lower
}

/// <summary>
/// The three timeframes a top-down analysis runs on.
/// <para>
/// Module 8: "Using multiple timeframe analysis with at least three different timeframes will give
/// you a broader view of any market. Using fewer than this can result in a considerable loss of
/// data while using more typically provides redundant analysis and indecision." Three, and exactly
/// three - module 9 adds "Don't add more. If you add more you will always find a reason not to take
/// a trade."
/// </para>
/// <para>
/// Which three is a parameter, not a constant. The course lists five sequences from quarterly down
/// to scalping and is explicit that the choice is the trader's. Nothing in the rules below reads a
/// literal duration; they read roles.
/// </para>
/// </summary>
public sealed record TimeframeSequence
{
    public required TimeSpan Top { get; init; }

    public required TimeSpan Middle { get; init; }

    public required TimeSpan Lower { get; init; }

    /// <summary>Module 8's "weekly sequence (swing)": weekly, daily and H4.</summary>
    public static TimeframeSequence Weekly => new()
    {
        Top = TimeSpan.FromDays(7),
        Middle = TimeSpan.FromDays(1),
        Lower = TimeSpan.FromHours(4)
    };

    /// <summary>Module 8's "daily sequence (intraday)": daily, H4 and H1.</summary>
    public static TimeframeSequence Daily => new()
    {
        Top = TimeSpan.FromDays(1),
        Middle = TimeSpan.FromHours(4),
        Lower = TimeSpan.FromHours(1)
    };

    /// <summary>Module 8's "scalping sequence": H4, H1 and M15.</summary>
    public static TimeframeSequence Scalping => new()
    {
        Top = TimeSpan.FromHours(4),
        Middle = TimeSpan.FromHours(1),
        Lower = TimeSpan.FromMinutes(15)
    };

    public IEnumerable<(SequenceRole Role, TimeSpan Interval)> All()
    {
        yield return (SequenceRole.Top, Top);
        yield return (SequenceRole.Middle, Middle);
        yield return (SequenceRole.Lower, Lower);
    }

    public void Validate()
    {
        if (Top <= Middle || Middle <= Lower)
        {
            throw new InvalidOperationException(
                $"A sequence must run from largest to smallest; got {Top}/{Middle}/{Lower}.");
        }

        if (Lower <= TimeSpan.Zero)
            throw new InvalidOperationException("Timeframes must be positive.");
    }
}
