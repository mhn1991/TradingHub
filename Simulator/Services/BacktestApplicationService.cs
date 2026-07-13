using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Agent.Abstractions;
using Agent.Strategies;
using Brokers;
using Brokers.Models;
using Brokers.Oanda;
using Simulator.Abstractions;
using Simulator.Engine;
using Simulator.Jobs;
using Simulator.MarketData;
using Simulator.Models;

namespace Simulator.Services;

public sealed class BacktestApplicationServiceOptions
{
    public int MaxConcurrentJobs { get; init; } = 1;
    public int QueueCapacity { get; init; } = 8;
    public Func<BacktestRequest, IHistoricalCandleStream>? StreamFactory { get; init; }
    public Func<BacktestRequest, IReadOnlyList<(string Id, ITradingAgent Agent)>>? StrategyFactory { get; init; }
}

/// <summary>
/// Shared application service used by Dashboard API and CLI. Owns a bounded job queue
/// and cancellation; does not use fire-and-forget tasks without ownership.
/// </summary>
public sealed class BacktestApplicationService : IBacktestApplicationService, IAsyncDisposable
{
    private readonly ISimulationJobRepository _repository;
    private readonly CoalescingJobSnapshotStore _snapshotStore;
    private readonly BacktestApplicationServiceOptions _options;
    private readonly ConcurrentDictionary<Guid, RunningJob> _running = new();
    private readonly Channel<Guid> _queue;
    private readonly CancellationTokenSource _serviceLifetime = new();
    private readonly Task[] _workers;
    private bool _disposed;

    public BacktestApplicationService(
        ISimulationJobRepository repository,
        BacktestApplicationServiceOptions? options = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _snapshotStore = new CoalescingJobSnapshotStore(repository);
        _options = options ?? new BacktestApplicationServiceOptions();
        if (_options.MaxConcurrentJobs < 1)
            throw new ArgumentOutOfRangeException(nameof(options.MaxConcurrentJobs));
        if (_options.QueueCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(options.QueueCapacity));

        _queue = Channel.CreateBounded<Guid>(new BoundedChannelOptions(_options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

        if (repository is FileSimulationJobRepository fileRepo)
        {
            // Do not leave stale "Running" jobs after process restart.
            fileRepo.MarkInterruptedJobsAsync().GetAwaiter().GetResult();
        }

        _workers = Enumerable.Range(0, _options.MaxConcurrentJobs)
            .Select(_ => Task.Run(() => WorkerLoopAsync(_serviceLifetime.Token)))
            .ToArray();
    }

    public event Action<SimulationJobSnapshot>? JobUpdated;

    public async Task<SimulationJobHandle> StartAsync(
        BacktestRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        Guid id = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string outputDirectory = Path.GetFullPath(
            Path.Combine(request.OutputDirectory, id.ToString("N")));
        Directory.CreateDirectory(outputDirectory);

        string inputRequestId = BuildInputRequestId(request);
        var snapshot = new SimulationJobSnapshot
        {
            Id = id,
            Revision = 1,
            Status = SimulationJobStatus.Queued,
            CreatedAt = now,
            Instrument = request.Instrument.Value,
            RequestedFrom = request.From,
            RequestedTo = request.To,
            WarmupFrom = request.ResolveWarmupFrom(),
            ProcessedBaseCandles = 0,
            EstimatedBaseCandleCount = StreamingComparativeEngine.EstimateCandleCount(
                request.ResolveWarmupFrom(),
                request.To,
                request.Runtime.BaseInterval),
            ProgressPercent = 0m,
            CandlesPerSecond = 0m,
            Strategies = request.Strategies.Select(name => new StrategyProgressSnapshot
            {
                StrategyId = NormalizeStrategyId(name),
                StrategyName = name,
                Balance = request.StartingBalance,
                Equity = request.StartingBalance,
                UnrealizedProfitLoss = 0m,
                OpenPositions = 0,
                CompletedTrades = 0,
                ActiveSetups = 0,
                NetProfit = 0m,
                Status = "Queued"
            }).ToArray(),
            OutputDirectory = outputDirectory,
            InputRequestId = inputRequestId,
            Request = request with { AccessToken = null, AccountId = null },
            DataSourceStatus = "Queued"
        };

        var running = new RunningJob(id, request, snapshot, outputDirectory);
        if (!_running.TryAdd(id, running))
            throw new InvalidOperationException("Failed to register simulation job.");

        await PersistAsync(snapshot, terminal: false, cancellationToken).ConfigureAwait(false);
        RaiseUpdated(snapshot);

        await _queue.Writer.WriteAsync(id, cancellationToken).ConfigureAwait(false);
        return new SimulationJobHandle(id, SimulationJobStatus.Queued);
    }

    public async Task<SimulationJobSnapshot?> GetAsync(
        Guid simulationId,
        CancellationToken cancellationToken = default)
    {
        if (_running.TryGetValue(simulationId, out RunningJob? job))
            return job.Snapshot;

        return await _repository.GetAsync(simulationId, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<SimulationJobSnapshot>> ListAsync(
        int take = 50,
        CancellationToken cancellationToken = default) =>
        _repository.ListAsync(take, cancellationToken);

    public async Task PauseAsync(Guid simulationId, CancellationToken cancellationToken = default)
    {
        if (!_running.TryGetValue(simulationId, out RunningJob? job))
        {
            SimulationJobSnapshot? existing = await _repository.GetAsync(simulationId, cancellationToken)
                .ConfigureAwait(false);
            if (existing is null)
                throw new KeyNotFoundException($"Simulation '{simulationId}' was not found.");
            if (existing.IsComplete)
                throw new InvalidOperationException($"Simulation '{simulationId}' is already {existing.Status}.");
            throw new InvalidOperationException($"Simulation '{simulationId}' is not running in this process.");
        }

        if (job.Snapshot.IsComplete)
            throw new InvalidOperationException($"Simulation '{simulationId}' is already complete.");

        job.PauseGate.Pause();
        await UpdateStatusAsync(job, SimulationJobStatus.Paused, cancellationToken).ConfigureAwait(false);
    }

    public async Task ResumeAsync(Guid simulationId, CancellationToken cancellationToken = default)
    {
        if (!_running.TryGetValue(simulationId, out RunningJob? job))
        {
            SimulationJobSnapshot? existing = await _repository.GetAsync(simulationId, cancellationToken)
                .ConfigureAwait(false);
            if (existing is null)
                throw new KeyNotFoundException($"Simulation '{simulationId}' was not found.");
            if (existing.IsComplete)
                throw new InvalidOperationException($"Simulation '{simulationId}' is already {existing.Status}.");
            throw new InvalidOperationException($"Simulation '{simulationId}' is not running in this process.");
        }

        if (job.Snapshot.IsComplete)
            throw new InvalidOperationException($"Simulation '{simulationId}' is already complete.");

        job.PauseGate.Resume();
        await UpdateStatusAsync(job, SimulationJobStatus.Running, cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelAsync(Guid simulationId, CancellationToken cancellationToken = default)
    {
        if (!_running.TryGetValue(simulationId, out RunningJob? job))
        {
            SimulationJobSnapshot? existing = await _repository.GetAsync(simulationId, cancellationToken)
                .ConfigureAwait(false);
            if (existing is null)
                throw new KeyNotFoundException($"Simulation '{simulationId}' was not found.");
            if (existing.IsComplete)
                return;

            await _repository.SaveAsync(existing with
            {
                Revision = existing.Revision + 1,
                Status = SimulationJobStatus.Cancelled,
                CompletedAt = DateTimeOffset.UtcNow,
                IsComplete = true
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (job.Snapshot.IsComplete)
            return;

        await UpdateStatusAsync(job, SimulationJobStatus.Cancelling, cancellationToken).ConfigureAwait(false);
        job.PauseGate.Resume();
        job.Cancellation.Cancel();
    }

    public async Task<ComparativeSimulationResult> RunToCompletionAsync(
        BacktestRequest request,
        IProgress<BacktestProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        SimulationJobHandle handle = await StartAsync(request, cancellationToken).ConfigureAwait(false);
        if (!_running.TryGetValue(handle.SimulationId, out RunningJob? job))
            throw new InvalidOperationException("Simulation job disappeared after start.");

        if (progress is not null)
            job.ExternalProgress = progress;

        await using CancellationTokenRegistration registration =
            cancellationToken.Register(() => job.Cancellation.Cancel());

        ComparativeSimulationResult result = await job.Completion
            .Task
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _queue.Writer.TryComplete();
        _serviceLifetime.Cancel();
        try
        {
            await Task.WhenAll(_workers).ConfigureAwait(false);
        }
        catch
        {
            // Workers observe cancellation.
        }

        foreach (RunningJob job in _running.Values)
        {
            job.Cancellation.Cancel();
            job.Cancellation.Dispose();
            job.Completion.TrySetCanceled();
        }

        _serviceLifetime.Dispose();
    }

    private async Task WorkerLoopAsync(CancellationToken serviceToken)
    {
        await foreach (Guid id in _queue.Reader.ReadAllAsync(serviceToken).ConfigureAwait(false))
        {
            if (!_running.TryGetValue(id, out RunningJob? job))
                continue;

            try
            {
                ComparativeSimulationResult result = await ExecuteJobAsync(job, serviceToken)
                    .ConfigureAwait(false);
                SimulationJobSnapshot completed = NextRevision(job.Snapshot) with
                {
                    Status = SimulationJobStatus.Completed,
                    CompletedAt = DateTimeOffset.UtcNow,
                    IsComplete = true,
                    ProgressPercent = 100m,
                    InputHash = result.InputHash,
                    InputStreamId = result.InputStreamId,
                    DataQuality = result.DataQuality,
                    WorkerMetrics = result.Strategies.Select(item => item.Metrics).ToArray(),
                    Strategies = result.Strategies.Select(item => new StrategyProgressSnapshot
                    {
                        StrategyId = item.StrategyId,
                        StrategyName = item.StrategyName,
                        Balance = item.Result.FinalBalance,
                        Equity = item.Result.FinalEquity,
                        UnrealizedProfitLoss = item.Result.UnrealizedProfitLoss,
                        OpenPositions = item.Result.OpenPositions.Count,
                        CompletedTrades = item.Result.Trades.Count,
                        ActiveSetups = 0,
                        NetProfit = item.Result.NetProfit,
                        Status = item.IsComplete ? "Completed" : "Incomplete"
                    }).ToArray()
                };
                job.Snapshot = completed;
                await PersistAsync(completed, terminal: true, CancellationToken.None).ConfigureAwait(false);
                RaiseUpdated(completed);
                job.Completion.TrySetResult(result);
            }
            catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested)
            {
                SimulationJobSnapshot cancelled = NextRevision(job.Snapshot) with
                {
                    Status = SimulationJobStatus.Cancelled,
                    CompletedAt = DateTimeOffset.UtcNow,
                    IsComplete = true
                };
                job.Snapshot = cancelled;
                await PersistAsync(cancelled, terminal: true, CancellationToken.None).ConfigureAwait(false);
                RaiseUpdated(cancelled);
                job.Completion.TrySetCanceled(job.Cancellation.Token);
            }
            catch (Exception exception)
            {
                SimulationJobSnapshot failed = NextRevision(job.Snapshot) with
                {
                    Status = SimulationJobStatus.Failed,
                    CompletedAt = DateTimeOffset.UtcNow,
                    IsComplete = true,
                    Error = exception.Message
                };
                job.Snapshot = failed;
                await PersistAsync(failed, terminal: true, CancellationToken.None).ConfigureAwait(false);
                RaiseUpdated(failed);
                job.Completion.TrySetException(exception);
            }
            finally
            {
                // Release in-memory resources; file-backed snapshot remains.
                if (_running.TryRemove(id, out RunningJob? finished))
                {
                    finished.Cancellation.Dispose();
                }
            }
        }
    }

    private async Task<ComparativeSimulationResult> ExecuteJobAsync(
        RunningJob job,
        CancellationToken serviceToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            serviceToken,
            job.Cancellation.Token);
        CancellationToken cancellationToken = linked.Token;
        BacktestRequest request = job.Request;

        await UpdateStatusAsync(job, SimulationJobStatus.PreparingData, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<(string Id, ITradingAgent Agent)> strategies =
            (_options.StrategyFactory ?? CreateDefaultStrategies)(request);
        IHistoricalCandleStream stream = (_options.StreamFactory ?? CreateDefaultStream)(request);

        string inputRequestId = job.Snapshot.InputRequestId ?? BuildInputRequestId(request);
        // SimulationId identifies the run; InputRequestId is deterministic for the data request.
        string inputStreamId = inputRequestId;

        DateTimeOffset streamFrom = request.ResolveWarmupFrom();
        var runtime = request.Runtime with
        {
            // Do not claim historical bid/ask until broker path supports it.
            UseHistoricalBidAsk = false
        };
        string baseCurrency = request.BaseCurrency ??
                              ResolveBaseCurrency(request.Instrument);

        var simulationOptions = new SimulationOptions
        {
            BaseCurrency = baseCurrency,
            StartingBalance = request.StartingBalance,
            Leverage = request.Leverage,
            CommissionRate = request.CommissionRate,
            SpreadBasisPoints = request.SpreadBasisPoints,
            SlippageBasisPoints = request.SlippageBasisPoints,
            BaseCandleGapPolicy = runtime.BaseCandleGapPolicy,
            CloseOpenPositionsAtEnd = true,
            CandleCapacity = runtime.CandleCapacity,
            LedgerCapacity = runtime.LedgerCapacity,
            OrderEventCapacity = runtime.OrderEventCapacity,
            OcoFillPolicy = runtime.ToOcoFillPolicy()
        };

        var engine = new StreamingComparativeEngine(stream, strategies);
        var stopwatch = Stopwatch.StartNew();
        long lastProcessed = 0;
        DateTimeOffset lastSample = DateTimeOffset.UtcNow;

        if (stream is IHistoricalCandleStreamWithProgress progressStream)
        {
            progressStream.ProgressChanged += download =>
            {
                HistoricalSourceProgress sourceProgress = new()
                {
                    Phase = download.Status,
                    FromCache = download.FromCache,
                    CandlesRead = download.DownloadedCandles,
                    PagesRead = 0,
                    LatestCandle = download.LatestCandleOpenTime,
                    EstimatedCandles = job.Snapshot.EstimatedBaseCandleCount,
                    Percent = job.Snapshot.EstimatedBaseCandleCount is > 0
                        ? Math.Round(100m * download.DownloadedCandles / job.Snapshot.EstimatedBaseCandleCount.Value, 2)
                        : null
                };
                SimulationJobStatus mapped = download.Status switch
                {
                    "DownloadingData" => SimulationJobStatus.DownloadingData,
                    "LoadingCache" or "CacheLoaded" => SimulationJobStatus.LoadingCache,
                    _ => job.Snapshot.Status
                };
                SimulationJobSnapshot next = NextRevision(job.Snapshot) with
                {
                    Status = job.Snapshot.Status is SimulationJobStatus.Running or SimulationJobStatus.WarmingUp
                        ? job.Snapshot.Status
                        : mapped,
                    SourceProgress = sourceProgress,
                    DataSourceStatus = download.Status,
                    StartedAt = job.Snapshot.StartedAt ?? DateTimeOffset.UtcNow
                };
                job.Snapshot = next;
                _ = PersistAsync(next, terminal: false, CancellationToken.None);
                RaiseUpdated(next);
            };
        }

        var progress = new Progress<BacktestProgress>(update =>
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            double seconds = Math.Max(0.001, (now - lastSample).TotalSeconds);
            decimal cps = (decimal)((update.ProcessedBaseCandles - lastProcessed) / seconds);
            lastProcessed = update.ProcessedBaseCandles;
            lastSample = now;

            SimulationJobSnapshot next = NextRevision(job.Snapshot) with
            {
                Status = update.Status,
                CurrentMarketTime = update.CurrentMarketTime,
                ProcessedBaseCandles = update.ProcessedBaseCandles,
                EstimatedBaseCandleCount = update.EstimatedTotalBaseCandles,
                ProgressPercent = update.ProgressPercent,
                CandlesPerSecond = cps > 0 ? cps : job.Snapshot.CandlesPerSecond,
                Strategies = update.Strategies,
                StartedAt = job.Snapshot.StartedAt ?? now,
                DataSourceStatus = update.DataSourceStatus ?? job.Snapshot.DataSourceStatus
            };
            job.Snapshot = next;
            _ = PersistAsync(next, terminal: false, CancellationToken.None);
            RaiseUpdated(next);
            job.ExternalProgress?.Report(update with { CandlesPerSecond = next.CandlesPerSecond });
        });

        var engineOptions = new StreamingComparativeEngineOptions
        {
            SimulationId = job.Id,
            Instrument = request.Instrument,
            EvaluationFrom = request.From,
            EvaluationTo = request.To,
            StreamFrom = streamFrom,
            AnalysisIntervals = runtime.ToTimeframeOptions()
                .EffectiveAnalysisIntervals(
                    strategies.SelectMany(s => s.Agent.RequiredIntervals)),
            Runtime = runtime,
            SimulationOptions = simulationOptions,
            OutputDirectory = job.OutputDirectory,
            InputStreamId = inputStreamId,
            Progress = progress,
            PauseGate = job.PauseGate,
            StatusChanged = async status =>
            {
                await UpdateStatusAsync(job, status, cancellationToken).ConfigureAwait(false);
            }
        };

        var candleRequest = new HistoricalCandleRequest(
            request.Instrument,
            runtime.ExecutionInterval,
            streamFrom,
            request.To,
            runtime.RefreshCache,
            runtime.UseHistoricalBidAsk,
            runtime.SourcePageSize,
            request.CacheDirectory,
            runtime.NoCache);

        ComparativeSimulationResult result = await engine
            .RunAsync(engineOptions, candleRequest, cancellationToken)
            .ConfigureAwait(false);

        stopwatch.Stop();
        return result;
    }

    private async Task UpdateStatusAsync(
        RunningJob job,
        SimulationJobStatus status,
        CancellationToken cancellationToken)
    {
        SimulationJobSnapshot next = NextRevision(job.Snapshot) with
        {
            Status = status,
            StartedAt = job.Snapshot.StartedAt ??
                        (status is SimulationJobStatus.Running or SimulationJobStatus.WarmingUp or SimulationJobStatus.DownloadingData
                            ? DateTimeOffset.UtcNow
                            : job.Snapshot.StartedAt),
            DataSourceStatus = status.ToString()
        };
        job.Snapshot = next;
        await PersistAsync(next, terminal: false, cancellationToken).ConfigureAwait(false);
        RaiseUpdated(next);
    }

    private Task PersistAsync(
        SimulationJobSnapshot snapshot,
        bool terminal,
        CancellationToken cancellationToken) =>
        _snapshotStore.PublishAsync(snapshot, terminal, cancellationToken);

    private static SimulationJobSnapshot NextRevision(SimulationJobSnapshot snapshot) =>
        snapshot with { Revision = snapshot.Revision + 1 };

    private void RaiseUpdated(SimulationJobSnapshot snapshot) => JobUpdated?.Invoke(snapshot);

    private static string BuildInputRequestId(BacktestRequest request)
    {
        // Deterministic identity of the candle request only — not strategies/risk.
        DateTimeOffset streamFrom = request.ResolveWarmupFrom();
        string source = request.Runtime.SourceKind.ToString();
        string exec = BarIntervalParser.Format(request.Runtime.ExecutionInterval);
        string analysisBase = BarIntervalParser.Format(request.Runtime.AnalysisBaseInterval);
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{source}|{request.Environment}|{request.Instrument.Value}|exec:{exec}|abase:{analysisBase}|{streamFrom:O}|{request.To:O}|mid|v3");
    }

    private static IHistoricalCandleStream CreateDefaultStream(BacktestRequest request)
    {
        if (request.InlineCandles is not null)
            return new EnumerablePagedCandleSource(request.InlineCandles);

        BacktestRuntimeOptions runtime = request.Runtime;
        HistoricalCapabilityRegistry.ValidateExecutionInterval(
            runtime.SourceKind,
            runtime.ExecutionInterval);

        if (runtime.SourceKind is HistoricalDataSourceKind.ImportedSecondCandles
            or HistoricalDataSourceKind.RecordedQuotes)
        {
            return new ImportedSecondCandleSource(
                runtime.ImportedCandlePath!,
                request.Instrument,
                runtime.ExecutionInterval);
        }

        if (runtime.SourceKind != HistoricalDataSourceKind.OandaCandles &&
            runtime.SourceKind != HistoricalDataSourceKind.InlineTestData)
        {
            throw new NotSupportedException(
                $"Historical source {runtime.SourceKind} is not wired in this build.");
        }

        (string accountId, string accessToken) = ResolveOandaCredentials(request);
        var oanda = BrokerClientFactory.CreateOanda(new OandaOptions
        {
            Environment = request.Environment,
            AccountId = accountId,
            AccessToken = accessToken
        });
        // Note: the stream outlives a single method call; disposal is owned by the broker
        // client factory lifetime for this process. Integration hosts should inject StreamFactory.
        return new OandaStreamingCandleSource(
            oanda.MarketData,
            request.Environment,
            request.CacheDirectory);
    }

    private static IReadOnlyList<(string Id, ITradingAgent Agent)> CreateDefaultStrategies(
        BacktestRequest request)
    {
        ProgressiveStrategyTimeframes tf = request.Runtime.StrategyTimeframes;
        var strategyOptions = new ProgressiveStrategyOptions
        {
            TrendInterval = tf.TrendInterval,
            ConfirmationInterval = tf.ConfirmationInterval,
            EntryInterval = tf.EntryInterval,
            Quantity = request.Quantity,
            MinimumRewardRisk = request.MinimumRewardRisk
        };

        var list = new List<(string, ITradingAgent)>();
        foreach (string raw in request.Strategies)
        {
            string id = NormalizeStrategyId(raw);
            ITradingAgent agent = id switch
            {
                "legacy" or "legacy-progressive" => new LegacyProgressiveAgent(strategyOptions),
                "improved" or "improved-progressive" => new ImprovedProgressiveAgent(strategyOptions),
                _ => throw new ArgumentException($"Unknown strategy '{raw}'. Use legacy or improved.")
            };
            list.Add((id.StartsWith("legacy", StringComparison.Ordinal) ? "legacy" :
                id.StartsWith("improved", StringComparison.Ordinal) ? "improved" : id, agent));
        }

        return list;
    }

    private static string NormalizeStrategyId(string name)
    {
        string trimmed = name.Trim().ToLowerInvariant();
        if (trimmed.Contains("legacy", StringComparison.Ordinal))
            return "legacy";
        if (trimmed.Contains("improved", StringComparison.Ordinal))
            return "improved";
        return trimmed;
    }

    private static string ResolveBaseCurrency(InstrumentKey instrument)
    {
        string value = instrument.Value;
        int slash = value.LastIndexOf('/');
        if (slash > 0 && slash < value.Length - 1)
            return value[(slash + 1)..].Trim().ToUpperInvariant();
        return "USD";
    }

    private static (string AccountId, string AccessToken) ResolveOandaCredentials(BacktestRequest request)
    {
        string? accountId = request.AccountId
            ?? Environment.GetEnvironmentVariable("OANDA_ACCOUNT_ID")
            ?? Environment.GetEnvironmentVariable("Oanda__AccountId");
        string? token = request.AccessToken
            ?? Environment.GetEnvironmentVariable("OANDA_ACCESS_TOKEN")
            ?? Environment.GetEnvironmentVariable("Oanda__AccessToken");
        if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                "OANDA credentials are required. Set OANDA_ACCOUNT_ID and OANDA_ACCESS_TOKEN, " +
                "or provide InlineCandles for offline runs.");
        }

        return (accountId, token);
    }

    private sealed class RunningJob
    {
        public RunningJob(
            Guid id,
            BacktestRequest request,
            SimulationJobSnapshot snapshot,
            string outputDirectory)
        {
            Id = id;
            Request = request;
            Snapshot = snapshot;
            OutputDirectory = outputDirectory;
            Cancellation = new CancellationTokenSource();
            PauseGate = new AsyncPauseGate();
            Completion = new TaskCompletionSource<ComparativeSimulationResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Guid Id { get; }
        public BacktestRequest Request { get; }
        public string OutputDirectory { get; }
        public CancellationTokenSource Cancellation { get; }
        public AsyncPauseGate PauseGate { get; }
        public TaskCompletionSource<ComparativeSimulationResult> Completion { get; }
        public SimulationJobSnapshot Snapshot { get; set; }
        public IProgress<BacktestProgress>? ExternalProgress { get; set; }
    }
}
