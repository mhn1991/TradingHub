using System.Text.Json;
using System.Threading.Channels;
using Agent.Models;
using Agent.Strategies.DivergenceReversal;
using Brokers.Models;
using ChartAnnotator.Engine;
using ChartAnnotator.Indicators;
using ChartAnnotator.MarketData;
using ChartAnnotator.Models;
using Simulator.MarketData;

namespace Dashboard.Live;

public sealed class AgentDebugJob
{
    private readonly Channel<AgentDebugEvent> _channel = Channel.CreateUnbounded<AgentDebugEvent>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });

    public AgentDebugJob(Guid id, AgentDebugRunRequest request)
    {
        Id = id;
        Request = request;
    }

    public Guid Id { get; }
    public AgentDebugRunRequest Request { get; }
    public ChannelReader<AgentDebugEvent> Reader => _channel.Reader;
    internal ChannelWriter<AgentDebugEvent> Writer => _channel.Writer;
}

/// <summary>
/// In-memory registry of running/completed agent-debug jobs. One process's worth of state is
/// fine here - this is a diagnostic tool, not something that needs to survive a restart.
/// </summary>
public sealed class AgentDebugJobRegistry
{
    private readonly Dictionary<Guid, AgentDebugJob> _jobs = [];
    private readonly Lock _lock = new();

    public AgentDebugJob Start(AgentDebugRunRequest request, string cacheDirectory)
    {
        var job = new AgentDebugJob(Guid.NewGuid(), request);
        lock (_lock)
        {
            _jobs[job.Id] = job;
        }

        _ = Task.Run(() => AgentDebugRunner.RunAsync(request, job.Writer, cacheDirectory, CancellationToken.None));
        return job;
    }

    public bool TryGet(Guid id, out AgentDebugJob job)
    {
        lock (_lock)
        {
            return _jobs.TryGetValue(id, out job!);
        }
    }
}

/// <summary>
/// Drives DivergenceReversalAgent against real cached candle data outside the
/// Simulator/BacktestApplicationService/execution pipeline entirely - true lowest-timeframe-up
/// aggregation (MultiTimeframeAggregator, the same component the real engine uses) feeding
/// ChartAnnotationEngine for indicators, with a hand-rolled synthetic position tracker standing
/// in for real execution. See PROJECT_STATE.md §2.8's "agent debugger" entry.
/// </summary>
public static class AgentDebugRunner
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task RunAsync(
        AgentDebugRunRequest request,
        ChannelWriter<AgentDebugEvent> writer,
        string cacheDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            await RunCoreAsync(request, writer, cacheDirectory, cancellationToken);
            await writer.WriteAsync(new AgentDebugEvent
            {
                Type = AgentDebugEventType.Complete,
                Time = DateTimeOffset.UtcNow
            }, cancellationToken);
        }
        catch (Exception exception)
        {
            await writer.WriteAsync(new AgentDebugEvent
            {
                Type = AgentDebugEventType.Error,
                Time = DateTimeOffset.UtcNow,
                Message = exception.Message
            }, cancellationToken);
        }
        finally
        {
            writer.Complete();
        }
    }

    private static async Task RunCoreAsync(
        AgentDebugRunRequest request,
        ChannelWriter<AgentDebugEvent> writer,
        string cacheDirectory,
        CancellationToken cancellationToken)
    {
        var instrument = new InstrumentKey(request.Instrument);
        BarInterval[] monitored = request.MonitoredTimeframes.Select(BarIntervalParser.Parse).ToArray();
        BarInterval[] confirmation = request.ConfirmationTimeframes.Select(BarIntervalParser.Parse).ToArray();
        BarInterval[] allTargets = [.. monitored, .. confirmation];
        BarInterval baseInterval = BarInterval.Minutes(1);

        string? cachePath = ResolveCacheFile(cacheDirectory, request.Instrument, "1m", request.WarmupStart, request.RunEnd);
        if (cachePath is null)
        {
            throw new InvalidOperationException(
                $"No cached 1m data covers {request.WarmupStart:yyyy-MM-dd} .. {request.RunEnd:yyyy-MM-dd} for {request.Instrument}. " +
                "Pick a range inside the cached data (see GET /api/agent-debug/instruments).");
        }

        var options = new DivergenceReversalStrategyOptions
        {
            MonitoredIntervals = monitored,
            ConfirmationIntervals = confirmation,
            Quantity = request.Quantity,
            RsiOverbought = request.RsiOverbought,
            RsiOversold = request.RsiOversold,
            StochRsiFastOverbought = request.StochRsiFastOverbought,
            StochRsiFastOversold = request.StochRsiFastOversold,
            PartialStochRsiFastOverbought = request.PartialStochRsiFastOverbought,
            PartialStochRsiFastOversold = request.PartialStochRsiFastOversold,
            ProtectiveStopAtrMultiple = request.ProtectiveStopAtrMultiple
        };
        var agent = new DivergenceReversalAgent(options);

        // ResetIncompleteBuckets: real FX data has weekend/holiday gaps in the 1m base stream;
        // the default Throw policy is for detecting genuine data-integrity bugs in a curated feed,
        // not for tolerating normal market-closed periods.
        var aggregator = new MultiTimeframeAggregator(instrument, allTargets, gapPolicy: BaseCandleGapPolicy.ResetIncompleteBuckets);
        var engine = new ChartAnnotationEngine();
        // Diagnostic-only mirrors of DivergenceReversalAgent's private StochRSI-fast divergence
        // tracking - display purposes only, never feeds the actual decision (that always comes
        // from calling agent.EvaluateAsync below).
        Dictionary<BarInterval, StochRsiAnalysisState> diagnosticStochRsi = allTargets.ToDictionary(
            interval => interval,
            _ => new StochRsiAnalysisState());
        Dictionary<BarInterval, DateTimeOffset?> diagnosticLastProcessed = allTargets.ToDictionary(
            interval => interval, _ => (DateTimeOffset?)null);
        HashSet<BarInterval> monitoredSet = [.. monitored];

        BrokerPosition? syntheticPosition = null;
        int totalTrades = 0;
        bool inRun = false;

        await foreach (MarketCandle marketCandle in StreamingCandleCache.ReadStreamAsync(cachePath, instrument, baseInterval, cancellationToken))
        {
            Candle baseCandle = marketCandle.Mid;
            if (baseCandle.OpenTime < request.WarmupStart)
                continue;
            if (baseCandle.OpenTime >= request.RunEnd)
                break;

            if (!inRun && baseCandle.OpenTime >= request.RunStart)
            {
                inRun = true;
                await writer.WriteAsync(new AgentDebugEvent
                {
                    Type = AgentDebugEventType.Status,
                    Time = baseCandle.OpenTime,
                    Message = "Warm-up complete - entering run window."
                }, cancellationToken);
            }

            IReadOnlyList<CandleClosedEvent> closedEvents = aggregator.Apply(baseCandle);
            foreach (CandleClosedEvent closedEvent in closedEvents)
            {
                AnalysisSnapshot snapshot = await engine.ProcessAsync(closedEvent, runtimeContext: null, cancellationToken);
                if (!inRun)
                    continue;

                await writer.WriteAsync(BuildCandleEvent(closedEvent.Interval, snapshot), cancellationToken);

                if (allTargets.Contains(closedEvent.Interval))
                {
                    AgentDebugEvent? diagnostic = BuildDiagnosticEvent(
                        snapshot, options,
                        diagnosticStochRsi[closedEvent.Interval],
                        diagnosticLastProcessed,
                        closedEvent.Interval,
                        monitoredSet.Contains(closedEvent.Interval) ? "trigger" : "confirmation");
                    if (diagnostic is not null)
                        await writer.WriteAsync(diagnostic, cancellationToken);
                }
            }

            if (!inRun)
                continue;

            bool triggerClosed = closedEvents.Any(evt => evt.Interval == agent.TriggerInterval);
            if (!triggerClosed)
                continue;

            IReadOnlyDictionary<BarInterval, AnalysisSnapshot> snapshots = allTargets
                .Select(interval => (interval, snapshot: engine.GetLatest(instrument, interval)))
                .Where(pair => pair.snapshot is not null)
                .ToDictionary(pair => pair.interval, pair => pair.snapshot!);
            if (snapshots.Count == 0)
                continue;

            var context = new AgentMarketContext
            {
                Instrument = instrument,
                Timestamp = baseCandle.OpenTime,
                Analysis = new MultiTimeframeAnalysis(instrument, baseCandle.OpenTime, snapshots),
                Account = new AccountSnapshot { AccountId = "agent-debug", CanTrade = true },
                Positions = syntheticPosition is null ? [] : [syntheticPosition],
                OpenOrders = []
            };

            AgentDecision decision = await agent.EvaluateAsync(context, cancellationToken);
            if (decision.Action != AgentAction.Observe)
            {
                await writer.WriteAsync(new AgentDebugEvent
                {
                    Type = AgentDebugEventType.Decision,
                    Time = baseCandle.OpenTime,
                    Interval = BarIntervalParser.Format(decision.SignalInterval ?? agent.TriggerInterval),
                    Action = decision.Action.ToString(),
                    ReferencePrice = decision.ReferencePrice,
                    StopLossPrice = decision.StopLossPrice,
                    Reason = decision.Reason
                }, cancellationToken);
            }

            (syntheticPosition, bool completedTrade) = ApplySyntheticExecution(syntheticPosition, decision, instrument);
            if (completedTrade)
                totalTrades++;
        }

        await writer.WriteAsync(new AgentDebugEvent
        {
            Type = AgentDebugEventType.Status,
            Time = request.RunEnd,
            Message = $"Run finished. {totalTrades} completed trade(s).",
            TotalTrades = totalTrades
        }, cancellationToken);
    }

    /// <summary>Minimal stand-in for the real execution pipeline: tracks one synthetic open
    /// position so the agent's close-then-flip logic (which reads context.Positions) behaves the
    /// same way it would against a real broker. No P&amp;L, fills, or costs modeled - this tool is
    /// for watching the agent's signal logic, not for performance numbers (use the walk-forward/
    /// backtest scripts for that).</summary>
    private static (BrokerPosition? Position, bool CompletedTrade) ApplySyntheticExecution(
        BrokerPosition? current, AgentDecision decision, InstrumentKey instrument)
    {
        switch (decision.Action)
        {
            case AgentAction.Buy or AgentAction.Sell:
                return (new BrokerPosition
                {
                    PositionId = Guid.NewGuid().ToString("N"),
                    Instrument = instrument,
                    Side = decision.Action == AgentAction.Buy ? OrderSide.Buy : OrderSide.Sell,
                    Quantity = decision.SuggestedQuantity ?? 0m
                }, false);
            case AgentAction.Close:
                return (null, current is not null);
            default:
                return (current, false);
        }
    }

    private static AgentDebugEvent BuildCandleEvent(BarInterval interval, AnalysisSnapshot snapshot) => new()
    {
        Type = AgentDebugEventType.Candle,
        Time = snapshot.LatestCandle.OpenTime,
        Interval = BarIntervalParser.Format(interval),
        IsBaseInterval = interval == BarInterval.Minutes(1),
        Open = snapshot.LatestCandle.Prices.Open,
        High = snapshot.LatestCandle.Prices.High,
        Low = snapshot.LatestCandle.Prices.Low,
        Close = snapshot.LatestCandle.Prices.Close,
        Rsi = snapshot.Indicators.Rsi,
        BollingerUpper = snapshot.Indicators.BollingerUpper,
        BollingerMiddle = snapshot.Indicators.BollingerMiddle,
        BollingerLower = snapshot.Indicators.BollingerLower,
        StochRsiFast = snapshot.Indicators.StochRsi.Fast,
        StochRsiSlow = snapshot.Indicators.StochRsi.Slow,
        Atr = snapshot.Indicators.Atr
    };

    /// <summary>Display-only mirror of DivergenceReversalAgent.ClassifyExtreme + the fresh-pivot
    /// StochRSI-fast divergence check, kept intentionally separate/read-only so it can never
    /// influence the real decision path.</summary>
    private static AgentDebugEvent? BuildDiagnosticEvent(
        AnalysisSnapshot snapshot,
        DivergenceReversalStrategyOptions options,
        StochRsiAnalysisState stochRsiState,
        Dictionary<BarInterval, DateTimeOffset?> lastProcessed,
        BarInterval interval,
        string role)
    {
        IndicatorSnapshot indicators = snapshot.Indicators;
        if (indicators.BollingerUpper is not decimal upper ||
            indicators.BollingerLower is not decimal lower ||
            indicators.Rsi is not decimal rsi ||
            indicators.StochRsi.Fast is not decimal stochRsiFast)
        {
            return null;
        }

        Candle candle = snapshot.LatestCandle;
        decimal close = candle.Prices.Close;
        string certainty = "None";
        string? direction = null;

        if (close >= upper && stochRsiFast >= options.StochRsiFastOverbought && rsi >= options.RsiOverbought)
        {
            certainty = "Full";
            direction = "Bearish";
        }
        else if (close <= lower && stochRsiFast <= options.StochRsiFastOversold && rsi <= options.RsiOversold)
        {
            certainty = "Full";
            direction = "Bullish";
        }
        else if (candle.Prices.High >= upper || stochRsiFast >= options.PartialStochRsiFastOverbought)
        {
            certainty = "Partial";
            direction = "Bearish";
        }
        else if (candle.Prices.Low <= lower || stochRsiFast <= options.PartialStochRsiFastOversold)
        {
            certainty = "Partial";
            direction = "Bullish";
        }

        DateTimeOffset? watermark = lastProcessed[interval];
        IReadOnlyList<SwingPoint> newSwings = watermark is { } mark
            ? snapshot.Swings.Where(swing => swing.ConfirmedAt > mark).ToArray()
            : snapshot.Swings;
        if (newSwings.Count > 0)
            lastProcessed[interval] = newSwings.Max(swing => swing.ConfirmedAt);

        StochRsiAnalysisSnapshot relationship = stochRsiState.Update(candle, stochRsiFast, newSwings, indicators.Atr);

        return new AgentDebugEvent
        {
            Type = AgentDebugEventType.Diagnostic,
            Time = candle.OpenTime,
            Interval = BarIntervalParser.Format(interval),
            Role = role,
            Certainty = certainty,
            Direction = direction,
            RelationshipType = relationship.LatestRelationship?.Type.ToString(),
            RelationshipStrength = relationship.LatestRelationship?.Strength,
            IsNewRelationship = relationship.IsNewRelationship
        };
    }

    private static string? ResolveCacheFile(
        string cacheDirectory, string instrument, string interval, DateTimeOffset from, DateTimeOffset to)
    {
        if (!Directory.Exists(cacheDirectory))
            return null;

        string? best = null;
        TimeSpan bestSpan = TimeSpan.Zero;
        foreach (string manifestPath in Directory.EnumerateFiles(cacheDirectory, "*.manifest.json"))
        {
            CachedRangeManifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<CachedRangeManifest>(File.ReadAllText(manifestPath), ManifestJsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (manifest is null ||
                !string.Equals(manifest.Instrument, instrument, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(manifest.Interval, interval, StringComparison.OrdinalIgnoreCase) ||
                manifest.From > from || manifest.To < to)
            {
                continue;
            }

            TimeSpan span = manifest.To - manifest.From;
            if (best is null || span > bestSpan)
            {
                best = manifestPath[..^".manifest.json".Length];
                bestSpan = span;
            }
        }

        return best;
    }

    public static IReadOnlyList<AgentDebugInstrumentInfo> ListCachedInstruments(string cacheDirectory)
    {
        if (!Directory.Exists(cacheDirectory))
            return [];

        var byInstrument = new Dictionary<string, (DateTimeOffset From, DateTimeOffset To)>();
        foreach (string manifestPath in Directory.EnumerateFiles(cacheDirectory, "*.manifest.json"))
        {
            CachedRangeManifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<CachedRangeManifest>(File.ReadAllText(manifestPath), ManifestJsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (manifest is null || !string.Equals(manifest.Interval, "1m", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!byInstrument.TryGetValue(manifest.Instrument, out (DateTimeOffset From, DateTimeOffset To) range) ||
                manifest.To - manifest.From > range.To - range.From)
            {
                byInstrument[manifest.Instrument] = (manifest.From, manifest.To);
            }
        }

        return byInstrument
            .Select(pair => new AgentDebugInstrumentInfo(pair.Key, pair.Value.From, pair.Value.To))
            .OrderBy(item => item.Instrument, StringComparer.Ordinal)
            .ToArray();
    }

    private sealed record CachedRangeManifest(
        string Instrument, string Interval, DateTimeOffset From, DateTimeOffset To);
}
