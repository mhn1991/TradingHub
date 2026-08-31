using Agent.Strategies.Alfonso.Zones;

namespace Agent.Strategies.Alfonso.Sequence;

/// <summary>
/// Module 9's nesting rule: a lower-timeframe zone sitting inside a higher-timeframe one, used to
/// "drill the entry timeframe to a smaller zone at a lower timeframe" and so cut the risk on the
/// entry.
/// </summary>
public static class Nesting
{
    /// <summary>
    /// Whether <paramref name="inner"/> is nested inside <paramref name="outer"/>.
    /// <para>
    /// The test is on the DISTAL line only. Module 9 is explicit that the inner zone may straddle
    /// the outer one: "the D1 DZ may have its proximal line slightly above the proximal line of the
    /// W DZ, subject to the D1 DZ having its distal line within the W DZ." Requiring full
    /// containment would reject exactly the case the course illustrates.
    /// </para>
    /// </summary>
    public static bool IsNested(Imbalance inner, Imbalance outer)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(outer);

        return inner.Kind == outer.Kind && outer.Contains(inner.Distal);
    }

    /// <summary>
    /// The outer zone <paramref name="inner"/> is nested in, preferring the one whose proximal is
    /// nearest - that is the level price reaches first.
    /// </summary>
    public static Imbalance? FindHost(Imbalance inner, IEnumerable<Imbalance> candidates)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(candidates);

        return candidates
            .Where(outer => outer.State != ImbalanceState.Eliminated && IsNested(inner, outer))
            .OrderBy(outer => Math.Abs(outer.Proximal - inner.Proximal))
            .FirstOrDefault();
    }
}
