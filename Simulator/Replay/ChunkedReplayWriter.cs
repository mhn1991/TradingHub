using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Brokers.Models;
using ChartAnnotator.Models;
using Simulator.Models;
using RiskManager;
using RiskManager.Safety;
using TradeManager;

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
    private readonly Dictionary<string, ExecutionDetailState> _executionDetailStates = new(StringComparer.Ordinal);
    private readonly List<MarketReplayRow> _marketBuffer = [];
    private readonly Queue<MarketReplayRow> _executionTail = new();
    private readonly List<ReplayChunkDescriptor> _marketChunks = [];
    private int _marketChunkIndex;
    private readonly int _executionDetailPreEntryFrames;
    private readonly int _executionDetailPostExitFrames;
    private bool _completed;
    private bool _disposed;

    public ChunkedReplayWriter(
        string rootDirectory,
        int chunkSize,
        int executionDetailPreEntryFrames = 120,
        int executionDetailPostExitFrames = 120)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new ArgumentException("Output directory is required.", nameof(rootDirectory));
        if (chunkSize < 1)
            throw new ArgumentOutOfRangeException(nameof(chunkSize));
        if (executionDetailPreEntryFrames < 0 || executionDetailPostExitFrames < 0)
            throw new ArgumentOutOfRangeException(nameof(executionDetailPreEntryFrames));
        _root = Path.GetFullPath(rootDirectory);
        _chunkSize = chunkSize;
        _executionDetailPreEntryFrames = executionDetailPreEntryFrames;
        _executionDetailPostExitFrames = executionDetailPostExitFrames;
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "market"));
        Directory.CreateDirectory(Path.Combine(_root, "execution-detail"));
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

        MarketReplayRow executionRow = new(
            frame.Sequence,
            frame.AvailableAt,
            frame.ExecutionCandle.Mid.OpenTime,
            frame.ExecutionCandle.Mid.Prices.Open,
            frame.ExecutionCandle.Mid.Prices.High,
            frame.ExecutionCandle.Mid.Prices.Low,
            frame.ExecutionCandle.Mid.Prices.Close,
            frame.ExecutionCandle.Mid.Volume?.Value ?? 0m,
            frame.ClosedIntervals.Select(FormatInterval).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            frame.IsWarmup,
            null,
            frame.ExecutionCandle.Mid.Instrument.Value);
        _executionTail.Enqueue(executionRow);
        while (_executionTail.Count > _executionDetailPreEntryFrames + 1)
            _executionTail.Dequeue();
        await CaptureExecutionDetailAsync(executionRow, results, cancellationToken)
            .ConfigureAwait(false);

        // Normal chart replay is analysis-base (1m by default), not one row per
        // 1s/5s execution frame. Full-resolution context is retained around trades.
        if (frame.AnalysisBaseCandle is Candle analysisCandle)
        {
            frame.Snapshots.TryGetValue(analysisCandle.Interval, out AnalysisSnapshot? analysis);
            _marketBuffer.Add(new MarketReplayRow(
                frame.Sequence,
                frame.AvailableAt,
                analysisCandle.OpenTime,
                analysisCandle.Prices.Open,
                analysisCandle.Prices.High,
                analysisCandle.Prices.Low,
                analysisCandle.Prices.Close,
                analysisCandle.Volume?.Value ?? 0m,
                frame.ClosedIntervals.Select(FormatInterval).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                frame.IsWarmup,
                analysis,
                frame.ExecutionCandle.Mid.Instrument.Value));
        }

        if (_marketBuffer.Count >= _chunkSize)
            await FlushMarketChunkAsync(cancellationToken).ConfigureAwait(false);

        foreach (StrategyFrameResult result in results)
        {
            StrategyChunkState state = GetOrCreateStrategy(result.StrategyId, result.StrategyName);
            string[] lifecycleEvents = result.Events.Select(item => item.Type.ToString()).ToArray();
            if (frame.AnalysisBaseCandle is not null || result.Events.Count > 0)
            {
                state.Events.Add(new StrategyEventRow(
                    frame.Sequence,
                    frame.AvailableAt,
                    result.Balance,
                    result.Equity,
                    result.UnrealizedProfitLoss,
                    result.OpenPositions,
                    result.CompletedTrades,
                    result.ActiveSetups,
                    result.NewlyCompletedTrade,
                    lifecycleEvents,
                    result.Events));
            }

            if (result.NewlyCompletedTrade is SimulatedTradeRecord trade)
            {
                state.Trades.Add(trade);
                await AppendLiveTradeAsync(state, trade, cancellationToken).ConfigureAwait(false);
            }

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
        foreach (ExecutionDetailState detail in _executionDetailStates.Values)
            await FlushExecutionDetailAsync(detail, complete: true, cancellationToken).ConfigureAwait(false);
        foreach (StrategyChunkState state in _strategyStates.Values)
        {
            await FlushStrategyChunkAsync(state, cancellationToken).ConfigureAwait(false);
            await WriteStrategySidecarsAsync(state, strategies, cancellationToken).ConfigureAwait(false);
        }

        _completed = true;
        await WriteMarketIndexAsync(complete: true, cancellationToken).ConfigureAwait(false);
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
                foreach (ExecutionDetailState detail in _executionDetailStates.Values)
                    await FlushExecutionDetailAsync(detail, complete: false, CancellationToken.None)
                        .ConfigureAwait(false);
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
        MarketReplayRow[] rows = _marketBuffer.ToArray();
        await WriteGzipJsonAsync(path, rows, cancellationToken).ConfigureAwait(false);
        await using (FileStream file = File.OpenRead(path))
        {
            byte[] hash = await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false);
            _marketChunks.Add(new ReplayChunkDescriptor(
                Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(fileName)),
                fileName,
                rows[0].Sequence,
                rows[^1].Sequence,
                rows[0].AvailableAt,
                rows[^1].AvailableAt,
                rows.Length,
                file.Length,
                Convert.ToHexString(hash).ToLowerInvariant()));
        }
        _marketBuffer.Clear();
        await WriteMarketIndexAsync(complete: false, cancellationToken).ConfigureAwait(false);
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

    private async Task CaptureExecutionDetailAsync(
        MarketReplayRow row,
        IReadOnlyList<StrategyFrameResult> results,
        CancellationToken cancellationToken)
    {
        var activeKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (StrategyFrameResult result in results)
        {
            string? setupId = result.ExecutionDetailSetupId ?? result.NewlyCompletedTrade?.SetupId;
            if (string.IsNullOrWhiteSpace(setupId))
                continue;
            string key = $"{result.StrategyId}|{setupId}";
            ExecutionDetailState state = GetOrCreateExecutionDetail(result.StrategyId, setupId);
            activeKeys.Add(key);
            if (!state.Initialized)
            {
                state.Initialized = true;
                foreach (MarketReplayRow tailRow in _executionTail)
                    state.Add(tailRow);
            }
            state.Add(row);
            if (result.NewlyCompletedTrade is not null)
                state.PostFramesRemaining = _executionDetailPostExitFrames;
            if (state.Buffer.Count >= _chunkSize)
                await FlushExecutionDetailAsync(state, complete: false, cancellationToken)
                    .ConfigureAwait(false);
        }

        foreach ((string key, ExecutionDetailState state) in _executionDetailStates)
        {
            if (activeKeys.Contains(key) || state.PostFramesRemaining <= 0)
                continue;
            state.Add(row);
            state.PostFramesRemaining--;
            if (state.Buffer.Count >= _chunkSize)
                await FlushExecutionDetailAsync(state, complete: false, cancellationToken)
                    .ConfigureAwait(false);
        }
    }

    private ExecutionDetailState GetOrCreateExecutionDetail(string strategyId, string setupId)
    {
        string key = $"{strategyId}|{setupId}";
        if (_executionDetailStates.TryGetValue(key, out ExecutionDetailState? existing))
            return existing;
        string directory = Path.Combine(
            _root,
            "execution-detail",
            Sanitize(strategyId),
            Sanitize(setupId));
        Directory.CreateDirectory(directory);
        var created = new ExecutionDetailState(strategyId, setupId, directory);
        _executionDetailStates.Add(key, created);
        return created;
    }

    private async Task FlushExecutionDetailAsync(
        ExecutionDetailState state,
        bool complete,
        CancellationToken cancellationToken)
    {
        if (state.Buffer.Count > 0)
        {
            state.ChunkIndex++;
            string fileName = $"chunk-{state.ChunkIndex:D6}.json.gz";
            string path = Path.Combine(state.Directory, fileName);
            MarketReplayRow[] rows = state.Buffer.ToArray();
            await WriteGzipJsonAsync(path, rows, cancellationToken).ConfigureAwait(false);
            await using FileStream file = File.OpenRead(path);
            byte[] hash = await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false);
            state.Chunks.Add(new ReplayChunkDescriptor(
                Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(fileName)),
                fileName,
                rows[0].Sequence,
                rows[^1].Sequence,
                rows[0].AvailableAt,
                rows[^1].AvailableAt,
                rows.Length,
                file.Length,
                Convert.ToHexString(hash).ToLowerInvariant()));
            state.Buffer.Clear();
        }

        string indexPath = Path.Combine(state.Directory, "index.json");
        string temporary = indexPath + ".tmp";
        await using (FileStream index = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(
                    index,
                    new ExecutionDetailIndex(
                        1,
                        complete,
                        state.StrategyId,
                        state.SetupId,
                        state.Chunks.ToArray()),
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        File.Move(temporary, indexPath, overwrite: true);
    }

    private static async Task AppendLiveTradeAsync(
        StrategyChunkState state,
        SimulatedTradeRecord trade,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(state.Directory, "trades.ndjson");
        await using var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        string json = JsonSerializer.Serialize(trade, JsonOptions);
        byte[] payload = Encoding.UTF8.GetBytes(json + "\n");
        long offset = stream.Position;
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        state.TradeIndex.Add(new TradeIndexEntry(
            state.TradeIndex.Count,
            offset,
            payload.Length,
            trade.SetupId,
            trade.ClosedAt));

        string indexPath = Path.Combine(state.Directory, "trades.index.json");
        string temporary = indexPath + ".tmp";
        await using (FileStream index = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(
                    index,
                    new TradeIndex(1, state.Id, state.TradeIndex.ToArray()),
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        File.Move(temporary, indexPath, overwrite: true);
    }

    private async Task WriteMarketIndexAsync(bool complete, CancellationToken cancellationToken)
    {
        string path = Path.Combine(_root, "market", "index.json");
        string temporary = path + ".tmp";
        await using (FileStream stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(
                    stream,
                    new ReplayChunkIndex(1, complete, _marketChunks.ToArray()),
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        File.Move(temporary, path, overwrite: true);
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
        public List<TradeIndexEntry> TradeIndex { get; } = [];
    }

    private sealed class ExecutionDetailState(
        string strategyId,
        string setupId,
        string directory)
    {
        public string StrategyId { get; } = strategyId;
        public string SetupId { get; } = setupId;
        public string Directory { get; } = directory;
        public bool Initialized { get; set; }
        public int ChunkIndex { get; set; }
        public int PostFramesRemaining { get; set; }
        public long LastSequence { get; private set; }
        public List<MarketReplayRow> Buffer { get; } = [];
        public List<ReplayChunkDescriptor> Chunks { get; } = [];

        public void Add(MarketReplayRow row)
        {
            if (row.Sequence <= LastSequence)
                return;
            Buffer.Add(row);
            LastSequence = row.Sequence;
        }
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
        bool IsWarmup,
        AnalysisSnapshot? Analysis,
        // Additive (§7 multi-instrument clock): null on rows written before this field
        // existed, or absent from JSON entirely - readers must fall back to the
        // manifest's single/primary Instrument for those.
        string? Instrument = null);

    private sealed record StrategyEventRow(
        long Sequence,
        DateTimeOffset AvailableAt,
        decimal Balance,
        decimal Equity,
        decimal UnrealizedProfitLoss,
        int OpenPositions,
        int CompletedTrades,
        int ActiveSetups,
        SimulatedTradeRecord? CompletedTrade,
        IReadOnlyList<string> EventTypes,
        IReadOnlyList<StrategyReplayEvent> Events);

    private sealed record ReplayChunkIndex(
        int SchemaVersion,
        bool Complete,
        IReadOnlyList<ReplayChunkDescriptor> Chunks);

    private sealed record ReplayChunkDescriptor(
        string ChunkId,
        string File,
        long FirstSequence,
        long LastSequence,
        DateTimeOffset FirstTime,
        DateTimeOffset LastTime,
        int RowCount,
        long SizeBytes,
        string Sha256);

    private sealed record ExecutionDetailIndex(
        int SchemaVersion,
        bool Complete,
        string StrategyId,
        string SetupId,
        IReadOnlyList<ReplayChunkDescriptor> Chunks);

    private sealed record TradeIndex(
        int SchemaVersion,
        string StrategyId,
        IReadOnlyList<TradeIndexEntry> Items);

    private sealed record TradeIndexEntry(
        int Ordinal,
        long Offset,
        int Length,
        string SetupId,
        DateTimeOffset? ClosedAt);
}

public sealed record SimulationManifest
{
    public required Guid SimulationId { get; init; }
    public required int SchemaVersion { get; init; }
    /// <summary>Primary/default instrument - Instruments[0] for a §7 multi-instrument run.</summary>
    public required string Instrument { get; init; }
    /// <summary>
    /// Every distinct instrument traded in this run. Null/single-element for a plain
    /// single-instrument run (kept optional so older manifests without this field still
    /// deserialize; readers should fall back to [Instrument] when absent).
    /// </summary>
    public IReadOnlyList<string>? Instruments { get; init; }
    /// <summary>Strategy id -> the instrument it trades. Same fallback rule as Instruments.</summary>
    public IReadOnlyDictionary<string, string>? StrategyInstruments { get; init; }
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }
    public required DateTimeOffset? WarmupFrom { get; init; }
    public required string BaseInterval { get; init; }
    public required IReadOnlyList<string> AnalysisIntervals { get; init; }
    public required IReadOnlyList<string> Strategies { get; init; }
    public required string InputStreamId { get; init; }
    public string? SimulationConfigurationId { get; init; }
    public string? InputHash { get; init; }
    public string? SourceKind { get; init; }
    public string? PrecisionMode { get; init; }
    public string? AnalysisBaseInterval { get; init; }
    public string? TrendInterval { get; init; }
    public IReadOnlyList<string>? SecondaryTrendIntervals { get; init; }
    public IReadOnlyList<string>? SetupIntervals { get; init; }
    public string? ConfirmationInterval { get; init; }
    public IReadOnlyList<string>? AdditionalConfirmationIntervals { get; init; }
    public string? EntryInterval { get; init; }
    public int? MinimumSecondaryTrendAlignments { get; init; }
    public int? MinimumSetupAlignments { get; init; }
    public int? MinimumConfirmationAlignments { get; init; }
    public bool? StrongOppositionVeto { get; init; }
    public PositionSizingOptions? PositionSizing { get; init; }
    public PositionManagementOptions? LegacyPositionManagement { get; init; }
    public PositionManagementOptions? ImprovedPositionManagement { get; init; }
    public TradingSafetyOptions? SafetyOptions { get; init; }
    public decimal? SpreadBasisPoints { get; init; }
    public decimal? SlippageBasisPoints { get; init; }
    public decimal? CommissionRate { get; init; }
    public string? AmbiguityPolicy { get; init; }
    public string? FillModel { get; init; }
    public string? AccountMode { get; init; }
    public BacktestRuntimeOptions? RuntimeOptions { get; init; }
    public PortfolioPerformanceSnapshot? PortfolioPerformance { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public string? Status { get; init; }
}
