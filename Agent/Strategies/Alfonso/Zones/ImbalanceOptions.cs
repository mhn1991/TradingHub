namespace Agent.Strategies.Alfonso.Zones;

/// <summary>
/// Every threshold the zone rules depend on, in one place.
/// <para>
/// The course states these as round numbers ("about 80%", "maximum of 4-6 candlesticks", "twice as
/// wide", "25% of padding"), so the defaults reproduce the text. They are settable because the book
/// is equally clear that they are conventions rather than laws - "Different trading plans can have
/// different Reward Risk criteria, yours could require a minimum 3:1 RR, 2.5 or even 1.5:1" - and
/// because a threshold that cannot be varied cannot be shown to matter.
/// </para>
/// </summary>
public sealed record ImbalanceOptions
{
    /// <summary>
    /// Maximum body-to-range ratio for a candle to count as basing. Module 7: "Tight candle bases
    /// with bodies &lt;= 50% of the candle range. Remember a base is a pause, a wide body or ERC
    /// candle is not a pause."
    /// </summary>
    public decimal MaximumBasingBodyRatio { get; init; } = 0.50m;

    /// <summary>
    /// Minimum body-to-range ratio for an extended range candle. Module 1 defines an ERC as "wide
    /// candlestick bodies covering about 80% of its candle range". Defaulted from
    /// <see cref="AlfonsoBar.ExtendedRangeBodyRatio"/> so the zone and trend layers share one
    /// definition; still settable, because the two layers may legitimately be tuned apart.
    /// </summary>
    public decimal ExtendedRangeBodyRatio { get; init; } = AlfonsoBar.ExtendedRangeBodyRatio;

    /// <summary>
    /// Largest base the rules admit. Module 7: "We want to see a maximum of 4-6 candlesticks at the
    /// base - no matter which timeframe." Anything longer is trading, not a pause.
    /// </summary>
    public int MaximumBaseCandles { get; init; } = 6;

    /// <summary>
    /// Smallest base. A single candle base is explicitly allowed - module 4 discusses adjusting the
    /// proximal line when "There is a single candle at the base".
    /// </summary>
    public int MinimumBaseCandles { get; init; } = 1;

    /// <summary>
    /// How much taller the leg out must be than the zone is wide. Module 7: "The impulse created
    /// after the basing structure has to be twice as wide as the basing structure ... That is, a
    /// minimum 2:1 reward/risk."
    /// </summary>
    public decimal MinimumImpulseToBaseRatio { get; init; } = 2.0m;

    /// <summary>
    /// How far the leg out must carry price, in ATR of the same timeframe. Module 7: "The impulse
    /// created after the basing structure has to be twice as wide as the basing structure ... It
    /// also has to be made of at least two ERCs."
    /// <para>
    /// Expressed as distance rather than as a count of candles. The rule is about how far price
    /// travelled leaving the base, so one candle covering what two would cover satisfies it - and
    /// did it faster, which module 7 ranks higher, not lower. Counting literal candles also fails
    /// for a reason unrelated to the strategy: extended range candles are about 9% of gold bars at
    /// these timeframes, and two CONSECUTIVE ones occurred 4 times in 1,018 H4 bars.
    /// </para>
    /// </summary>
    public decimal MinimumImpulseAtrMultiple { get; init; } = 2.0m;

    /// <summary>
    /// Candles the impulse has to cover that distance in. This is what keeps the test a measure of
    /// departure rather than of eventual travel - module 7's weak impulse is "you pushing a car up
    /// a hill", a leg that gets there slowly, and without a bound a slow drift would eventually
    /// clear any distance and score as strong.
    /// </summary>
    public int ImpulseSpeedCandles { get; init; } = 2;

    /// <summary>
    /// Candles averaged for the ATR that sets the timeframe's scale. The scale has to be local: an
    /// ERC on M15 gold and one on H4 gold are different distances, and a fixed price threshold would
    /// silently mean different things on each.
    /// </summary>
    public int AtrLookbackCandles { get; init; } = 14;

    /// <summary>
    /// Full candles that must close clear of the zone before it is confirmed. Module 4: "An
    /// imbalance will not be confirmed if price returns to the origin of the move in the very next
    /// candlestick" and module 7 requires "at least one or more full OCHL candles" away.
    /// </summary>
    public int ConsolidationAwayCandles { get; init; } = 1;

    /// <summary>
    /// How many candles after the base the leg out may take to reach its extreme. Bounded so a slow
    /// drift that eventually travels far cannot be scored as an impulse; the course's impulses are
    /// measured in a handful of candles.
    /// </summary>
    public int MaximumImpulseCandles { get; init; } = 10;

    /// <summary>
    /// Candles scanned before the base for the leg in, used only to tell a swing from a
    /// continuation pattern.
    /// <para>
    /// Deliberately short. The leg in is the approach into the base, not the prevailing trend: over
    /// a long window a demand zone at the foot of a three-bar pullback inside an uptrend nets out
    /// positive and is misread as a continuation. On real H4 gold a six-candle window classified 23
    /// of 31 zones as continuations, which starves the trend layer of the swings it needs - module 3
    /// forbids building trendlines from continuation patterns, so nothing could be drawn at all.
    /// </para>
    /// </summary>
    public int LegInLookbackCandles { get; init; } = 5;

    /// <summary>
    /// Minimum gap, as a fraction of the zone width, for a departure to be scored as a gap rather
    /// than merely strong. Zero would make every non-touching bar a gap.
    /// </summary>
    public decimal MinimumGapToWidthRatio { get; init; } = 0.10m;

    /// <summary>
    /// Fraction of the zone width added beyond the distal line for protection. Module 10's worked
    /// examples use 25%.
    /// </summary>
    public decimal StopPaddingFraction { get; init; } = 0.25m;

    /// <summary>
    /// Fixed reward multiple. Module 11: "Exit at a fixed target of 3:1, three times the width of
    /// the imbalance including the padding."
    /// </summary>
    public decimal RewardMultiple { get; init; } = 3.0m;

    /// <summary>
    /// Completed tests after which a zone is no longer tradeable. Module 7: "Taking a third pullback
    /// to a level is not allowed", so two completed tests exhaust it.
    /// </summary>
    public int MaximumTests { get; init; } = 2;

    /// <summary>
    /// Live zones retained per timeframe, as a memory bound rather than a behavioural knob.
    /// <para>
    /// Module 4 says to "go as far back as you need to in order to look for imbalances", and a zone
    /// only stops mattering when price eliminates it - which the engine already handles. Trimming
    /// live zones therefore discards setups the rules still consider valid, silently. At 200 the cap
    /// was binding on one-minute data (peak live hit exactly 200) while never approaching it at the
    /// timeframes actually traded (19 to 69 at H4 through M15), so it was invisible where it applied
    /// and irrelevant where it did not.
    /// </para>
    /// </summary>
    public int MaximumTrackedZones { get; init; } = 5_000;

    /// <summary>
    /// When true the proximal line covers the basing wicks instead of the extreme body edge. Module
    /// 4 offers both, gated on the base being tight: "Option 2: proximal line covering the lower
    /// shadows since both bodies and wicks are very tight and small."
    /// </summary>
    public bool ProximalCoversWicks { get; init; } = false;

    /// <summary>
    /// Whether a structure must have accomplished something to be TRADEABLE. Module 4 makes this
    /// compulsory for an imbalance - an impulse that broke no trendline, removed no opposing zone
    /// and took out no extreme is not one.
    /// <para>
    /// Note what this does not do: it does not stop the structure being tracked. Modules 2 and 3
    /// treat valleys and peaks as price-action features - "Connect the latest two obvious valleys
    /// (swing lows) and peaks (swing highs)" - and never require them to be validated imbalances
    /// first. Conflating the two deadlocks the whole method: zones need an accomplishment, the
    /// principal accomplishment is a trendline break, trendlines are drawn from swings, and swings
    /// come from zones. Applying the gate at detection left 10 swings in eight months of H4 gold,
    /// no drawable trendline, and therefore zero trendline-break accomplishments ever awarded.
    /// </para>
    /// </summary>
    public bool RequireAccomplishment { get; init; } = true;

    /// <summary>
    /// Whether elimination needs a CLOSE beyond the distal line rather than a wick through it.
    /// <para>
    /// Defaults to the literal Module 4 rule: "An imbalance is eliminated if the lowest low or the
    /// highest high of the basing structure has been penetrated through by as little as a tick or a
    /// pip." Requiring the candle to close beyond the distal is retained as an optional stricter
    /// interpretation for controlled comparisons.
    /// </para>
    /// </summary>
    public bool EliminationRequiresClose { get; init; } = false;

    /// <summary>
    /// Whether the impulse taking out a prior peak or valley counts as an accomplishment.
    /// <para>
    /// A valid zone is one that "itself eliminated a prior opposing zone, or whose move broke a
    /// trendline or a peak / valley". The all-time-extreme route the English text describes is a
    /// special case of this and far rarer - on eight months of H4 gold it fired 15 times, while
    /// swings are taken out constantly.
    /// </para>
    /// </summary>
    public bool SwingBreakIsAnAccomplishment { get; init; } = true;

    /// <summary>
    /// Whether a base with no readable approach is treated as a continuation pattern rather than a
    /// swing. Module 2: "When you are in doubt, consider them as a CP."
    /// <para>
    /// Default false, which is the behaviour every result before 2026-09-01 was measured under.
    /// Turning it on is faithful to the book's sentence but it is not a small change: it feeds
    /// swing detection, which feeds trendlines, which feeds trend establishment. Measured on six
    /// instruments it cut trade count 127 -> 40 and took pooled avgR from -0.239 to -0.474, so it
    /// stays off until there is evidence for it.
    /// </para>
    /// </summary>
    public bool TreatAmbiguousBaseAsContinuation { get; init; } = false;

    /// <summary>
    /// Whether the drop/rally base spans both extended range candles rather than only the turning
    /// one. Module 2 states the alternative base outright: "the basing structure of a valley may be
    /// formed by non 50% candlesticks and be made of only a bearish ERC and a bullish ERC
    /// (drop/rally)" - the structure IS the pair. Module 4 then binds the geometry to it: "The
    /// distal line of an imbalance must always include the lowest low in the basing structure when
    /// drawing a demand level and the highest high when drawing a supply level."
    /// <para>
    /// Reading the base as one candle leaves the opposing ERC's extreme outside the zone, and the
    /// omission has a direction: a bearish ERC closes within 20% of its range of its low by
    /// definition, so its low usually sits below the following bullish ERC's low. The distal then
    /// sits too tight and a wick still inside the book's base eliminates the zone. Measured over
    /// ~75,000 H4 bars (see 3.53 and <c>tools/alfonso_droprally_distal.py</c>): 384 drop/rally
    /// bases, 48% of them with a different distal, median omission 0.14 zone widths.
    /// </para>
    /// <para>
    /// Default true because the book states it rather than offering it. Set false to restore the
    /// single-candle reading every result before 2026-09-05 was measured under.
    /// </para>
    /// </summary>
    public bool DropRallyBaseSpansBothCandles { get; init; } = true;

    /// <summary>Peaks and valleys retained for the swing-break test.</summary>
    public int SwingMemory { get; init; } = 16;

    /// <summary>
    /// Where inside the zone the entry is planned. Module 10 offers both on the same worked example:
    /// "Take the full imbalance based on your entry timeframe ... We would plan the entry at weekly
    /// demand proximal line at $46.25" or "Use half the width of the original imbalance. The entry
    /// would be around $45.15", and module 11 repeats the pair for an IPO with no history behind it -
    /// "Buy the whole imbalance or half of it."
    /// <para>
    /// Protection does not move with it: the stop stays beyond the distal line by
    /// <see cref="StopPaddingFraction"/>, so a half entry is a smaller risk and a nearer target, at
    /// the cost of the fills where price turns in the first half of the zone.
    /// </para>
    /// </summary>
    public ZoneEntryPlacement EntryPlacement { get; init; } = ZoneEntryPlacement.Proximal;

    /// <summary>
    /// Points at which <see cref="ZoneScorer"/> calls a zone strong. Not a course figure - module 7
    /// names the qualifiers and the two extremes but never totals them - so it is a convention, and
    /// it gates nothing unless a minimum grade is configured.
    /// </summary>
    public int StrongGradePoints { get; init; } = 8;

    /// <summary>Points at which a zone is graded medium rather than weak. Also a convention.</summary>
    public int MediumGradePoints { get; init; } = 5;

    public void Validate()
    {
        if (MaximumBasingBodyRatio is <= 0m or > 1m)
            throw new InvalidOperationException("MaximumBasingBodyRatio must be within (0, 1].");
        if (ExtendedRangeBodyRatio is <= 0m or > 1m)
            throw new InvalidOperationException("ExtendedRangeBodyRatio must be within (0, 1].");
        if (ExtendedRangeBodyRatio <= MaximumBasingBodyRatio)
        {
            throw new InvalidOperationException(
                "ExtendedRangeBodyRatio must exceed MaximumBasingBodyRatio, otherwise one candle " +
                "could be both a pause and an impulse.");
        }

        if (MinimumBaseCandles < 1)
            throw new InvalidOperationException("MinimumBaseCandles must be at least 1.");
        if (MaximumBaseCandles < MinimumBaseCandles)
            throw new InvalidOperationException("MaximumBaseCandles must be at least MinimumBaseCandles.");
        if (MinimumImpulseToBaseRatio <= 0m)
            throw new InvalidOperationException("MinimumImpulseToBaseRatio must be positive.");
        if (MinimumImpulseAtrMultiple <= 0m)
            throw new InvalidOperationException("MinimumImpulseAtrMultiple must be positive.");
        if (ImpulseSpeedCandles < 1)
            throw new InvalidOperationException("ImpulseSpeedCandles must be at least 1.");
        if (AtrLookbackCandles < 1)
            throw new InvalidOperationException("AtrLookbackCandles must be at least 1.");
        if (ConsolidationAwayCandles < 1)
            throw new InvalidOperationException("ConsolidationAwayCandles must be at least 1.");
        if (MaximumImpulseCandles < 1)
            throw new InvalidOperationException("MaximumImpulseCandles must be at least 1.");
        if (LegInLookbackCandles < 1)
            throw new InvalidOperationException("LegInLookbackCandles must be at least 1.");
        if (StopPaddingFraction < 0m)
            throw new InvalidOperationException("StopPaddingFraction cannot be negative.");
        if (RewardMultiple <= 0m)
            throw new InvalidOperationException("RewardMultiple must be positive.");
        if (MaximumTests < 1)
            throw new InvalidOperationException("MaximumTests must be at least 1.");
        if (MaximumTrackedZones < 1)
            throw new InvalidOperationException("MaximumTrackedZones must be at least 1.");
        if (MediumGradePoints < 1 || MediumGradePoints > ZoneScorer.MaximumPoints)
        {
            throw new InvalidOperationException(
                $"MediumGradePoints must be within [1, {ZoneScorer.MaximumPoints}].");
        }

        if (StrongGradePoints < MediumGradePoints || StrongGradePoints > ZoneScorer.MaximumPoints)
        {
            throw new InvalidOperationException(
                $"StrongGradePoints must be within [MediumGradePoints, {ZoneScorer.MaximumPoints}].");
        }
    }
}
