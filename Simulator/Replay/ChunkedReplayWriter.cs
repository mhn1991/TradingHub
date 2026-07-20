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
    private const int MaximumMarketReplayRowsPerChunk = 250;
    private const int ReplaySwingLimit = 100;
    private const int ReplaySupplyDemandZoneLimit = 100;
    private const int ReplaySupplyDemandActiveZoneLimit = 50;
    private const int ReplaySupplyDemandEventLimit = 50;
    private const int ReplayLiquidityPoolLimit = 100;
    private const int ReplayLiquidityActivePoolLimit = 64;
    private const int ReplayLiquidityEventLimit = 64;
    private const int ReplayLiquiditySweepLimit = 64;
    private const int ReplayPriceActionDiagnosticLimit = 64;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };
    // Encoding.UTF8's preamble is the 3-byte BOM - StreamWriter emits it by default, which
    // breaks strict ndjson parsers (e.g. Python's json.loads chokes on the first line).
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _root;
    private readonly int _chunkSize;
    private readonly int _marketChunkSize;
    private readonly Dictionary<string, StrategyChunkState> _strategyStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ExecutionDetailState> _executionDetailStates = new(StringComparer.Ordinal);
    private readonly List<MarketReplayRow> _marketBuffer = [];
    private readonly Queue<MarketReplayRow> _executionTail = new();
    private readonly List<ReplayChunkDescriptor> _marketChunks = [];
    private int _marketChunkIndex;
    private readonly int _executionDetailPreEntryFrames;
    private readonly int _executionDetailPostExitFrames;
    private readonly bool _captureMarketReplay;
    private bool _completed;
    private bool _disposed;

    public ChunkedReplayWriter(
        string rootDirectory,
        int chunkSize,
        int executionDetailPreEntryFrames = 120,
        int executionDetailPostExitFrames = 120,
        bool captureMarketReplay = true)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new ArgumentException("Output directory is required.", nameof(rootDirectory));
        if (chunkSize < 1)
            throw new ArgumentOutOfRangeException(nameof(chunkSize));
        if (executionDetailPreEntryFrames < 0 || executionDetailPostExitFrames < 0)
            throw new ArgumentOutOfRangeException(nameof(executionDetailPreEntryFrames));
        _root = Path.GetFullPath(rootDirectory);
        _chunkSize = chunkSize;
        // Profile runtimes created before replay snapshots became analysis-rich can still
        // contain the historical 5,000-row value. Bound only the output partition size so
        // those profiles do not retain thousands of large snapshots in memory at once.
        _marketChunkSize = Math.Min(chunkSize, MaximumMarketReplayRowsPerChunk);
        _executionDetailPreEntryFrames = executionDetailPreEntryFrames;
        _executionDetailPostExitFrames = executionDetailPostExitFrames;
        _captureMarketReplay = captureMarketReplay;
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
        if (_captureMarketReplay && frame.AnalysisBaseCandle is Candle analysisCandle)
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
                CompactAnalysisForReplay(analysis),
                frame.ExecutionCandle.Mid.Instrument.Value));
        }

        if (_marketBuffer.Count >= _marketChunkSize)
            await FlushMarketChunkAsync(cancellationToken).ConfigureAwait(false);

        foreach (StrategyFrameResult result in results)
        {
            StrategyChunkState state = GetOrCreateStrategy(result.StrategyId, result.StrategyName);
            string[] lifecycleEvents = result.Events.Select(item => item.Type.ToString()).ToArray();
            if (result.Events.Count > 0)
            {
                EnsureEventsStreamOpen(state);
                foreach (StrategyReplayEvent evt in result.Events)
                {
                    state.Funnel.Record(evt);
                    state.EventsStreamWriter!.WriteLine(JsonSerializer.Serialize(evt, JsonOptions));
                }
            }

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

    internal static AnalysisSnapshot? CompactAnalysisForReplay(AnalysisSnapshot? analysis)
    {
        if (analysis is null)
            return null;

        // Replay is a bounded chart projection. The analysis engine keeps its complete
        // immutable snapshot; only the serialized copy is trimmed to recent chart context.
        return analysis with
        {
            Swings = TakeTail(analysis.Swings, ReplaySwingLimit),
            SupplyDemand = analysis.SupplyDemand with
            {
                Zones = TakeTail(analysis.SupplyDemand.Zones, ReplaySupplyDemandZoneLimit),
                ActiveZones = TakeTail(
                    analysis.SupplyDemand.ActiveZones,
                    ReplaySupplyDemandActiveZoneLimit),
                RecentEvents = TakeTail(
                    analysis.SupplyDemand.RecentEvents,
                    ReplaySupplyDemandEventLimit)
            },
            Liquidity = analysis.Liquidity with
            {
                Pools = TakeTail(analysis.Liquidity.Pools, ReplayLiquidityPoolLimit),
                ActivePools = TakeTail(
                    analysis.Liquidity.ActivePools,
                    ReplayLiquidityActivePoolLimit),
                RecentEvents = TakeTail(
                    analysis.Liquidity.RecentEvents,
                    ReplayLiquidityEventLimit),
                RecentSweeps = TakeTail(
                    analysis.Liquidity.RecentSweeps,
                    ReplayLiquiditySweepLimit)
            },
            PriceAction = analysis.PriceAction with
            {
                Diagnostics = TakeTail(
                    analysis.PriceAction.Diagnostics,
                    ReplayPriceActionDiagnosticLimit)
            }
        };
    }

    private static IReadOnlyList<T> TakeTail<T>(IReadOnlyList<T> values, int limit) =>
        values.Count <= limit
            ? values
            : values.Skip(values.Count - limit).ToArray();

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
                {
                    await FlushStrategyChunkAsync(state, CancellationToken.None).ConfigureAwait(false);
                    await CloseEventsStreamAsync(state).ConfigureAwait(false);
                }
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
        await CloseEventsStreamAsync(state).ConfigureAwait(false);

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

        // Decision funnel + per-signal disposition, aggregated incrementally from the same
        // event stream as it was committed - avoids re-scanning the (potentially many) chunked
        // events-NNNNNN.json.gz files after the fact just to answer "why didn't this convert to
        // a trade", which previously required bespoke ad hoc parsing for every investigation.
        SignalFunnelSummary funnel = state.Funnel.BuildSummary(state.Id, state.Name);
        await WriteJsonAsync(
            Path.Combine(state.Directory, "funnel-summary.json"),
            funnel,
            cancellationToken).ConfigureAwait(false);

        string signalsPath = Path.Combine(state.Directory, "signals.ndjson");
        string signalsTemporary = signalsPath + ".tmp";
        await using (FileStream signalsFile = File.Create(signalsTemporary))
        await using (var writer = new StreamWriter(signalsFile, Utf8NoBom))
        {
            foreach (SignalLifecycleRow row in state.Funnel.BuildLifecycleRows())
                await writer.WriteLineAsync(JsonSerializer.Serialize(row, JsonOptions)).ConfigureAwait(false);
        }
        File.Move(signalsTemporary, signalsPath, overwrite: true);
    }

    private static void EnsureEventsStreamOpen(StrategyChunkState state)
    {
        if (state.EventsStreamWriter is not null)
            return;
        // Flat, single-file ndjson (one JSON object per line) rather than the chunked
        // JSON-array-per-file format used for events-NNNNNN.json.gz: each chunk file must be
        // parsed as a whole separate document, which makes ad hoc cross-run querying (DuckDB,
        // grep, streaming line-by-line) awkward. This file is additive - the chunked files still
        // exist for the dashboard's incremental replay/scrubbing UI.
        string path = Path.Combine(state.Directory, "events.ndjson.gz");
        FileStream file = File.Create(path);
        var gzip = new GZipStream(file, CompressionLevel.Fastest);
        state.EventsStreamGzip = gzip;
        state.EventsStreamWriter = new StreamWriter(gzip, Utf8NoBom) { AutoFlush = false };
    }

    private static async Task CloseEventsStreamAsync(StrategyChunkState state)
    {
        if (state.EventsStreamWriter is null)
            return;
        await state.EventsStreamWriter.FlushAsync().ConfigureAwait(false);
        await state.EventsStreamWriter.DisposeAsync().ConfigureAwait(false);
        state.EventsStreamWriter = null;
        state.EventsStreamGzip = null;
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        string temporary = path + ".tmp";
        await using (FileStream stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(temporary, path, overwrite: true);
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
        public SignalFunnelAggregator Funnel { get; } = new();
        public GZipStream? EventsStreamGzip { get; set; }
        public StreamWriter? EventsStreamWriter { get; set; }
    }

    /// <summary>
    /// Accumulates two views over the same per-frame event stream as it is committed, so
    /// answering "why didn't this signal convert to a trade" never requires re-scanning the raw
    /// chunked event files after a run completes: an event-type x reason-code funnel (mirrors the
    /// ad hoc Counter/groupby scripts previously written by hand for every investigation), and a
    /// per-SetupId lifecycle/disposition row (mirrors manually diffing SignalCreated against
    /// OrderSubmitted/OrderFilled by SetupId). Memory cost is bounded by distinct SetupId count,
    /// not total event count - dormant/non-candidate evaluations never carry a SetupId.
    /// </summary>
    private sealed class SignalFunnelAggregator
    {
        private readonly Dictionary<string, int> _eventTypeCounts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, int>> _reasonCodesByType = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SignalLifecycleTracker> _signals = new(StringComparer.Ordinal);
        private readonly List<string> _signalOrder = [];

        public void Record(StrategyReplayEvent evt)
        {
            string typeKey = evt.Type.ToString();
            _eventTypeCounts[typeKey] = _eventTypeCounts.GetValueOrDefault(typeKey) + 1;

            string? reasonCode = evt.ReasonCode;
            if (!string.IsNullOrWhiteSpace(reasonCode))
            {
                if (!_reasonCodesByType.TryGetValue(typeKey, out Dictionary<string, int>? byReason))
                {
                    byReason = new Dictionary<string, int>(StringComparer.Ordinal);
                    _reasonCodesByType[typeKey] = byReason;
                }
                byReason[reasonCode] = byReason.GetValueOrDefault(reasonCode) + 1;
            }

            if (string.IsNullOrWhiteSpace(evt.SetupId))
                return;

            if (!_signals.TryGetValue(evt.SetupId, out SignalLifecycleTracker? tracker))
            {
                tracker = new SignalLifecycleTracker(evt.SetupId, evt.StrategyId, evt.EventTime);
                _signals[evt.SetupId] = tracker;
                _signalOrder.Add(evt.SetupId);
            }
            tracker.Apply(evt);
        }

        public SignalFunnelSummary BuildSummary(string strategyId, string strategyName)
        {
            int distinctSignals = _signals.Count;
            int executed = 0, expiredOrInvalidated = 0, rejected = 0;
            var rejectionReasons = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (SignalLifecycleTracker tracker in _signals.Values)
            {
                if (tracker.Executed)
                {
                    executed++;
                }
                else if (tracker.Expired || tracker.Invalidated)
                {
                    expiredOrInvalidated++;
                }
                else if (tracker.RejectionCount > 0)
                {
                    rejected++;
                    if (tracker.LastReasonCode is string reason)
                        rejectionReasons[reason] = rejectionReasons.GetValueOrDefault(reason) + 1;
                }
            }
            int unresolved = distinctSignals - executed - expiredOrInvalidated - rejected;
            decimal conversionRate = distinctSignals == 0
                ? 0m
                : Math.Round(100m * executed / distinctSignals, 2);

            return new SignalFunnelSummary(
                1,
                strategyId,
                strategyName,
                DateTimeOffset.UtcNow,
                _eventTypeCounts,
                _reasonCodesByType.ToDictionary(
                    kv => kv.Key,
                    kv => (IReadOnlyDictionary<string, int>)kv.Value),
                new SignalConversionSummary(
                    distinctSignals, executed, rejected, expiredOrInvalidated, unresolved,
                    conversionRate, rejectionReasons));
        }

        public IEnumerable<SignalLifecycleRow> BuildLifecycleRows() =>
            _signalOrder.Select(setupId => _signals[setupId].ToRow());
    }

    private sealed class SignalLifecycleTracker
    {
        private readonly string _setupId;
        private readonly string _strategyId;
        private readonly DateTimeOffset _firstSeenAt;

        public SignalLifecycleTracker(string setupId, string strategyId, DateTimeOffset firstSeenAt)
        {
            _setupId = setupId;
            _strategyId = strategyId;
            _firstSeenAt = firstSeenAt;
            _lastEventAt = firstSeenAt;
        }

        public bool Executed { get; private set; }
        public bool Completed { get; private set; }
        public bool Expired { get; private set; }
        public bool Invalidated { get; private set; }
        public int RejectionCount { get; private set; }
        public string? LastReasonCode { get; private set; }

        public void Apply(StrategyReplayEvent evt)
        {
            _lastEventAt = evt.EventTime;
            _lastEventType = evt.Type.ToString();
            if (evt.PositionId is not null)
                _positionId = evt.PositionId;

            switch (evt.Type)
            {
                case StrategyReplayEventType.SignalCreated:
                    _signalCreatedCount++;
                    break;
                case StrategyReplayEventType.OrderFilled:
                case StrategyReplayEventType.PositionOpened:
                    Executed = true;
                    break;
                case StrategyReplayEventType.TradeCompleted:
                    Completed = true;
                    _realizedR = evt.RealizedR;
                    _exitReason = evt.ReasonCode ?? evt.Reason;
                    break;
                case StrategyReplayEventType.RiskRejected:
                case StrategyReplayEventType.OrderRejected:
                case StrategyReplayEventType.TradingConditionRejected:
                    RejectionCount++;
                    LastReasonCode = evt.ReasonCode ?? evt.Reason;
                    break;
                case StrategyReplayEventType.SetupExpired:
                    Expired = true;
                    break;
                case StrategyReplayEventType.SetupInvalidated:
                    Invalidated = true;
                    break;
            }
        }

        private DateTimeOffset _lastEventAt;
        private string? _lastEventType;
        private string? _positionId;
        private int _signalCreatedCount;
        private decimal? _realizedR;
        private string? _exitReason;

        public SignalLifecycleRow ToRow()
        {
            string disposition = Executed
                ? (Completed ? "Completed" : "OpenAtEndOfRun")
                : Expired ? "Expired"
                : Invalidated ? "Invalidated"
                : RejectionCount > 0 ? "Rejected"
                : "Unresolved";

            return new SignalLifecycleRow(
                _setupId,
                _strategyId,
                _firstSeenAt,
                _lastEventAt,
                _signalCreatedCount,
                RejectionCount,
                disposition,
                LastReasonCode,
                _lastEventType,
                Completed,
                _realizedR,
                _exitReason,
                _positionId);
        }
    }

    private sealed record SignalFunnelSummary(
        int SchemaVersion,
        string StrategyId,
        string StrategyName,
        DateTimeOffset GeneratedAt,
        IReadOnlyDictionary<string, int> EventTypeCounts,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> ReasonCodesByEventType,
        SignalConversionSummary SignalConversion);

    private sealed record SignalConversionSummary(
        int DistinctSignals,
        int Executed,
        int Rejected,
        int ExpiredOrInvalidated,
        int Unresolved,
        decimal ConversionRatePercent,
        IReadOnlyDictionary<string, int> RejectionReasonCounts);

    private sealed record SignalLifecycleRow(
        string SetupId,
        string StrategyId,
        DateTimeOffset FirstSeenAt,
        DateTimeOffset LastEventAt,
        int SignalCreatedCount,
        int RejectionCount,
        string Disposition,
        string? LastReasonCode,
        string? LastEventType,
        bool Completed,
        decimal? RealizedR,
        string? ExitReason,
        string? PositionId);

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
