using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Brokers.Models;
using ChartAnnotator.Models;
using Simulator.Models;

namespace Simulator.Replay;

/// <summary>
/// Single ordered writer for market and strategy replay chunks. Strategy workers must not
/// write concurrently; the orchestrator commits after each per-frame barrier.
/// </summary>
public sealed class ChunkedReplayWriter : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _root;
    private readonly int _chunkSize;
    private readonly Dictionary<string, StrategyChunkState> _strategyStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MarketReplayRow> _marketBuffer = [];
    private int _marketChunkIndex;
    private bool _completed;
    private bool _disposed;

    public ChunkedReplayWriter(string rootDirectory, int chunkSize)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new ArgumentException("Output directory is required.", nameof(rootDirectory));
        if (chunkSize < 1)
            throw new ArgumentOutOfRangeException(nameof(chunkSize));
        _root = Path.GetFullPath(rootDirectory);
        _chunkSize = chunkSize;
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "market"));
        Directory.CreateDirectory(Path.Combine(_root, "strategies"));
    }

    public string Root => _root;

    public async Task WriteManifestAsync(SimulationManifest manifest, CancellationToken cancellationToken)
    {
        string path = Path.Combine(_root, "manifest.json");
        string temporary = path + ".tmp";
        await using (FileStream stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(temporary, path, overwrite: true);
    }

    public async Task CommitFrameAsync(
        MarketFrame frame,
        IReadOnlyList<StrategyFrameResult> results,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
            throw new InvalidOperationException("Replay writer is already completed.");

        // Compact market row — not a full annotation graph for every 1m candle.
        _marketBuffer.Add(new MarketReplayRow(
            frame.Sequence,
            frame.AvailableAt,
            frame.ExecutionCandle.Mid.OpenTime,
            frame.ExecutionCandle.Mid.Prices.Open,
            frame.ExecutionCandle.Mid.Prices.High,
            frame.ExecutionCandle.Mid.Prices.Low,
            frame.ExecutionCandle.Mid.Prices.Close,
            frame.ExecutionCandle.Mid.Volume?.Value ?? 0m,
            frame.ClosedIntervals.Select(FormatInterval).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            frame.IsWarmup));

        if (_marketBuffer.Count >= _chunkSize)
            await FlushMarketChunkAsync(cancellationToken).ConfigureAwait(false);

        foreach (StrategyFrameResult result in results)
        {
            StrategyChunkState state = GetOrCreateStrategy(result.StrategyId, result.StrategyName);
            state.Events.Add(new StrategyEventRow(
                frame.Sequence,
                frame.AvailableAt,
                result.Balance,
                result.Equity,
                result.UnrealizedProfitLoss,
                result.OpenPositions,
                result.CompletedTrades,
                result.ActiveSetups,
                result.NewlyCompletedTrade));

            if (result.NewlyCompletedTrade is SimulatedTradeRecord trade)
                state.Trades.Add(trade);

            if (state.Events.Count >= _chunkSize)
                await FlushStrategyChunkAsync(state, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task CompleteAsync(
        IReadOnlyList<StrategySimulationResult> strategies,
        MarketDataQualityReport quality,
        CancellationToken cancellationToken)
    {
        await FlushMarketChunkAsync(cancellationToken).ConfigureAwait(false);
        foreach (StrategyChunkState state in _strategyStates.Values)
        {
            await FlushStrategyChunkAsync(state, cancellationToken).ConfigureAwait(false);
            await WriteStrategySidecarsAsync(state, strategies, cancellationToken).ConfigureAwait(false);
        }

        _completed = true;
        await File.WriteAllTextAsync(
            Path.Combine(_root, "COMPLETE"),
            DateTimeOffset.UtcNow.ToString("O"),
            cancellationToken).ConfigureAwait(false);
        _ = quality;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (!_completed)
        {
            try
            {
                await FlushMarketChunkAsync(CancellationToken.None).ConfigureAwait(false);
                foreach (StrategyChunkState state in _strategyStates.Values)
                    await FlushStrategyChunkAsync(state, CancellationToken.None).ConfigureAwait(false);
                await File.WriteAllTextAsync(
                    Path.Combine(_root, "INCOMPLETE"),
                    DateTimeOffset.UtcNow.ToString("O")).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort incomplete marker.
            }
        }
    }

    private StrategyChunkState GetOrCreateStrategy(string id, string name)
    {
        if (_strategyStates.TryGetValue(id, out StrategyChunkState? existing))
            return existing;

        string directory = Path.Combine(_root, "strategies", Sanitize(id));
        Directory.CreateDirectory(directory);
        var state = new StrategyChunkState(id, name, directory);
        _strategyStates[id] = state;
        return state;
    }

    private async Task FlushMarketChunkAsync(CancellationToken cancellationToken)
    {
        if (_marketBuffer.Count == 0)
            return;

        _marketChunkIndex++;
        string fileName = $"chunk-{_marketChunkIndex:D6}.json.gz";
        string path = Path.Combine(_root, "market", fileName);
        await WriteGzipJsonAsync(path, _marketBuffer.ToArray(), cancellationToken).ConfigureAwait(false);
        _marketBuffer.Clear();
    }

    private async Task FlushStrategyChunkAsync(StrategyChunkState state, CancellationToken cancellationToken)
    {
        if (state.Events.Count == 0)
            return;

        state.ChunkIndex++;
        string fileName = $"events-{state.ChunkIndex:D6}.json.gz";
        string path = Path.Combine(state.Directory, fileName);
        await WriteGzipJsonAsync(path, state.Events.ToArray(), cancellationToken).ConfigureAwait(false);
        state.Events.Clear();
    }

    private async Task WriteStrategySidecarsAsync(
        StrategyChunkState state,
        IReadOnlyList<StrategySimulationResult> strategies,
        CancellationToken cancellationToken)
    {
        StrategySimulationResult? match = strategies.FirstOrDefault(item =>
            string.Equals(item.StrategyId, state.Id, StringComparison.OrdinalIgnoreCase));
        await WriteGzipJsonAsync(
            Path.Combine(state.Directory, "trades.json.gz"),
            state.Trades.ToArray(),
            cancellationToken).ConfigureAwait(false);

        if (match is not null)
        {
            await WriteGzipJsonAsync(
                Path.Combine(state.Directory, "performance.json.gz"),
                match.Result,
                cancellationToken).ConfigureAwait(false);
            await WriteGzipJsonAsync(
                Path.Combine(state.Directory, "metrics.json.gz"),
                match.Metrics,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteGzipJsonAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        string temporary = path + ".tmp";
        await using (FileStream file = File.Create(temporary))
        await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        {
            await JsonSerializer.SerializeAsync(gzip, value, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(temporary, path, overwrite: true);
    }

    private static string Sanitize(string value) => string.Concat(value.Select(character =>
        char.IsLetterOrDigit(character) ? character : '_'));

    private static string FormatInterval(BarInterval interval) => interval.Unit switch
    {
        BarUnit.Second => $"{interval.Value}s",
        BarUnit.Minute => $"{interval.Value}m",
        BarUnit.Hour => $"{interval.Value}h",
        BarUnit.Day => $"{interval.Value}d",
        BarUnit.Week => $"{interval.Value}w",
        BarUnit.Month => $"{interval.Value}mo",
        _ => interval.ToString()
    };

    private sealed class StrategyChunkState(string id, string name, string directory)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public string Directory { get; } = directory;
        public int ChunkIndex { get; set; }
        public List<StrategyEventRow> Events { get; } = [];
        public List<SimulatedTradeRecord> Trades { get; } = [];
    }

    private sealed record MarketReplayRow(
        long Sequence,
        DateTimeOffset AvailableAt,
        DateTimeOffset OpenTime,
        decimal Open,
        decimal High,
        decimal Low,
        decimal Close,
        decimal Volume,
        IReadOnlyList<string> ClosedIntervals,
        bool IsWarmup);

    private sealed record StrategyEventRow(
        long Sequence,
        DateTimeOffset AvailableAt,
        decimal Balance,
        decimal Equity,
        decimal UnrealizedProfitLoss,
        int OpenPositions,
        int CompletedTrades,
        int ActiveSetups,
        SimulatedTradeRecord? CompletedTrade);
}

public sealed record SimulationManifest
{
    public required Guid SimulationId { get; init; }
    public required int SchemaVersion { get; init; }
    public required string Instrument { get; init; }
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }
    public required DateTimeOffset? WarmupFrom { get; init; }
    public required string BaseInterval { get; init; }
    public required IReadOnlyList<string> AnalysisIntervals { get; init; }
    public required IReadOnlyList<string> Strategies { get; init; }
    public required string InputStreamId { get; init; }
    public string? InputHash { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public string? Status { get; init; }
}
