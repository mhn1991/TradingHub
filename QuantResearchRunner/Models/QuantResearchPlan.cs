using Agent.Configuration;
using Brokers.Models;
using QuantResearch.Validation;
using Simulator.Models;

namespace QuantResearchRunner.Models;

/// <summary>Research experiment configuration (audit §14.4).</summary>
public sealed record QuantResearchPlan
{
    public required IReadOnlyList<InstrumentKey> Instruments { get; init; }
    public required IReadOnlyList<string> Strategies { get; init; }
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }
    public required WalkForwardPlan WalkForward { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlyList<decimal>> ParameterGrid { get; init; }
    public required FeatureSwitches FeatureSwitches { get; init; }
    public required string OutputDirectory { get; init; }
    public required int Seed { get; init; }
    /// <summary>
    /// Validation-window drawdown cap used by <c>WalkForwardExperimentRunner</c>'s parameter
    /// selection rule. Null means unconstrained - any candidate within the grid is eligible.
    /// </summary>
    public decimal? MaximumAcceptableDrawdown { get; init; }
    /// <summary>
    /// Base runtime configuration every fold/candidate request is derived from before
    /// <see cref="FeatureSwitches"/> and the parameter grid are applied on top. Not part of
    /// audit §14.4's literal record shape, but required in practice - there is no other
    /// source for annotation/regime/position-management/etc. configuration.
    /// </summary>
    public BacktestRuntimeOptions BaselineRuntime { get; init; } = new();
    public decimal StartingBalance { get; init; } = 100_000m;
    public decimal Quantity { get; init; } = 1_000m;
    /// <summary>
    /// Round-trip execution cost assumptions applied to every fold/candidate request. Defaults
    /// match <see cref="BacktestRequest"/>'s own defaults (unchanged behavior for existing plans),
    /// but previously <see cref="QuantResearchPlan"/> had no way to override them at all -
    /// <c>WalkForwardExperimentRunner.BuildRequest</c> never set these fields on the requests it
    /// built, so every walk-forward run silently used <see cref="BacktestRequest.CommissionRate"/>'s
    /// record default (0.00002, i.e. ~0.2bps/side) regardless of what a caller might reasonably
    /// expect to configure for a given instrument/broker. A plan intending to stress-test a
    /// strategy's edge against realistic retail-FX costs had no field to do that with.
    /// </summary>
    public decimal CommissionRate { get; init; } = 0.00002m;
    public decimal SpreadBasisPoints { get; init; } = 1m;
    public decimal SlippageBasisPoints { get; init; } = 0.5m;
    /// <summary>
    /// Optional master candle series covering the full [<see cref="From"/>, <see cref="To"/>)
    /// range, sliced per fold/window via <c>CandlePrefetchPlanner.SliceForWindow</c>. Used by
    /// tests and offline data; null lets each request fetch normally via
    /// <see cref="BaselineRuntime"/>'s configured source.
    /// </summary>
    public IReadOnlyList<Candle>? InlineCandles { get; init; }

    public void Validate()
    {
        if (Instruments is null || Instruments.Count == 0)
            throw new ArgumentException("At least one instrument is required.");
        if (Strategies is null || Strategies.Count == 0)
            throw new ArgumentException("At least one strategy is required.");
        foreach (string strategy in Strategies)
            _ = TradingAgentTypeIds.Parse(strategy);
        if (From >= To)
            throw new ArgumentException("From must be earlier than To.");
        ArgumentNullException.ThrowIfNull(WalkForward);
        WalkForward.Validate();
        ArgumentNullException.ThrowIfNull(ParameterGrid);
        ArgumentNullException.ThrowIfNull(FeatureSwitches);
        if (string.IsNullOrWhiteSpace(OutputDirectory))
            throw new ArgumentException("An output directory is required.");
        if (MaximumAcceptableDrawdown is < 0m)
            throw new ArgumentOutOfRangeException(nameof(MaximumAcceptableDrawdown));
    }
}
