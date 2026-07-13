using System.IO.Compression;
using System.Text.Json;
using Brokers.Models;
using Simulator.Models;
using Simulator.Services;

namespace Dashboard.Live;

public static class SimulationApi
{
    public static void MapSimulationEndpoints(this WebApplication app)
    {
        app.MapPost("/api/simulations", async (
            CreateSimulationRequest body,
            IBacktestApplicationService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                BacktestRequest request = body.ToBacktestRequest();
                SimulationJobHandle handle = await service.StartAsync(request, cancellationToken);
                return Results.Accepted($"/api/simulations/{handle.SimulationId:N}", new
                {
                    simulationId = handle.SimulationId,
                    status = handle.Status.ToString()
                });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        app.MapGet("/api/simulations", async (
            IBacktestApplicationService service,
            int? take,
            CancellationToken cancellationToken) =>
        {
            IReadOnlyList<SimulationJobSnapshot> jobs =
                await service.ListAsync(take ?? 50, cancellationToken);
            return Results.Ok(jobs);
        });

        app.MapGet("/api/simulations/{id:guid}", async (
            Guid id,
            IBacktestApplicationService service,
            CancellationToken cancellationToken) =>
        {
            SimulationJobSnapshot? snapshot = await service.GetAsync(id, cancellationToken);
            return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
        });

        app.MapPost("/api/simulations/{id:guid}/pause", async (
            Guid id,
            IBacktestApplicationService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await service.PauseAsync(id, cancellationToken);
                return Results.Accepted();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        app.MapPost("/api/simulations/{id:guid}/resume", async (
            Guid id,
            IBacktestApplicationService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await service.ResumeAsync(id, cancellationToken);
                return Results.Accepted();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        app.MapPost("/api/simulations/{id:guid}/cancel", async (
            Guid id,
            IBacktestApplicationService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await service.CancelAsync(id, cancellationToken);
                return Results.Accepted();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        app.MapGet("/api/simulations/{id:guid}/strategies", async (
            Guid id,
            IBacktestApplicationService service,
            CancellationToken cancellationToken) =>
        {
            SimulationJobSnapshot? snapshot = await service.GetAsync(id, cancellationToken);
            return snapshot is null ? Results.NotFound() : Results.Ok(snapshot.Strategies);
        });

        app.MapGet("/api/simulations/{id:guid}/trades", async (
            Guid id,
            string? strategy,
            IBacktestApplicationService service,
            CancellationToken cancellationToken) =>
        {
            SimulationJobSnapshot? snapshot = await service.GetAsync(id, cancellationToken);
            if (snapshot?.OutputDirectory is null || !Directory.Exists(snapshot.OutputDirectory))
                return snapshot is null ? Results.NotFound() : Results.Ok(Array.Empty<object>());

            string strategiesRoot = Path.Combine(snapshot.OutputDirectory, "strategies");
            if (!Directory.Exists(strategiesRoot))
                return Results.Ok(Array.Empty<object>());

            var trades = new List<object>();
            foreach (string directory in Directory.EnumerateDirectories(strategiesRoot))
            {
                string strategyId = Path.GetFileName(directory);
                if (!string.IsNullOrWhiteSpace(strategy) &&
                    !string.Equals(strategyId, strategy, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string tradesPath = Path.Combine(directory, "trades.json.gz");
                if (!File.Exists(tradesPath))
                    continue;

                await using FileStream file = File.OpenRead(tradesPath);
                await using var gzip = new GZipStream(file, CompressionMode.Decompress);
                JsonElement? payload = await JsonSerializer.DeserializeAsync<JsonElement>(gzip, cancellationToken: cancellationToken);
                if (payload is JsonElement element)
                {
                    trades.Add(new { strategyId, trades = element });
                }
            }

            return Results.Ok(trades);
        });

        app.MapGet("/api/simulations/{id:guid}/performance", async (
            Guid id,
            IBacktestApplicationService service,
            CancellationToken cancellationToken) =>
        {
            SimulationJobSnapshot? snapshot = await service.GetAsync(id, cancellationToken);
            if (snapshot is null)
                return Results.NotFound();

            return Results.Ok(new
            {
                snapshot.Id,
                snapshot.Status,
                snapshot.Strategies,
                snapshot.WorkerMetrics,
                snapshot.DataQuality,
                snapshot.InputHash
            });
        });

        app.MapGet("/api/simulations/{id:guid}/replay", async (
            Guid id,
            DateTimeOffset? from,
            DateTimeOffset? to,
            long? startSequence,
            long? endSequence,
            int? limit,
            string? cursor,
            IBacktestApplicationService service,
            CancellationToken cancellationToken) =>
        {
            const int maxLimit = 5_000;
            int take = Math.Clamp(limit ?? 1_000, 1, maxLimit);

            SimulationJobSnapshot? snapshot = await service.GetAsync(id, cancellationToken);
            if (snapshot?.OutputDirectory is null)
                return snapshot is null ? Results.NotFound() : Results.NotFound(new { error = "No replay output yet." });

            string marketDir = Path.Combine(snapshot.OutputDirectory, "market");
            if (!Directory.Exists(marketDir))
            {
                return Results.Ok(new
                {
                    simulationId = id,
                    startSequence = startSequence ?? 0,
                    nextCursor = (string?)null,
                    hasMore = false,
                    rows = Array.Empty<object>()
                });
            }

            long minSequence = startSequence ?? 0;
            if (!string.IsNullOrWhiteSpace(cursor) &&
                cursor.StartsWith("seq:", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(cursor[4..], out long cursorSeq))
            {
                minSequence = Math.Max(minSequence, cursorSeq);
            }

            var rows = new List<JsonElement>(take);
            long lastSequence = minSequence;
            bool hasMore = false;

            foreach (string path in Directory.EnumerateFiles(marketDir, "chunk-*.json.gz").OrderBy(p => p))
            {
                await using FileStream file = File.OpenRead(path);
                await using var gzip = new GZipStream(file, CompressionMode.Decompress);
                JsonElement[]? chunk = await JsonSerializer.DeserializeAsync<JsonElement[]>(
                    gzip,
                    cancellationToken: cancellationToken);
                if (chunk is null)
                    continue;

                foreach (JsonElement row in chunk)
                {
                    long sequence = 0;
                    if (row.TryGetProperty("sequence", out JsonElement seqEl))
                        sequence = seqEl.GetInt64();
                    if (sequence < minSequence)
                        continue;
                    if (endSequence is not null && sequence > endSequence)
                    {
                        hasMore = false;
                        goto Done;
                    }

                    if (from is not null || to is not null)
                    {
                        if (row.TryGetProperty("availableAt", out JsonElement availableAt) &&
                            DateTimeOffset.TryParse(availableAt.GetString(), out DateTimeOffset stamp))
                        {
                            if (from is not null && stamp < from)
                                continue;
                            if (to is not null && stamp > to)
                                continue;
                        }
                    }

                    if (rows.Count >= take)
                    {
                        hasMore = true;
                        goto Done;
                    }

                    rows.Add(row);
                    lastSequence = sequence + 1;
                }
            }

        Done:
            return Results.Ok(new
            {
                simulationId = id,
                startSequence = minSequence,
                nextCursor = hasMore ? $"seq:{lastSequence}" : null,
                hasMore,
                count = rows.Count,
                rows
            });
        });

        app.MapGet("/api/simulations/{id:guid}/replay/chunks", async (
            Guid id,
            IBacktestApplicationService service,
            CancellationToken cancellationToken) =>
        {
            SimulationJobSnapshot? snapshot = await service.GetAsync(id, cancellationToken);
            if (snapshot?.OutputDirectory is null)
                return snapshot is null ? Results.NotFound() : Results.Ok(Array.Empty<object>());

            string marketDir = Path.Combine(snapshot.OutputDirectory, "market");
            if (!Directory.Exists(marketDir))
                return Results.Ok(Array.Empty<object>());

            var chunks = Directory.EnumerateFiles(marketDir, "chunk-*.json.gz")
                .OrderBy(path => path)
                .Select(path =>
                {
                    string name = Path.GetFileName(path);
                    string chunkId = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path));
                    return new
                    {
                        chunkId,
                        file = name,
                        sizeBytes = new FileInfo(path).Length
                    };
                })
                .ToArray();
            return Results.Ok(chunks);
        });

        app.MapGet("/api/simulations/{id:guid}/replay/chunks/{chunkId}", async (
            Guid id,
            string chunkId,
            IBacktestApplicationService service,
            CancellationToken cancellationToken) =>
        {
            if (chunkId.Contains("..", StringComparison.Ordinal) ||
                chunkId.Contains('/', StringComparison.Ordinal) ||
                chunkId.Contains('\\', StringComparison.Ordinal))
            {
                return Results.BadRequest(new { error = "Invalid chunk id." });
            }

            SimulationJobSnapshot? snapshot = await service.GetAsync(id, cancellationToken);
            if (snapshot?.OutputDirectory is null)
                return Results.NotFound();

            string marketRoot = Path.GetFullPath(Path.Combine(snapshot.OutputDirectory, "market"));
            string path = Path.GetFullPath(Path.Combine(marketRoot, $"{chunkId}.json.gz"));
            if (!path.StartsWith(marketRoot, StringComparison.Ordinal))
                return Results.BadRequest(new { error = "Invalid chunk path." });

            if (!File.Exists(path))
            {
                path = Path.GetFullPath(Path.Combine(
                    marketRoot,
                    chunkId.EndsWith(".json.gz", StringComparison.Ordinal) ? chunkId : chunkId + ".json.gz"));
                if (!path.StartsWith(marketRoot, StringComparison.Ordinal) || !File.Exists(path))
                    return Results.NotFound();
            }

            await using FileStream file = File.OpenRead(path);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            JsonElement? payload = await JsonSerializer.DeserializeAsync<JsonElement>(
                gzip,
                cancellationToken: cancellationToken);
            return Results.Ok(payload);
        });
    }
}

public sealed record CreateSimulationRequest
{
    public string Instrument { get; init; } = "FX:GBP/JPY";
    public DateTimeOffset From { get; init; }
    public DateTimeOffset To { get; init; }
    /// <summary>Legacy field; prefer ExecutionInterval / PrecisionMode.</summary>
    public string BaseInterval { get; init; } = "1m";
    public string? ExecutionInterval { get; init; }
    public string AnalysisBaseInterval { get; init; } = "1m";
    public string[] AnalysisIntervals { get; init; } = ["5m", "15m", "1h"];
    public string PrecisionMode { get; init; } = "Fast";
    public string SourceKind { get; init; } = "OandaCandles";
    public string? ImportedCandlePath { get; init; }
    public string TrendInterval { get; init; } = "1h";
    public string ConfirmationInterval { get; init; } = "15m";
    public string EntryInterval { get; init; } = "5m";
    public string[] Strategies { get; init; } = ["legacy", "improved"];
    public decimal StartingBalance { get; init; } = 100_000m;
    public decimal Quantity { get; init; } = 1_000m;
    public decimal Leverage { get; init; } = 20m;
    public decimal CommissionRate { get; init; } = 0.00002m;
    public decimal SpreadBasisPoints { get; init; } = 1m;
    public decimal SlippageBasisPoints { get; init; } = 0.5m;
    public decimal MinimumRewardRisk { get; init; } = 1.5m;
    public int WarmupDays { get; init; } = 45;
    public string StrategyExecutionMode { get; init; } = "ParallelWorkers";
    public string StrategyWorkerMode { get; init; } = "Task";
    public string AmbiguousIntrabarPolicy { get; init; } = "ConservativeStopFirst";
    public bool RefreshCache { get; init; }
    public bool NoCache { get; init; }
    public int DeterministicSeed { get; init; } = 12_345;

    public BacktestRequest ToBacktestRequest()
    {
        string solutionRoot = FindSolutionRoot() ?? Directory.GetCurrentDirectory();
        var precision = Enum.Parse<Simulator.MarketData.SimulationPrecisionMode>(PrecisionMode, ignoreCase: true);
        var source = Enum.Parse<Simulator.MarketData.HistoricalDataSourceKind>(SourceKind, ignoreCase: true);
        SimulationTimeframeOptions fromPrecision =
            SimulationTimeframeOptions.FromPrecision(precision, AnalysisIntervals.Select(BarIntervalParser.Parse).ToArray());

        BarInterval execution = !string.IsNullOrWhiteSpace(ExecutionInterval)
            ? BarIntervalParser.Parse(ExecutionInterval)
            : precision == Simulator.MarketData.SimulationPrecisionMode.Fast &&
              !string.IsNullOrWhiteSpace(BaseInterval)
                ? BarIntervalParser.Parse(BaseInterval)
                : fromPrecision.ExecutionInterval;

        BarInterval analysisBase = !string.IsNullOrWhiteSpace(AnalysisBaseInterval)
            ? BarIntervalParser.Parse(AnalysisBaseInterval)
            : fromPrecision.AnalysisBaseInterval;

        return new BacktestRequest
        {
            Instrument = new InstrumentKey(Instrument),
            From = From,
            To = To,
            Strategies = Strategies,
            StartingBalance = StartingBalance,
            Quantity = Quantity,
            Leverage = Leverage,
            CommissionRate = CommissionRate,
            SpreadBasisPoints = SpreadBasisPoints,
            SlippageBasisPoints = SlippageBasisPoints,
            MinimumRewardRisk = MinimumRewardRisk,
            OutputDirectory = Path.Combine(solutionRoot, "Dashboard", "public", "data", "simulations"),
            CacheDirectory = Path.Combine(solutionRoot, ".cache", "oanda"),
            JobsDirectory = Path.Combine(solutionRoot, ".cache", "simulation-jobs"),
            Runtime = new BacktestRuntimeOptions
            {
                ExecutionInterval = execution,
                AnalysisBaseInterval = analysisBase,
                AnalysisIntervals = AnalysisIntervals.Select(BarIntervalParser.Parse).ToArray(),
                PrecisionMode = precision,
                SourceKind = source,
                ImportedCandlePath = ImportedCandlePath,
                StrategyTimeframes = new ProgressiveStrategyTimeframes
                {
                    TrendInterval = BarIntervalParser.Parse(TrendInterval),
                    ConfirmationInterval = BarIntervalParser.Parse(ConfirmationInterval),
                    EntryInterval = BarIntervalParser.Parse(EntryInterval)
                },
                WarmupDays = WarmupDays,
                StrategyExecutionMode = Enum.Parse<StrategyExecutionMode>(StrategyExecutionMode, ignoreCase: true),
                StrategyWorkerMode = Enum.Parse<StrategyWorkerMode>(StrategyWorkerMode, ignoreCase: true),
                AmbiguousIntrabarPolicy = Enum.Parse<AmbiguousIntrabarPolicy>(AmbiguousIntrabarPolicy, ignoreCase: true),
                RefreshCache = RefreshCache,
                NoCache = NoCache,
                DeterministicSeed = DeterministicSeed,
                ProgressPublishIntervalMilliseconds = 500
            }
        };
    }

    private static string? FindSolutionRoot()
    {
        DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TradingHub.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        return null;
    }
}
