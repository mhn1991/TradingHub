namespace Agent.Strategies.Alfonso.Zones;

/// <summary>
/// The verdict module 7 asks for on a single zone.
/// <para>
/// The module's title is "Scoring imbalances - how to grade an imbalance to qualify it as a
/// tradeable zone", and its instruction is "We need to somehow come up with a mechanical and
/// straightforward scoring system that clues us in as to what makes a quality zone ... If the
/// particular trade gets a passing score, it must be traded."
/// </para>
/// </summary>
public enum ZoneGrade
{
    /// <summary>Scores at the bottom of the scale. Module 7 scores a weak departure at zero outright.</summary>
    Weak,

    Medium,

    /// <summary>"Both supply levels have a very strong departure, that's the kind of imbalances we are looking to trade."</summary>
    Strong
}

/// <summary>
/// A zone graded against module 7's qualifiers, with the per-qualifier points kept so the verdict
/// can be read back rather than trusted.
/// <para>
/// Module 7 lists five qualifiers: "1. Accomplishments ... 2. Consolidation away ... 3. Strength of
/// the impulse ... 4. Basing structure ... 5. Freshness", and adds the 2:1 minimum under "Minimum
/// reward/risk and profit margin". Two of the six are absolute rather than scored and so carry no
/// points here:
/// </para>
/// <para>
/// Consolidation away is structural - <see cref="ImbalanceDetector"/> only ever emits a zone whose
/// consolidation completed, so "no consolidation away = no imbalance = no trade" is enforced by the
/// zone not existing, and scoring it would award a constant to every zone.
/// </para>
/// <para>
/// The 3:1 profit margin to the opposing level is not a property of the zone at all - it depends on
/// where the nearest opposing zone sits at decision time - so it stays where it is, in
/// <c>AlfonsoSequenceAnalyzer.HasRoomToTarget</c>.
/// </para>
/// </summary>
public sealed record ZoneScore
{
    /// <summary>
    /// What the impulse achieved. "An impulse must accomplish something. Else it will not become an
    /// imbalance." Module 4 lists three routes and notes "All three scenarios can happen at the same
    /// time", so achieving more than one scores higher than achieving exactly one.
    /// </summary>
    public required int Accomplishment { get; init; }

    /// <summary>
    /// The departure. "The move away from a level or impulse is the most essential feature and odds
    /// enhancer." A gap takes the extra point module 7 offers - "You can give a gap away an extra
    /// point if you like because it is the biggest representation of an imbalance"; a weak departure
    /// takes none - "Very low score = 0".
    /// </summary>
    public required int Impulse { get; init; }

    /// <summary>
    /// The base. "We want to see a maximum of 4-6 candlesticks at the base - no matter which
    /// timeframe", and the worked example praises "just four tight candles" while faulting three
    /// other zones for having "a lot of trading, more than six as per the rules".
    /// </summary>
    public required int BaseStructure { get; init; }

    /// <summary>
    /// Freshness. "The first pullback is always the highest odds ... The second pullback does not
    /// have the same odds ... Taking a third pullback to a level is not allowed."
    /// </summary>
    public required int Freshness { get; init; }

    /// <summary>
    /// The 2:1 minimum. "The impulse created after the basing structure has to be twice as wide as
    /// the basing structure ... That is, a minimum 2:1 reward/risk." Pass or fail rather than
    /// graded, because module 7 states it as a threshold: below it the level is "valid but
    /// non-tradable (confirmation needed)".
    /// </summary>
    public required int RewardRisk { get; init; }

    public required ZoneGrade Grade { get; init; }

    public int Total => Accomplishment + Impulse + BaseStructure + Freshness + RewardRisk;

    public override string ToString() =>
        $"{Grade} {Total}/{ZoneScorer.MaximumPoints} " +
        $"(accomplishment {Accomplishment}, impulse {Impulse}, base {BaseStructure}, " +
        $"freshness {Freshness}, 2:1 {RewardRisk})";
}

/// <summary>
/// Grades a zone against module 7's qualifiers.
/// <para>
/// Deliberately a pure function of a zone plus its thresholds, computed on demand rather than stored
/// on <see cref="Imbalance"/>: freshness changes as price tests the level, so a score frozen at
/// creation would be wrong by the time it mattered.
/// </para>
/// <para>
/// What the book supplies and what it does not: the qualifiers, their ordering and the two extreme
/// anchors (weak departure scores zero, a gap earns an extra point) are module 7's. The arithmetic
/// that turns them into one number, and the cut-offs between weak, medium and strong, are not - the
/// module never states them. They are therefore conventions, settable through
/// <see cref="ImbalanceOptions.StrongGradePoints"/> and
/// <see cref="ImbalanceOptions.MediumGradePoints"/>, and the grade gates nothing unless
/// <c>AlfonsoStrategyOptions.MinimumZoneGrade</c> is raised above <see cref="ZoneGrade.Weak"/>.
/// </para>
/// </summary>
public static class ZoneScorer
{
    /// <summary>Highest attainable total: 2 + 3 + 2 + 2 + 1.</summary>
    public const int MaximumPoints = 10;

    public static ZoneScore Score(Imbalance zone, ImbalanceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ImbalanceOptions settings = options ?? new ImbalanceOptions();

        int accomplishments = CountFlags(zone.Accomplished);
        int accomplishment = accomplishments switch
        {
            0 => 0,
            1 => 1,
            _ => 2
        };

        int impulse = zone.Strength switch
        {
            ImpulseStrength.Gap => 3,
            ImpulseStrength.Strong => 2,
            _ => 0
        };

        // "Just four tight candles" is the module's own example of a good base; "more than six as
        // per the rules" is its example of a bad one. The band between the two is the middle score,
        // and it moves with the configured maximum rather than being pinned to a literal six.
        int tight = Math.Max(settings.MinimumBaseCandles, settings.MaximumBaseCandles - 2);
        int baseStructure = zone.BaseCandleCount <= tight
            ? 2
            : zone.BaseCandleCount <= settings.MaximumBaseCandles ? 1 : 0;

        int freshness = zone.State switch
        {
            ImbalanceState.Fresh => 2,
            ImbalanceState.Tested => 1,
            _ => 0
        };

        int rewardRisk = zone.ImpulseToBaseRatio >= settings.MinimumImpulseToBaseRatio ? 1 : 0;

        int total = accomplishment + impulse + baseStructure + freshness + rewardRisk;

        // A zone that accomplished nothing is not an imbalance at all (module 4), and a weak
        // departure is scored at zero by module 7. Neither can be carried to a passing grade by the
        // other qualifiers, so both floor the verdict rather than merely subtracting points.
        ZoneGrade grade = accomplishment == 0 || impulse == 0
            ? ZoneGrade.Weak
            : total >= settings.StrongGradePoints
                ? ZoneGrade.Strong
                : total >= settings.MediumGradePoints
                    ? ZoneGrade.Medium
                    : ZoneGrade.Weak;

        return new ZoneScore
        {
            Accomplishment = accomplishment,
            Impulse = impulse,
            BaseStructure = baseStructure,
            Freshness = freshness,
            RewardRisk = rewardRisk,
            Grade = grade
        };
    }

    /// <summary>Grade alone, for the common case where only the verdict is wanted.</summary>
    public static ZoneGrade Grade(Imbalance zone, ImbalanceOptions? options = null) =>
        Score(zone, options).Grade;

    private static int CountFlags(Accomplishment accomplished)
    {
        int count = 0;
        foreach (Accomplishment flag in Enum.GetValues<Accomplishment>())
        {
            if (flag != Accomplishment.None && accomplished.HasFlag(flag))
                count++;
        }

        return count;
    }
}
