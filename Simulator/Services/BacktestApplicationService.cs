using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agent.Abstractions;
using Agent.Configuration;
using Agent.Factories;
using Brokers;
using Brokers.Abstractions;
using Brokers.Binance;
using Brokers.Models;
using Brokers.Oanda;
using ChartAnnotator.Engine;
using Microsoft.Extensions.Logging;
using Simulator.Abstractions;
using Simulator.Engine;
using Simulator.Jobs;
using Simulator.MarketData;
using Simulator.Models;
using TradingCore.Pipeline;

namespace Simulator.Services;

public sealed class BacktestApplicationServiceOptions
{
    public int MaxConcurrentJobs { get; init; } = 1;
    public int QueueCapacity { get; init; } = 8;
    public Func<BacktestRequest, IHistoricalCandleStream>? StreamFactory { get; init; }
    public Func<BacktestRequest, CancellationToken, Task<HistoricalBrokerCredentials?>>? CredentialResolver { get; init; }
    public Func<BacktestRequest, IReadOnlyList<StrategyFactoryEntry>>? StrategyFactory { get; init; }
}

public sealed record HistoricalBrokerCredentials(
    string? AccountId = null,
    string? AccessToken = null,
    string? ApiKey = null,
    string? SecretKey = null);

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
    private readonly Task _initialization;
    private readonly Task[] _workers;
    private readonly ILogger<BacktestApplicationService>? _logger;
    private bool _disposed;

    /// <summary>Logger is optional (null-safe throughout via ?.) so BacktestRunner's bare
    /// console-app path - no ILogger provider configured at all - keeps working exactly as
    /// before. When a caller does have a real logging pipeline (e.g. DashboardLive, which gets
    /// a durable Serilog sink - see Program.cs), job lifecycle events actually reach it: this
    /// project previously had zero ILogger usage across all 111 files, so even a properly
    /// hosted backtest run left no structured trace of what happened beyond the job snapshot's
    /// own Error field.</summary>
    public BacktestApplicationService(
        ISimulationJobRepository repository,
        BacktestApplicationServiceOptions? options = null,
        ILogger<BacktestApplicationService>? logger = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _snapshotStore = new CoalescingJobSnapshotStore(repository);
        _options = options ?? new BacktestApplicationServiceOptions();
        _logger = logger;
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

        // Do not leave stale "Running" jobs after process restart. Public operations
        // await this owned initialization task; construction never blocks on async I/O.
        _initialization = repository.MarkInterruptedJobsAsync();

        _workers = Enumerable.Range(0, _options.MaxConcurrentJobs)
            .Select(_ => Task.Run(() => WorkerLoopAsync(_serviceLifetime.Token)))
            .ToArray();
    }

    public event Action<SimulationJobSnapshot>? JobUpdated;
    public event Action<Guid, string, SimulatedTradeRecord>? TradeCompleted;

    public async Task<SimulationJobHandle> StartAsync(
        BacktestRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        Guid id = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string outputDirectory = Path.GetFullPath(
            Path.Combine(request.OutputDirectory, id.ToString("N")));
        Directory.CreateDirectory(outputDirectory);

        string inputRequestId = BuildInputRequestId(request);
        string configurationId = BuildSimulationConfigurationId(request, inputRequestId);
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
            SimulationConfigurationId = configurationId,
            Request = request with { AccessToken = null, AccountId = null },
            DataSourceStatus = "Queued"
        };

        var running = new RunningJob(
            id,
            request,
            snapshot,
            outputDirectory,
            PersistAsync,
            RaiseUpdated);
        if (!_running.TryAdd(id, running))
            throw new InvalidOperationException("Failed to register simulation job.");

        await running.PublishInitialAsync(cancellationToken).ConfigureAwait(false);

        await _queue.Writer.WriteAsync(id, cancellationToken).ConfigureAwait(false);
        _logger?.LogInformation(
            "Backtest job {JobId} queued: {Instrument} {From:O} to {To:O}, strategies={Strategies}.",
            id, request.Instrument.Value, request.From, request.To, string.Join(',', request.Strategies));
        return new SimulationJobHandle(id, SimulationJobStatus.Queued);
    }

    public async Task<SimulationJobSnapshot?> GetAsync(
        Guid simulationId,
        CancellationToken cancellationToken = default)
    {
        await _initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (_running.TryGetValue(simulationId, out RunningJob? job))
            return job.Snapshot;

        return await _repository.GetAsync(simulationId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SimulationJobSnapshot>> ListAsync(
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        await _initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await _repository.ListAsync(take, cancellationToken).ConfigureAwait(false);
    }

    public async Task PauseAsync(Guid simulationId, CancellationToken cancellationToken = default)
    {
        await _initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!_running.TryGetValue(simulationId, out RunningJob? job))
        {
            SimulationJobSnapshot? existing = await _repository.GetAsync(simulationId, cancellationToken)
                .ConfigureAwait(false);
            if (existing is null)
                throw new KeyNotFoundException($"Simulation '{simulationId}' was not found.");
            // Terminal jobs: treat pause as a no-op so UI retries after completion don't look like crashes.
            if (existing.IsComplete)
                return;
            throw new InvalidOperationException($"Simulation '{simulationId}' is not running in this process.");
        }

        SimulationJobSnapshot current = job.Snapshot;
        if (current.IsComplete)
            return;
        if (current.Status is SimulationJobStatus.Paused)
            return;
        if (current.Status is not (
                SimulationJobStatus.PreparingData or
                SimulationJobStatus.DownloadingData or
                SimulationJobStatus.LoadingCache or
                SimulationJobStatus.WarmingUp or
                SimulationJobStatus.Running))
        {
            throw new InvalidOperationException(
                $"Simulation '{simulationId}' cannot be paused while {current.Status}.");
        }

        job.PauseGate.Pause();
        await UpdateStatusAsync(job, SimulationJobStatus.Paused, cancellationToken).ConfigureAwait(false);
    }

    public async Task ResumeAsync(Guid simulationId, CancellationToken cancellationToken = default)
    {
        await _initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!_running.TryGetValue(simulationId, out RunningJob? job))
        {
            SimulationJobSnapshot? existing = await _repository.GetAsync(simulationId, cancellationToken)
                .ConfigureAwait(false);
            if (existing is null)
                throw new KeyNotFoundException($"Simulation '{simulationId}' was not found.");
            if (existing.IsComplete)
            {
                string detail = string.IsNullOrWhiteSpace(existing.Error)
                    ? string.Empty
                    : $" Failure detail: {existing.Error}";
                throw new InvalidOperationException(
                    $"Simulation '{simulationId}' has already finished as {existing.Status}.{detail} " +
                    "Start a new simulation to run again — resume only works from Paused.");
            }

            throw new InvalidOperationException($"Simulation '{simulationId}' is not running in this process.");
        }

        SimulationJobSnapshot current = job.Snapshot;
        if (current.IsComplete)
        {
            string detail = string.IsNullOrWhiteSpace(current.Error)
                ? string.Empty
                : $" Failure detail: {current.Error}";
            throw new InvalidOperationException(
                $"Simulation '{simulationId}' has already finished as {current.Status}.{detail} " +
                "Start a new simulation to run again — resume only works from Paused.");
        }

        if (current.Status == SimulationJobStatus.Running)
            return;
        if (current.Status != SimulationJobStatus.Paused)
        {
            throw new InvalidOperationException(
                $"Simulation '{simulationId}' cannot be resumed while {current.Status}. Resume only works when the job is Paused.");
        }

        job.PauseGate.Resume();
        await UpdateStatusAsync(job, SimulationJobStatus.Running, cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelAsync(Guid simulationId, CancellationToken cancellationToken = default)
    {
        await _initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
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
        await _initialization.ConfigureAwait(false);
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
            await job.StopActorAsync().ConfigureAwait(false);
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
                SimulationJobSnapshot completed = await job.ApplyAsync(current => NextRevision(current) with
                {
                    Status = SimulationJobStatus.Completed,
                    CompletedAt = DateTimeOffset.UtcNow,
                    IsComplete = true,
                    ProgressPercent = 100m,
                    InputHash = result.InputHash,
                    InputStreamId = result.InputStreamId,
                    DataQuality = result.DataQuality,
                    WorkerMetrics = result.Strategies.Select(item => item.Metrics).ToArray(),
                    PortfolioPerformance = result.PortfolioPerformance,
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
                        Status = item.IsComplete ? "Completed" : "Incomplete",
                        Performance = StrategyPerformanceSnapshot.FromTrades(item.Result.Trades)
                    }).ToArray()
                }, terminal: true, CancellationToken.None).ConfigureAwait(false);
                job.Completion.TrySetResult(result);
                _logger?.LogInformation(
                    "Backtest job {JobId} completed: {StrategyCount} strategies, {ProcessedBaseCandles} candles.",
                    job.Id, result.Strategies.Count, result.ProcessedBaseCandles);
            }
            catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested)
            {
                SimulationJobSnapshot cancelled = await job.ApplyAsync(current => NextRevision(current) with
                {
                    Status = SimulationJobStatus.Cancelled,
                    CompletedAt = DateTimeOffset.UtcNow,
                    IsComplete = true
                }, terminal: true, CancellationToken.None).ConfigureAwait(false);
                job.Completion.TrySetCanceled(job.Cancellation.Token);
                _logger?.LogInformation("Backtest job {JobId} cancelled.", job.Id);
            }
            catch (Exception exception)
            {
                _logger?.LogError(exception, "Backtest job {JobId} failed.", job.Id);
                SimulationJobSnapshot failed = await job.ApplyAsync(current => NextRevision(current) with
                {
                    Status = SimulationJobStatus.Failed,
                    CompletedAt = DateTimeOffset.UtcNow,
                    IsComplete = true,
                    // Full ToString() (type, message, stack trace, inner exceptions), not just
                    // Message - a job-level failure isn't attributable to one strategy session
                    // the way StrategySimulationSession.FailureMessage already is, so this is
                    // the only record of what actually broke. Matches the existing per-strategy
                    // precedent (StrategySimulationSession._failureMessage).
                    Error = exception.ToString()
                }, terminal: true, CancellationToken.None).ConfigureAwait(false);
                job.Completion.TrySetException(exception);
            }
            finally
            {
                // Release in-memory resources; file-backed snapshot remains.
                if (_running.TryRemove(id, out RunningJob? finished))
                {
                    await finished.StopActorAsync().ConfigureAwait(false);
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

        IReadOnlyList<StrategyFactoryEntry> strategies =
            (_options.StrategyFactory ?? CreateDefaultStrategies)(request);
        HistoricalBrokerCredentials? credentials = _options.StreamFactory is null && _options.CredentialResolver is not null
            ? await _options.CredentialResolver(request, cancellationToken).ConfigureAwait(false)
            : null;
        IHistoricalCandleStream stream = _options.StreamFactory is not null
            ? _options.StreamFactory(request)
            : CreateDefaultStream(request, credentials);
        await using IAsyncDisposable? ownedStream = stream as IAsyncDisposable;

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
            OcoFillPolicy = runtime.ToOcoFillPolicy(),
            ExecutionModel = runtime.Execution,
            Financing = runtime.Financing
        };

        var engine = new StreamingComparativeEngine(stream, strategies);
        var stopwatch = Stopwatch.StartNew();
        long lastProcessed = 0;
        DateTimeOffset lastSample = DateTimeOffset.UtcNow;

        if (stream is IHistoricalCandleStreamWithProgress progressStream)
        {
            progressStream.ProgressChanged += download =>
            {
                job.Post(current =>
                {
                    HistoricalSourceProgress sourceProgress = new()
                    {
                        Phase = download.Status,
                        FromCache = download.FromCache,
                        CandlesRead = download.DownloadedCandles,
                        PagesRead = download.PagesRead,
                        LatestCandle = download.LatestCandleOpenTime,
                        EstimatedCandles = current.EstimatedBaseCandleCount,
                        Percent = current.EstimatedBaseCandleCount is > 0
                            ? Math.Round(100m * download.DownloadedCandles / current.EstimatedBaseCandleCount.Value, 2)
                            : null
                    };
                    SimulationJobStatus mapped = download.Status switch
                    {
                        "DownloadingData" => SimulationJobStatus.DownloadingData,
                        "ReadingCache" or "CacheLoaded" => SimulationJobStatus.LoadingCache,
                        _ => current.Status
                    };
                    return NextRevision(current) with
                    {
                        Status = current.Status is SimulationJobStatus.Running or
                            SimulationJobStatus.WarmingUp or
                            SimulationJobStatus.Paused or
                            SimulationJobStatus.Cancelling
                            ? current.Status
                            : mapped,
                        SourceProgress = sourceProgress,
                        DataSourceStatus = download.Status,
                        StartedAt = current.StartedAt ?? DateTimeOffset.UtcNow
                    };
                });
            };
        }

        var progress = new CallbackProgress<BacktestProgress>(update =>
        {
            job.Post(current =>
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                double seconds = Math.Max(0.001, (now - lastSample).TotalSeconds);
                decimal cps = (decimal)((update.ProcessedBaseCandles - lastProcessed) / seconds);
                lastProcessed = update.ProcessedBaseCandles;
                lastSample = now;
                return NextRevision(current) with
                {
                    Status = update.Status,
                    CurrentMarketTime = update.CurrentMarketTime,
                    ProcessedBaseCandles = update.ProcessedBaseCandles,
                    EstimatedBaseCandleCount = update.EstimatedTotalBaseCandles,
                    ProgressPercent = update.ProgressPercent,
                    CandlesPerSecond = cps > 0 ? cps : current.CandlesPerSecond,
                    Strategies = update.Strategies,
                    StartedAt = current.StartedAt ?? now,
                    DataSourceStatus = update.DataSourceStatus ?? current.DataSourceStatus
                };
            }, next => job.ExternalProgress?.Report(
                update with { CandlesPerSecond = next.CandlesPerSecond }));
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
            AnnotationOptions = runtime.AnnotationOptions,
            // Previously never set from any Runtime field - meta-labeling was 100% dead in
            // every production run regardless of what BacktestRuntimeOptions.MetaModel asked
            // for (2026-07-16 audit §14-17 fix).
            MetaLabelModel = runtime.MetaModel.Enabled
                ? new Simulator.Calibration.CalibratedSetupMetaModel(runtime.MetaModelArtifact!, runtime.MetaModel)
                : null,
            CaptureMarketReplay = request.CaptureMarketReplay,
            SimulationOptions = simulationOptions,
            OutputDirectory = job.OutputDirectory,
            InputStreamId = inputStreamId,
            SimulationConfigurationId = job.Snapshot.SimulationConfigurationId,
            Progress = progress,
            PauseGate = job.PauseGate,
            StatusChanged = async status =>
            {
                await UpdateStatusAsync(job, status, cancellationToken).ConfigureAwait(false);
            },
            TradeCompleted = (strategyId, trade) =>
                job.NotifyAsync(() => TradeCompleted?.Invoke(job.Id, strategyId, trade), cancellationToken)
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
        await job.ApplyAsync(current => NextRevision(current) with
        {
            Status = status,
            StartedAt = current.StartedAt ??
                        (status is SimulationJobStatus.Running or SimulationJobStatus.WarmingUp or SimulationJobStatus.DownloadingData
                            ? DateTimeOffset.UtcNow
                            : current.StartedAt),
            DataSourceStatus = status.ToString()
        }, terminal: false, cancellationToken).ConfigureAwait(false);
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
        // Deterministic identity of the candle request(s) only — not strategies/risk.
        // Folds in every distinct traded instrument (not just the primary Instrument
        // field) so two multi-instrument requests trading different instrument sets
        // never collide on cache/replay identity.
        DateTimeOffset streamFrom = request.ResolveWarmupFrom();
        string source = request.Runtime.SourceKind.ToString();
        string exec = BarIntervalParser.Format(request.Runtime.ExecutionInterval);
        string instruments = string.Join(
            ',', request.TradedInstruments().Select(instrument => instrument.Value).OrderBy(v => v, StringComparer.Ordinal));
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{source}|{request.Environment}|{instruments}|exec:{exec}|{streamFrom:O}|{request.To:O}|mid|v3");
    }

    private static string BuildSimulationConfigurationId(
        BacktestRequest request,
        string inputRequestId)
    {
        BacktestRuntimeOptions runtime = request.Runtime;
        string quantitativeConfiguration = JsonSerializer.Serialize(runtime, new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });
        // Only present for §7 multi-instrument requests, so a plain single-instrument
        // request's configuration identity stays byte-for-byte unchanged from before.
        string assignmentsSegment = request.StrategyAssignments is { Count: > 0 } assignmentsForId
            ? "assignments:" + string.Join(',', assignmentsForId
                .Select(assignment => $"{assignment.StrategyType}@{assignment.Instrument.Value}")
                .OrderBy(v => v, StringComparer.Ordinal)) + "|"
            : string.Empty;
        string canonical = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{inputRequestId}|analysis-base:{BarIntervalParser.Format(runtime.AnalysisBaseInterval)}|" +
            $"analysis:{string.Join(',', runtime.AnalysisIntervals.Select(BarIntervalParser.Format))}|" +
            $"strategy-tf:{BarIntervalParser.Format(runtime.StrategyTimeframes.TrendInterval)}," +
            $"secondary={string.Join(';', runtime.StrategyTimeframes.SecondaryTrendIntervals.Select(BarIntervalParser.Format))}," +
            $"setup={string.Join(';', runtime.StrategyTimeframes.SetupIntervals.Select(BarIntervalParser.Format))}," +
            $"{BarIntervalParser.Format(runtime.StrategyTimeframes.ConfirmationInterval)}," +
            $"additional-confirm={string.Join(';', runtime.StrategyTimeframes.AdditionalConfirmationIntervals.Select(BarIntervalParser.Format))}," +
            $"{BarIntervalParser.Format(runtime.StrategyTimeframes.EntryInterval)}," +
            $"minimums={runtime.StrategyTimeframes.MinimumSecondaryTrendAlignments}/" +
            $"{runtime.StrategyTimeframes.MinimumSetupAlignments}/" +
            $"{runtime.StrategyTimeframes.MinimumConfirmationAlignments}," +
            $"veto={runtime.StrategyTimeframes.StrongOppositionVeto}|" +
            $"strategies:{string.Join(',', request.Strategies)}|" +
            $"{assignmentsSegment}" +
            $"balance:{request.StartingBalance}|" +
            $"base-currency:{request.BaseCurrency}|quantity:{request.Quantity}|" +
            $"leverage:{request.Leverage}|commission:{request.CommissionRate}|" +
            $"sizing:{runtime.PositionSizing.Mode},{runtime.PositionSizing.FixedQuantity}," +
            $"{runtime.PositionSizing.RiskPercentOfEquity},{runtime.PositionSizing.FixedCashRisk}," +
            $"{runtime.PositionSizing.MinimumQuantity},{runtime.PositionSizing.MaximumQuantity}," +
            $"{runtime.PositionSizing.QuantityStep},{runtime.PositionSizing.MaximumAccountMarginUsagePercent}," +
            $"{runtime.PositionSizing.MaximumSinglePositionMarginPercent}," +
            $"{runtime.PositionSizing.Leverage},{runtime.PositionSizing.EstimatedRoundTripCostBasisPoints}|" +
            $"spread:{request.SpreadBasisPoints}|slippage:{request.SlippageBasisPoints}|" +
            $"rr:{request.MinimumRewardRisk}|ambiguity:{runtime.AmbiguousIntrabarPolicy}|" +
            $"legacy-pm:{PositionManagementIdentity(runtime.LegacyPositionManagement)}|" +
            $"improved-pm:{PositionManagementIdentity(runtime.ImprovedPositionManagement)}|" +
            $"safety:{SafetyIdentity(runtime.SafetyOptions)}|" +
            $"runtime:{quantitativeConfiguration}|v5");
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string PositionManagementIdentity(TradeManager.PositionManagementOptions options)
    {
        string scaleOut = string.Join(';', options.ScaleOutRules
            .OrderBy(rule => rule.StageId, StringComparer.Ordinal)
            .Select(rule => string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{rule.StageId}:{rule.ActivationR}:{rule.MinimumOpenProfitR}:" +
                $"{rule.FractionOfInitialQuantity}:{rule.TriggerMode}")));
        string profitFloors = string.Join(';', options.ProfitFloorRules
            .OrderBy(rule => rule.ActivationR)
            .Select(rule => string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{rule.ActivationR}:{rule.LockedProfitR}")));
        string giveback = string.Join(';', options.MaximumGivebackRules
            .OrderBy(rule => rule.ActivationR)
            .Select(rule => string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{rule.ActivationR}:{rule.MaximumGivebackR}")));
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{options.Mode},{(options.ManagementInterval is BarInterval interval ? BarIntervalParser.Format(interval) : "default")}," +
            $"mechanical={options.EvaluateMechanicalProtectionOnEveryExecutionFrame}," +
            $"fast={(options.FastStructureInterval is BarInterval fast ? BarIntervalParser.Format(fast) : "default")}," +
            $"main={(options.MainStructureInterval is BarInterval main ? BarIntervalParser.Format(main) : "default")}," +
            $"thesis={(options.ThesisInterval is BarInterval thesis ? BarIntervalParser.Format(thesis) : "default")}," +
            $"{options.BreakEvenActivationR},{options.StructureTrailActivationR},{options.AtrBufferMultiplier}," +
            $"{options.BreakEvenBufferAtr},{options.MinimumStopImprovementAtr},{options.MinimumStopImprovementTicks}," +
            $"{options.MinimumAnalysisBarsBetweenAmendments},{options.ExitOnAdverseStructureBreak}," +
            $"neo-wave-exit={options.EnableNeoWaveInvalidationExit}:{options.NeoWaveInvalidationBufferAtr}," +
            $"supply-demand-management={options.SupplyDemandManagementEnabled}," +
            $"liquidity-management={options.LiquidityManagementEnabled}," +
            $"structural-management-policy={options.StructuralManagementPolicyRevision}," +
            $"{options.PreserveBracketTarget},{options.IncludeEstimatedExitCostsAtBreakEven}," +
            $"{options.EnableScaleOut},{options.MinimumRunnerFraction},{options.OpposingStructureProximityAtr}," +
            $"{options.MinimumAnalysisBarsBetweenReductions},{scaleOut}," +
            $"{options.EnableProfitFloor},{profitFloors},{options.EnableMaximumGiveback},{giveback}," +
            $"{options.EnableStagnationReduction},{options.StagnationMinimumOpenProfitR}," +
            $"{options.StagnationBars},{options.StagnationMinimumMfeAdvanceR},{options.StagnationReductionFraction}," +
            $"{options.EnableStructuralDeteriorationReduction}," +
            $"{options.StructuralDeteriorationReductionFraction}," +
            $"{options.MaximumStructuralDeteriorationReductions}," +
            $"{options.EnableMomentumDecayReduction},{options.MomentumDecayMinimumOpenProfitR}," +
            $"{options.MomentumDecayReductionFraction},{options.MaximumMomentumDecayReductions}," +
            $"{options.EnableVolatilityExhaustionReduction}," +
            $"{options.VolatilityExhaustionMinimumOpenProfitR}," +
            $"{options.VolatilityExhaustionReductionFraction}," +
            $"{options.MaximumVolatilityExhaustionReductions}," +
            $"{options.EnableRiskWindowReduction},{options.RiskWindowStartUtc}," +
            $"{options.RiskWindowEndUtc},{options.RiskWindowMinimumOpenProfitR}," +
            $"{options.RiskWindowReductionFraction}," +
            $"{options.EnableExecutionCostStressReduction},{options.MaximumSpreadToAtrRatio}," +
            $"{options.ExecutionCostStressMinimumOpenProfitR}," +
            $"{options.ExecutionCostStressReductionFraction}");
    }

    private static string SafetyIdentity(RiskManager.Safety.TradingSafetyOptions options) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{options.MaximumDailyLoss},{options.MaximumWeeklyLoss}," +
            $"{options.MaximumConsecutiveLosses},{options.DailyEquityProfitTarget}," +
            $"{options.DailyEquityGivebackActivation},{options.MaximumDailyEquityGiveback}," +
            $"{options.TripOnCriticalDataQualityIssue}");

    private static IHistoricalCandleStream CreateDefaultStream(
        BacktestRequest request,
        HistoricalBrokerCredentials? credentials = null)
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

        if (runtime.SourceKind == HistoricalDataSourceKind.BinanceCandles)
        {
            var binance = BrokerClientFactory.CreateBinance(new BinanceOptions
            {
                Environment = BrokerEnvironment.Live,
                ApiKey = credentials?.ApiKey,
                SecretKey = credentials?.SecretKey,
                BaseAddress = ResolveBinanceMarketDataBaseAddress()
            });
            return new BinanceStreamingCandleSource(
                binance.MarketData,
                BrokerEnvironment.Live,
                request.CacheDirectory,
                binance);
        }

        if (runtime.SourceKind != HistoricalDataSourceKind.OandaCandles &&
            runtime.SourceKind != HistoricalDataSourceKind.InlineTestData)
        {
            throw new NotSupportedException(
                $"Historical source {runtime.SourceKind} is not wired in this build.");
        }

        (string accountId, string accessToken) = ResolveOandaCredentials(request, credentials);
        var oanda = BrokerClientFactory.CreateOanda(new OandaOptions
        {
            Environment = request.Environment,
            AccountId = accountId,
            AccessToken = accessToken
        });
        return new OandaStreamingCandleSource(
            oanda.MarketData,
            request.Environment,
            request.CacheDirectory,
            oanda);
    }

    private static IReadOnlyList<StrategyFactoryEntry> CreateDefaultStrategies(
        BacktestRequest request)
    {
        if (request.StrategyAssignments is { Count: > 0 } assignments)
        {
            return assignments
                .Select(assignment =>
                {
                    TradingAgentDefinition definition = request.ResolveAgentDefinition(
                        assignment.StrategyType,
                        assignment.AgentDefinitionOverride,
                        assignment.AgentOptionsOverride);
                    string typeId = TradingAgentTypeIds.Format(definition.Kind);
                    return new StrategyFactoryEntry(
                        assignment.Id ?? $"{typeId}:{assignment.Instrument.Value}",
                        TradingAgentFactory.Create(definition),
                        assignment.Instrument,
                        assignment.AnalysisOptionsOverride,
                        assignment.Mode,
                        assignment.PolicyBundleId,
                        assignment.PolicyRevision);
                })
                .ToArray();
        }

        var list = new List<StrategyFactoryEntry>();
        foreach (string raw in request.Strategies)
        {
            TradingAgentDefinition definition = request.ResolveAgentDefinition(raw);
            string id = TradingAgentTypeIds.Format(definition.Kind);
            ITradingAgent agent = TradingAgentFactory.Create(definition);
            list.Add(new StrategyFactoryEntry(
                id,
                agent,
                request.Instrument));
        }

        return list;
    }

    private static string NormalizeStrategyId(string name) =>
        TradingAgentTypeIds.Format(TradingAgentTypeIds.Parse(name));

    private static string ResolveBaseCurrency(InstrumentKey instrument)
    {
        string value = instrument.Value;
        int slash = value.LastIndexOf('/');
        if (slash > 0 && slash < value.Length - 1)
            return value[(slash + 1)..].Trim().ToUpperInvariant();
        return "USD";
    }

    private static Uri ResolveBinanceMarketDataBaseAddress()
    {
        string? configured = Environment.GetEnvironmentVariable("BINANCE_MARKET_DATA_BASE_URL");
        return Uri.TryCreate(configured, UriKind.Absolute, out Uri? address) &&
               address.Scheme == Uri.UriSchemeHttps
            ? address
            : new Uri("https://data-api.binance.vision/");
    }

    private static (string AccountId, string AccessToken) ResolveOandaCredentials(
        BacktestRequest request,
        HistoricalBrokerCredentials? credentials)
    {
        string? accountId = credentials?.AccountId
            ?? request.AccountId;
        string? token = credentials?.AccessToken
            ?? request.AccessToken;
        if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                "OANDA credentials are required in the broker credential database, " +
                "or provide InlineCandles for offline runs.");
        }

        return (accountId, token);
    }

    private sealed class RunningJob
    {
        private readonly Channel<JobStateMessage> _updates;
        private readonly Func<SimulationJobSnapshot, bool, CancellationToken, Task> _persist;
        private readonly Action<SimulationJobSnapshot> _raiseUpdated;
        private readonly Task _actor;
        private SimulationJobSnapshot _snapshot;
        private Exception? _lastActorError;

        public RunningJob(
            Guid id,
            BacktestRequest request,
            SimulationJobSnapshot snapshot,
            string outputDirectory,
            Func<SimulationJobSnapshot, bool, CancellationToken, Task> persist,
            Action<SimulationJobSnapshot> raiseUpdated)
        {
            Id = id;
            Request = request;
            _snapshot = snapshot;
            OutputDirectory = outputDirectory;
            Cancellation = new CancellationTokenSource();
            PauseGate = new AsyncPauseGate();
            Completion = new TaskCompletionSource<ComparativeSimulationResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _persist = persist;
            _raiseUpdated = raiseUpdated;
            _updates = Channel.CreateUnbounded<JobStateMessage>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
            _actor = RunActorAsync();
        }

        public Guid Id { get; }
        public BacktestRequest Request { get; }
        public string OutputDirectory { get; }
        public CancellationTokenSource Cancellation { get; }
        public AsyncPauseGate PauseGate { get; }
        public TaskCompletionSource<ComparativeSimulationResult> Completion { get; }
        public SimulationJobSnapshot Snapshot => Volatile.Read(ref _snapshot);
        public Exception? LastActorError => Volatile.Read(ref _lastActorError);
        public IProgress<BacktestProgress>? ExternalProgress { get; set; }

        public Task<SimulationJobSnapshot> PublishInitialAsync(CancellationToken cancellationToken) =>
            EnqueueAsync(new JobStateMessage
            {
                PublishCurrent = true,
                Completion = NewCompletion()
            }, cancellationToken);

        public Task<SimulationJobSnapshot> ApplyAsync(
            Func<SimulationJobSnapshot, SimulationJobSnapshot> transition,
            bool terminal,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(transition);
            return EnqueueAsync(new JobStateMessage
            {
                Transition = transition,
                Terminal = terminal,
                Completion = NewCompletion()
            }, cancellationToken);
        }

        public void Post(
            Func<SimulationJobSnapshot, SimulationJobSnapshot> transition,
            Action<SimulationJobSnapshot>? afterApplied = null)
        {
            ArgumentNullException.ThrowIfNull(transition);
            if (!_updates.Writer.TryWrite(new JobStateMessage
                {
                    Transition = transition,
                    AfterApplied = afterApplied
                }))
            {
                throw new InvalidOperationException($"The state actor for job '{Id}' is closed.");
            }
        }

        public async Task NotifyAsync(Action notification, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(notification);
            await EnqueueAsync(new JobStateMessage
            {
                Notification = notification,
                Completion = NewCompletion()
            }, cancellationToken).ConfigureAwait(false);
        }

        public async Task StopActorAsync()
        {
            _updates.Writer.TryComplete();
            await _actor.ConfigureAwait(false);
        }

        private async Task<SimulationJobSnapshot> EnqueueAsync(
            JobStateMessage message,
            CancellationToken cancellationToken)
        {
            await _updates.Writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
            return await message.Completion!.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task RunActorAsync()
        {
            await foreach (JobStateMessage message in _updates.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    SimulationJobSnapshot current = Snapshot;
                    SimulationJobSnapshot next = message.Transition is null
                        ? current
                        : message.Transition(current);
                    if (message.Transition is not null && next.Revision <= current.Revision)
                        next = next with { Revision = current.Revision + 1 };

                    if (message.Transition is not null)
                        Volatile.Write(ref _snapshot, next);

                    if (message.Transition is not null || message.PublishCurrent)
                    {
                        _raiseUpdated(next);
                        await _persist(next, message.Terminal, CancellationToken.None).ConfigureAwait(false);
                    }

                    message.Notification?.Invoke();
                    message.AfterApplied?.Invoke(next);
                    message.Completion?.TrySetResult(next);
                }
                catch (Exception exception)
                {
                    Volatile.Write(ref _lastActorError, exception);
                    message.Completion?.TrySetException(exception);
                    if (message.Completion is null)
                        Cancellation.Cancel();
                }
            }
        }

        private static TaskCompletionSource<SimulationJobSnapshot> NewCompletion() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private sealed record JobStateMessage
        {
            public Func<SimulationJobSnapshot, SimulationJobSnapshot>? Transition { get; init; }
            public Action<SimulationJobSnapshot>? AfterApplied { get; init; }
            public Action? Notification { get; init; }
            public bool PublishCurrent { get; init; }
            public bool Terminal { get; init; }
            public TaskCompletionSource<SimulationJobSnapshot>? Completion { get; init; }
        }
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        private readonly Action<T> _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        public void Report(T value) => _callback(value);
    }
}
