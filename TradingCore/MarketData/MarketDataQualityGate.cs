using System.Collections.Concurrent;
using Agent.Models;
using Brokers.Models;
using ChartAnnotator.Models;

namespace TradingCore.MarketData;

public enum DataQualitySeverity
{
    Warning,
    Error,
    Critical
}

public sealed record DataQualityIssue(
    string Code,
    DataQualitySeverity Severity,
    string Message,
    BarInterval? Interval = null);

public sealed record DataQualityResult
{
    public required bool IsValid { get; init; }
    public required bool ShouldTrip { get; init; }
    public required IReadOnlyList<DataQualityIssue> Issues { get; init; }

    public static DataQualityResult Valid { get; } = new()
    {
        IsValid = true,
        ShouldTrip = false,
        Issues = []
    };
}

public sealed record MarketDataQualityOptions
{
    public bool RequireCompleteCandles { get; init; } = true;
    public bool RequireIndicatorsReady { get; init; }
    public bool RejectGaps { get; init; } = true;
    public bool RejectFutureSnapshots { get; init; } = true;
    public TimeSpan MaximumFutureClockSkew { get; init; } = TimeSpan.FromSeconds(2);
    public int MaximumStalenessIntervals { get; init; } = 2;
}

public interface IMarketDataQualityGate
{
    DataQualityResult Evaluate(MultiTimeframeAnalysis analysis);
    void Reset(InstrumentKey? instrument = null);
}

public sealed class MarketDataQualityGate : IMarketDataQualityGate
{
    private readonly MarketDataQualityOptions _options;
    private readonly ConcurrentDictionary<ChartKey, Cursor> _cursors = [];

    public MarketDataQualityGate(MarketDataQualityOptions? options = null)
    {
        _options = options ?? new MarketDataQualityOptions();
        if (_options.MaximumFutureClockSkew < TimeSpan.Zero ||
            _options.MaximumStalenessIntervals < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    public DataQualityResult Evaluate(MultiTimeframeAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        var issues = new List<DataQualityIssue>();
        var commits = new List<(ChartKey Key, Cursor Cursor)>();

        if (analysis.Instrument.IsEmpty)
        {
            issues.Add(new DataQualityIssue(
                "instrument.empty",
                DataQualitySeverity.Critical,
                "The multi-timeframe analysis does not identify an instrument."));
        }

        if (analysis.Timeframes.Count == 0)
        {
            issues.Add(new DataQualityIssue(
                "timeframes.empty",
                DataQualitySeverity.Critical,
                "The multi-timeframe analysis does not contain any snapshots."));
        }

        foreach ((BarInterval interval, AnalysisSnapshot snapshot) in analysis.Timeframes)
        {
            ChartKey key = new(analysis.Instrument, interval);
            ValidateSnapshot(analysis, interval, snapshot, issues);
            Cursor previous = _cursors.TryGetValue(key, out Cursor? cursor)
                ? cursor!
                : Cursor.Empty;
            ValidateSequence(interval, snapshot, previous, issues);
            commits.Add((key, new Cursor(snapshot.Version, snapshot.AvailableAt, snapshot.LatestCandle.OpenTime)));
        }

        bool valid = issues.All(issue => issue.Severity == DataQualitySeverity.Warning);
        bool shouldTrip = issues.Any(issue => issue.Severity == DataQualitySeverity.Critical);
        if (valid)
        {
            foreach ((ChartKey key, Cursor cursor) in commits)
            {
                _cursors.AddOrUpdate(key, cursor, (_, _) => cursor);
            }
        }

        return issues.Count == 0
            ? DataQualityResult.Valid
            : new DataQualityResult
            {
                IsValid = valid,
                ShouldTrip = shouldTrip,
                Issues = issues
            };
    }

    public void Reset(InstrumentKey? instrument = null)
    {
        if (instrument is null)
        {
            _cursors.Clear();
            return;
        }

        foreach (ChartKey key in _cursors.Keys.Where(key => key.Instrument == instrument.Value))
        {
            _cursors.TryRemove(key, out _);
        }
    }

    private void ValidateSnapshot(
        MultiTimeframeAnalysis analysis,
        BarInterval interval,
        AnalysisSnapshot snapshot,
        List<DataQualityIssue> issues)
    {
        if (!interval.IsValid)
        {
            issues.Add(new DataQualityIssue(
                "interval.invalid",
                DataQualitySeverity.Critical,
                "The analysis contains an invalid interval.",
                interval));
            return;
        }

        if (snapshot.Instrument != analysis.Instrument || snapshot.Interval != interval)
        {
            issues.Add(new DataQualityIssue(
                "snapshot.identity",
                DataQualitySeverity.Critical,
                "The snapshot instrument or interval does not match its analysis key.",
                interval));
        }

        Candle candle = snapshot.LatestCandle;
        if (candle.Instrument != analysis.Instrument || candle.Interval != interval)
        {
            issues.Add(new DataQualityIssue(
                "candle.identity",
                DataQualitySeverity.Critical,
                "The latest candle instrument or interval does not match the snapshot.",
                interval));
        }

        if (_options.RequireCompleteCandles && !candle.IsComplete)
        {
            issues.Add(new DataQualityIssue(
                "candle.incomplete",
                DataQualitySeverity.Critical,
                "An incomplete candle reached the trading pipeline.",
                interval));
        }

        if (candle.CloseTime is not DateTimeOffset closeTime || closeTime <= candle.OpenTime)
        {
            issues.Add(new DataQualityIssue(
                "candle.time",
                DataQualitySeverity.Critical,
                "The latest candle has an invalid open/close time range.",
                interval));
        }
        else
        {
            if (snapshot.AvailableAt < closeTime)
            {
                issues.Add(new DataQualityIssue(
                    "snapshot.lookahead",
                    DataQualitySeverity.Critical,
                    "The snapshot is available before its latest candle closes.",
                    interval));
            }

            DateTimeOffset maximumAge = interval.AddTo(closeTime);
            for (int index = 1; index < _options.MaximumStalenessIntervals; index++)
            {
                maximumAge = interval.AddTo(maximumAge);
            }

            if (analysis.Timestamp > maximumAge)
            {
                issues.Add(new DataQualityIssue(
                    "snapshot.stale",
                    DataQualitySeverity.Error,
                    $"The {interval} snapshot is stale at analysis time {analysis.Timestamp:O}.",
                    interval));
            }
        }

        if (_options.RejectFutureSnapshots &&
            snapshot.AvailableAt > analysis.Timestamp + _options.MaximumFutureClockSkew)
        {
            issues.Add(new DataQualityIssue(
                "snapshot.future",
                DataQualitySeverity.Critical,
                "The snapshot is timestamped in the future relative to the market context.",
                interval));
        }

        Ohlc prices = candle.Prices;
        if (prices.High < prices.Low ||
            prices.High < prices.Open ||
            prices.High < prices.Close ||
            prices.Low > prices.Open ||
            prices.Low > prices.Close ||
            prices.Open <= 0m ||
            prices.High <= 0m ||
            prices.Low <= 0m ||
            prices.Close <= 0m)
        {
            issues.Add(new DataQualityIssue(
                "candle.ohlc",
                DataQualitySeverity.Critical,
                "The latest candle contains an impossible OHLC range.",
                interval));
        }

        if (candle.Volume is { Value: < 0m })
        {
            issues.Add(new DataQualityIssue(
                "candle.volume",
                DataQualitySeverity.Error,
                "The latest candle contains negative volume.",
                interval));
        }

        if (_options.RequireIndicatorsReady &&
            (snapshot.Indicators.Atr is null ||
             snapshot.Indicators.Rsi is null ||
             snapshot.Indicators.BollingerMiddle is null))
        {
            issues.Add(new DataQualityIssue(
                "indicators.warming",
                DataQualitySeverity.Error,
                $"Indicators for {interval} are not ready.",
                interval));
        }
    }

    private void ValidateSequence(
        BarInterval interval,
        AnalysisSnapshot snapshot,
        Cursor previous,
        List<DataQualityIssue> issues)
    {
        if (!previous.HasValue)
        {
            return;
        }

        if (snapshot.Version < previous.Version || snapshot.AvailableAt < previous.AvailableAt)
        {
            issues.Add(new DataQualityIssue(
                "snapshot.regression",
                DataQualitySeverity.Critical,
                "Snapshot version or time moved backwards.",
                interval));
            return;
        }

        if (snapshot.Version == previous.Version)
        {
            if (snapshot.AvailableAt != previous.AvailableAt ||
                snapshot.LatestCandle.OpenTime != previous.OpenTime)
            {
                issues.Add(new DataQualityIssue(
                    "snapshot.version_collision",
                    DataQualitySeverity.Critical,
                    "A snapshot changed without incrementing its version.",
                    interval));
            }

            return;
        }

        long versionDelta = snapshot.Version - previous.Version;
        DateTimeOffset expectedOpen = previous.OpenTime;
        for (long step = 0; step < versionDelta; step++)
        {
            expectedOpen = interval.AddTo(expectedOpen);
        }

        if (_options.RejectGaps && snapshot.LatestCandle.OpenTime > expectedOpen)
        {
            issues.Add(new DataQualityIssue(
                "candle.gap",
                DataQualitySeverity.Error,
                $"A candle gap was detected. Expected {expectedOpen:O}, received " +
                $"{snapshot.LatestCandle.OpenTime:O}.",
                interval));
        }
        else if (snapshot.LatestCandle.OpenTime < expectedOpen)
        {
            issues.Add(new DataQualityIssue(
                "candle.overlap",
                DataQualitySeverity.Critical,
                "The latest candle overlaps a previously accepted candle.",
                interval));
        }
    }

    private sealed record Cursor(
        long Version,
        DateTimeOffset AvailableAt,
        DateTimeOffset OpenTime)
    {
        public static Cursor Empty { get; } = new(0, default, default);
        public bool HasValue => Version > 0;
    }
}
