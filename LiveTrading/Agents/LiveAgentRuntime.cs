using Agent.Models;
using Brokers.Abstractions;
using Brokers.Models;
using LiveTrading.Shadow;
using TradingCore.Pipeline;

namespace LiveTrading.Agents;

/// <summary>
/// One isolated, serialized live Agent runtime. Extracted from the pipeline-evaluation body
/// previously inlined in <c>AgentSupervisor.EvaluateAsync</c> (broker snapshot fetch + one
/// <c>SafeTradingPipeline.ProcessAsync</c> call) - diagnostics bookkeeping
/// (<c>AgentInstanceState</c> mutation) and candidate submission
/// (<c>LiveDecisionEpochCoordinator.SubmitCandidate</c>) stay in <c>AgentSupervisor</c>, which
/// consumes this runtime's <see cref="AgentRuntimeResult"/> - those are orchestration concerns
/// spanning every registered runtime, not something one isolated runtime should own. A per-runtime
/// <see cref="SemaphoreSlim"/> guarantees this runtime's own pipeline state never observes two
/// concurrent evaluations, so many different runtimes' <see cref="EvaluateAsync"/> calls can be
/// dispatched concurrently (Phase 6) while each individual runtime stays serialized - exactly the
/// live host's existing "one call per closed candle" cadence, just no longer forcing every OTHER
/// runtime to wait for it.
/// </summary>
public sealed class LiveAgentRuntime : IAgentRuntime, IDisposable
{
    private readonly IBrokerClient _broker;
    private readonly SafeTradingPipeline _pipeline;
    private readonly SemaphoreSlim _gate = new(1, 1);

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

    /// <summary>Set by the caller before each <see cref="EvaluateAsync"/> call - the current
    /// executable spread is quote-driven and changes far more often than this runtime evaluates,
    /// so it cannot be captured once at construction. Not part of <see cref="MarketAnalysisSnapshot"/>
    /// itself, which carries only shared, environment-neutral analysis content.</summary>
    public decimal? ExecutableSpread { get; set; }

    public async Task<AgentRuntimeResult> EvaluateAsync(
        MarketAnalysisSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
                ExecutableSpread = ExecutableSpread,
                MarketDataAvailableAt = snapshot.AvailableAt,
                StrategyId = Key.StrategyId,
                CurrencyStrength = snapshot.CrossMarket
            };

            var shadowBroker = new ShadowBrokerClient(_broker);
            TradingPipelineResult result = await _pipeline
                .ProcessAsync(context, shadowBroker, cancellationToken)
                .ConfigureAwait(false);

            return new AgentRuntimeResult
            {
                Key = Key,
                EvaluatedAt = snapshot.AvailableAt,
                SnapshotVersion = snapshot.SnapshotVersion,
                Decision = result.Decision,
                Native = result
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
