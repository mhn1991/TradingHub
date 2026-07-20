using Brokers.Models;
using ChartAnnotator.MarketData;

namespace Simulator.Calibration;

/// <summary>
/// The complete effective timeframe setup a calibration request/artifact is bound to. Two
/// requests/artifacts are only comparable (for resume, caching, or overlay application) when
/// this hashes identically - blueprint §7.1/§12: "timeframe topology hash" is part of both the
/// backtest evaluation identity and the compatibility identity checked before an overlay applies.
/// </summary>
public sealed record TimeframeTopology
{
    public required BarInterval ExecutionInterval { get; init; }
    public required BarInterval AnalysisBaseInterval { get; init; }
    public required BarInterval SetupInterval { get; init; }
    public required IReadOnlyList<BarInterval> ConfirmationIntervals { get; init; } = [];
    public required IReadOnlyList<BarInterval> TrendIntervals { get; init; } = [];
    public required IReadOnlyList<BarInterval> ManagementIntervals { get; init; } = [];
    public required BaseCandleGapPolicy AlignmentPolicy { get; init; }
    public required int WarmupMinimumDays { get; init; }

    public void Validate()
    {
        if (!ExecutionInterval.IsValid || !AnalysisBaseInterval.IsValid || !SetupInterval.IsValid)
            throw new ArgumentException("Timeframe topology intervals must be valid.");
        if (ConfirmationIntervals is null || TrendIntervals is null || ManagementIntervals is null)
            throw new ArgumentException("Timeframe topology interval lists are required.");
        if (ConfirmationIntervals.Any(interval => !interval.IsValid) ||
            TrendIntervals.Any(interval => !interval.IsValid) ||
            ManagementIntervals.Any(interval => !interval.IsValid))
        {
            throw new ArgumentException("Every declared timeframe topology interval must be valid.");
        }
        if (!Enum.IsDefined(AlignmentPolicy))
            throw new ArgumentException("Timeframe topology alignment policy is invalid.");
        if (WarmupMinimumDays < 0)
            throw new ArgumentOutOfRangeException(nameof(WarmupMinimumDays));
    }

    /// <summary>
    /// Deterministic regardless of the order these lists were constructed in - two topologies
    /// describing the same effective set of intervals always hash identically.
    /// </summary>
    public string ComputeHash()
    {
        Validate();
        return IndicatorCalibrationHash.ComputeOfObject(new
        {
            ExecutionInterval = ExecutionInterval.ToString(),
            AnalysisBaseInterval = AnalysisBaseInterval.ToString(),
            SetupInterval = SetupInterval.ToString(),
            ConfirmationIntervals = ConfirmationIntervals.Select(interval => interval.ToString())
                .OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            TrendIntervals = TrendIntervals.Select(interval => interval.ToString())
                .OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            ManagementIntervals = ManagementIntervals.Select(interval => interval.ToString())
                .OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            AlignmentPolicy = AlignmentPolicy.ToString(),
            WarmupMinimumDays
        });
    }
}
