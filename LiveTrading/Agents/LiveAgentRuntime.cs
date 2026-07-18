using Agent.Models;
using Brokers.Abstractions;
using Brokers.Models;
using LiveTrading.Shadow;
using TradingCore.Pipeline;

namespace LiveTrading.Agents;

/// <summary>Live-specific result retained inside the environment-neutral runtime envelope.</summary>
public sealed record LiveAgentEvaluation
{
    public required AgentMarketContext Context { get; init; }
    public required TradingPipelineResult PipelineResult { get; init; }
}

/// <summary>
/// One isolated, serialized live Agent runtime. Extracted from the pipeline-evaluation body
/// previously inlined in <c>AgentSupervisor.EvaluateAsync</c> (broker snapshot fetch + one
/// <c>SafeTradingPipeline.ProcessAsync</c> call) - diagnostics bookkeeping
/// (<c>AgentInstanceState</c> mutation) and candidate submission
/// (<c>LiveDecisionEpochCoordinator.SubmitCandidate</c>) stay in <c>AgentSupervisor</c>, which
/// consumes this runtime's <see cref="AgentRuntimeResult"/> - those are orchestration concerns
/// spanning every registered runtime, not something one isolated runtime should own. A per-runtime
/// <see cref="SemaphoreSlim"/> guarantees this runtime's own pipeline state never observes two
/// concurrent evaluations, so many different runtimes' <c>EvaluateAsync</c> calls can be
/// dispatched concurrently (Phase 6) while each individual runtime stays serialized - exactly the
/// live host's existing "one call per closed candle" cadence, just no longer forcing every other
/// runtime to wait for it.
/// </summary>
public sealed class LiveAgentRuntime : IAgentRuntime, IDisposable
{
    private readonly IBrokerClient _broker;
    private readonly SafeTradingPipeline _pipeline;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _lastCompletedSnapshotVersion;
    private DateTimeOffset? _lastCompletedAvailableAt;

    public LiveAgentRuntime(
        AgentInstanceKey key,
        AnalysisProfileKey analysisProfile,
        AgentExecutionMode mode,
        SafeTradingPipeline pipeline,
        IBrokerClient broker)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        AnalysisProfile = analysisProfile ?? throw new ArgumentNullException(nameof(analysisProfile));
        Mode = mode;
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
    }

    public AgentInstanceKey Key { get; }
    public AnalysisProfileKey AnalysisProfile { get; }
    public AgentExecutionMode Mode { get; }

    public Task<AgentRuntimeResult> EvaluateAsync(
        MarketAnalysisSnapshot snapshot,
        CancellationToken cancellationToken = default) =>
        EvaluateAsync(snapshot, executableSpread: null, cancellationToken);

    /// <summary>The quote-derived spread is an input to this evaluation, not mutable runtime
    /// state. Passing it through the serialized call prevents a queued epoch from overwriting the
    /// value used by the epoch currently holding the runtime gate.</summary>
    public async Task<AgentRuntimeResult> EvaluateAsync(
        MarketAnalysisSnapshot snapshot,
        decimal? executableSpread,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (snapshot.Instrument != Key.Instrument)
            {
                throw new InvalidOperationException(
                    $"Snapshot instrument {snapshot.Instrument} does not match runtime instrument {Key.Instrument}.");
            }
            if (snapshot.Profile != AnalysisProfile)
            {
                throw new InvalidOperationException(
                    $"Snapshot profile {snapshot.Profile.ProfileHash} does not match runtime profile {AnalysisProfile.ProfileHash}.");
            }
            if (snapshot.SnapshotVersion < _lastCompletedSnapshotVersion ||
                snapshot.SnapshotVersion == _lastCompletedSnapshotVersion &&
                _lastCompletedAvailableAt is { } lastAvailableAt && snapshot.AvailableAt != lastAvailableAt)
            {
                throw new InvalidOperationException(
                    $"Stale or conflicting snapshot {snapshot.SnapshotVersion}/{snapshot.AvailableAt:O}; " +
                    $"runtime completed {_lastCompletedSnapshotVersion}/{_lastCompletedAvailableAt:O}.");
            }

            AccountSnapshot account = (await _broker.Accounts.GetAccountsAsync(cancellationToken)
                .ConfigureAwait(false)).Single();
            IReadOnlyList<BrokerPosition> positions = await _broker.Positions
                .GetOpenPositionsAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<BrokerOrder> openOrders = await _broker.Orders
                .GetOpenOrdersAsync(snapshot.Instrument, cancellationToken).ConfigureAwait(false);

            var analysis = new MultiTimeframeAnalysis(snapshot.Instrument, snapshot.AvailableAt, snapshot.Timeframes);
            var context = new AgentMarketContext
            {
                Instrument = snapshot.Instrument,
                Timestamp = snapshot.AvailableAt,
                Analysis = analysis,
                Account = account,
                Positions = positions,
                OpenOrders = openOrders,
                ExecutableSpread = executableSpread,
                MarketDataAvailableAt = snapshot.AvailableAt,
                StrategyId = Key.StrategyId,
                CurrencyStrength = snapshot.CrossMarket
            };

            var shadowBroker = new ShadowBrokerClient(_broker);
            TradingPipelineResult result = await _pipeline
                .ProcessAsync(context, shadowBroker, cancellationToken)
                .ConfigureAwait(false);

            _lastCompletedSnapshotVersion = snapshot.SnapshotVersion;
            _lastCompletedAvailableAt = snapshot.AvailableAt;

            return new AgentRuntimeResult
            {
                Key = Key,
                EvaluatedAt = snapshot.AvailableAt,
                SnapshotVersion = snapshot.SnapshotVersion,
                Decision = result.Decision,
                Native = new LiveAgentEvaluation
                {
                    Context = context,
                    PipelineResult = result
                }
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new AgentRuntimeResult
            {
                Key = Key,
                EvaluatedAt = snapshot.AvailableAt,
                SnapshotVersion = snapshot.SnapshotVersion,
                Failure = exception,
                Native = exception
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
