using Brokers.Models;
using ChartAnnotator.Confluence;
using ChartAnnotator.Engine;
using ChartAnnotator.Liquidity;
using ChartAnnotator.NeoWave;
using ChartAnnotator.Regime;
using ChartAnnotator.SupplyDemand;
using Dashboard.Contracts;

namespace Dashboard.Live;

internal static class WorkspaceAnalysis
{
    public static readonly string[] OandaTimeframes = ["1m", "5m", "15m", "30m", "1h", "4h", "1d"];

    public static ChartAnnotationOptions CreateOptions() => new()
    {
        AtrPeriod = 14,
        RsiPeriod = 14,
        BollingerPeriod = 20,
        BollingerStandardDeviations = 2m,
        SwingLeftBars = 2,
        SwingRightBars = 2,
        HeavyAnalysisEveryCandles = 6,
        // Chart layers need explicit enable — detection profiles default off for backtest identity.
        MarketRegime = new MarketRegimeOptions { Enabled = true },
        NeoWave = new NeoWaveOptions { Enabled = true },
        SupplyDemand = new SupplyDemandCalculationProfile { Enabled = true },
        Liquidity = new LiquidityCalculationProfile { Enabled = true },
        SupplyDemandLiquidityConfluence = new SupplyDemandLiquidityConfluenceOptions { Enabled = true }
    };

    public static BarInterval ParseInterval(string value) => value switch
    {
        "1m" => BarInterval.Minutes(1),
        "5m" => BarInterval.Minutes(5),
        "15m" => BarInterval.Minutes(15),
        "30m" => BarInterval.Minutes(30),
        "1h" => BarInterval.Hours(1),
        "4h" => BarInterval.Hours(4),
        "1d" => BarInterval.Days(1),
        _ => throw new ArgumentException($"Unsupported workspace interval '{value}'.", nameof(value))
    };

    public static int IntervalSeconds(BarInterval interval) => checked((int)(
        interval.AddTo(DateTimeOffset.UnixEpoch) - DateTimeOffset.UnixEpoch).TotalSeconds);

    public static bool IsExpectedForexClosure(DateTimeOffset previous, DateTimeOffset next)
    {
        TimeSpan elapsed = next - previous;
        if (elapsed <= TimeSpan.Zero || elapsed > TimeSpan.FromDays(4))
        {
            return false;
        }

        for (DateTime day = previous.UtcDateTime.Date;
             day <= next.UtcDateTime.Date;
             day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                return true;
            }
        }
        return false;
    }

    public static async Task<(IReadOnlyList<ReplayFrame> Frames, long Gaps)> AnalyzeAsync(
        InstrumentKey instrument,
        BarInterval interval,
        IEnumerable<Candle> candles,
        int frameCapacity,
        CancellationToken cancellationToken)
    {
        var analysis = new LiveAnalysisState(
            instrument,
            interval,
            CreateOptions(),
            frameCapacity);
        foreach (Candle candle in candles.OrderBy(candle => candle.OpenTime))
        {
            await analysis.ProcessAsync(candle, cancellationToken).ConfigureAwait(false);
        }

        return (analysis.Snapshot(), analysis.GapsDetected);
    }
}
