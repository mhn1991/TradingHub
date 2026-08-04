using Brokers.Models;

namespace Agent.Strategies;

/// <summary>
/// Read-only projection of <see cref="ProgressiveStrategyOptions"/>'s 7 bespoke timeframe fields
/// into the unified <see cref="TimeframePlan"/> shape (PROJECT_STATE.md §4b, Phase 1). Nothing
/// persisted or hashed changes here - <see cref="ProgressiveStrategyOptions"/> remains the real,
/// validated, serialized options type. This is purely a read model for new code (e.g. a future
/// Dashboard timeframe-overview panel) that wants one consistent shape across strategy families.
/// </summary>
public static class ProgressiveStrategyOptionsTimeframeExtensions
{
    public static TimeframePlan ToTimeframePlan(this ProgressiveStrategyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var assignments = new List<TimeframeAssignment>
        {
            new() { Role = TimeframeRole.Context, Interval = options.TrendInterval, Influence = TimeframeInfluence.Gate, Priority = 0 },
            new() { Role = TimeframeRole.Trigger, Interval = options.EntryInterval, Influence = TimeframeInfluence.Gate },
            new() { Role = TimeframeRole.Confirmation, Interval = options.ConfirmationInterval, Influence = TimeframeInfluence.Vote, Priority = 0 }
        };

        int priority = 1;
        foreach (BarInterval interval in options.SecondaryTrendIntervals)
            assignments.Add(new() { Role = TimeframeRole.Context, Interval = interval, Influence = TimeframeInfluence.Vote, Priority = priority++ });

        foreach (BarInterval interval in options.SetupIntervals)
            assignments.Add(new() { Role = TimeframeRole.Setup, Interval = interval, Influence = TimeframeInfluence.Vote });

        priority = 1;
        foreach (BarInterval interval in options.AdditionalConfirmationIntervals)
            assignments.Add(new() { Role = TimeframeRole.Confirmation, Interval = interval, Influence = TimeframeInfluence.Vote, Priority = priority++ });

        // Regime/NeoWave: RegimeInterval/NeoWaveInterval are optional overrides that fall back to
        // TrendInterval when unset (EffectiveRegimeInterval/EffectiveNeoWaveInterval). Fallback
        // priority order expresses that generically instead of via a bespoke `Effective*` property.
        if (options.RegimeInterval is BarInterval regime)
        {
            assignments.Add(new() { Role = TimeframeRole.Regime, Interval = regime, Influence = TimeframeInfluence.Fallback, Priority = 0 });
            assignments.Add(new() { Role = TimeframeRole.Regime, Interval = options.TrendInterval, Influence = TimeframeInfluence.Fallback, Priority = 1 });
        }
        else
        {
            assignments.Add(new() { Role = TimeframeRole.Regime, Interval = options.TrendInterval, Influence = TimeframeInfluence.Fallback, Priority = 0 });
        }

        if (options.NeoWaveInterval is BarInterval neoWave)
        {
            assignments.Add(new() { Role = TimeframeRole.NeoWave, Interval = neoWave, Influence = TimeframeInfluence.Fallback, Priority = 0 });
            assignments.Add(new() { Role = TimeframeRole.NeoWave, Interval = options.TrendInterval, Influence = TimeframeInfluence.Fallback, Priority = 1 });
        }
        else
        {
            assignments.Add(new() { Role = TimeframeRole.NeoWave, Interval = options.TrendInterval, Influence = TimeframeInfluence.Fallback, Priority = 0 });
        }

        return new TimeframePlan { Assignments = assignments };
    }

    /// <summary>
    /// Applies a <see cref="TimeframePlan"/> back onto <paramref name="options"/>, role by role:
    /// a role absent from <paramref name="plan"/> leaves that field(s) on <paramref name="options"/>
    /// untouched. This is the write-side complement of <see cref="ToTimeframePlan"/> - together they
    /// make <see cref="TimeframePlan"/> a genuinely bidirectional API without ProgressiveStrategyOptions
    /// ever changing its own persisted property shape, so nothing that hashes or JSON-serializes this
    /// type (FeatureSchemaHash, ConfigurationHash, AgentDefinition's source-generated JSON context) is
    /// affected - see PROJECT_STATE.md §4b's "Phase 3, revised" for why this replaced the originally
    /// planned persisted-shape migration.
    /// </summary>
    public static ProgressiveStrategyOptions ApplyTimeframePlan(this ProgressiveStrategyOptions options, TimeframePlan plan)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(plan);

        BarInterval trend = plan.First(TimeframeRole.Context, TimeframeInfluence.Gate) ?? options.TrendInterval;
        IReadOnlyList<TimeframeAssignment> contextVotes = plan.For(TimeframeRole.Context, TimeframeInfluence.Vote);
        IReadOnlyList<TimeframeAssignment> setupVotes = plan.For(TimeframeRole.Setup, TimeframeInfluence.Vote);
        IReadOnlyList<TimeframeAssignment> confirmationVotes = plan.For(TimeframeRole.Confirmation, TimeframeInfluence.Vote);
        IReadOnlyList<TimeframeAssignment> regimeFallbacks = plan.For(TimeframeRole.Regime, TimeframeInfluence.Fallback);
        IReadOnlyList<TimeframeAssignment> neoWaveFallbacks = plan.For(TimeframeRole.NeoWave, TimeframeInfluence.Fallback);

        return options with
        {
            TrendInterval = trend,
            SecondaryTrendIntervals = contextVotes.Count > 0
                ? [.. contextVotes.Select(a => a.Interval)]
                : options.SecondaryTrendIntervals,
            SetupIntervals = setupVotes.Count > 0
                ? [.. setupVotes.Select(a => a.Interval)]
                : options.SetupIntervals,
            ConfirmationInterval = confirmationVotes.Count > 0
                ? confirmationVotes[0].Interval
                : options.ConfirmationInterval,
            AdditionalConfirmationIntervals = confirmationVotes.Count > 0
                ? [.. confirmationVotes.Skip(1).Select(a => a.Interval)]
                : options.AdditionalConfirmationIntervals,
            EntryInterval = plan.First(TimeframeRole.Trigger, TimeframeInfluence.Gate) ?? options.EntryInterval,
            // A plan whose highest-priority Regime/NeoWave fallback equals the (possibly just-updated)
            // trend interval is indistinguishable from "unset" (EffectiveRegimeInterval/
            // EffectiveNeoWaveInterval already fall back to TrendInterval), so it's written back as
            // null rather than as a redundant explicit override.
            RegimeInterval = regimeFallbacks.Count > 0
                ? (regimeFallbacks[0].Interval == trend ? null : regimeFallbacks[0].Interval)
                : options.RegimeInterval,
            NeoWaveInterval = neoWaveFallbacks.Count > 0
                ? (neoWaveFallbacks[0].Interval == trend ? null : neoWaveFallbacks[0].Interval)
                : options.NeoWaveInterval
        };
    }
}
