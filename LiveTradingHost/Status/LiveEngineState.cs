using LiveTradingHost.Api;
using LiveTrading.Runtime;
using LiveTrading.ManualApproval;

namespace LiveTradingHost.Status;

/// <summary>
/// In-memory, thread-safe holder of the engine's current status - written by <see
/// cref="LiveEngineHostedService"/>, read by both the polling status endpoint and the SignalR
/// hub. Not persisted; a restart always starts from <c>Starting</c>.
/// </summary>
public sealed class LiveEngineState(TimeProvider timeProvider)
{
    private readonly object _sync = new();
    private readonly Dictionary<string, MarketStatusDto> _markets = [];
    private readonly Dictionary<string, AgentStatusDto> _agents = [];
    private readonly Dictionary<string, AnalysisProfileStatusDto> _analysisProfiles = [];
    private int _revision;
    private string _engineState = "Starting";
    private string? _message;
    private bool _quoteStreamConnected;
    private DateTimeOffset? _lastQuoteStreamFaultAt;
    private bool _restReachable;
    private string _leaseState = "Unknown";
    private LiveRuntimeStatusDto? _runtime;
    private DecisionEpochStatusDto? _lastDecisionEpoch;

    public void SetRunning()
    {
        lock (_sync)
        {
            _engineState = "Running";
            _message = null;
            _revision++;
        }
    }

    public void SetFaulted(string message)
    {
        lock (_sync)
        {
            _engineState = "Faulted";
            _message = message;
            _revision++;
        }
    }

    public void SetStopped()
    {
        lock (_sync)
        {
            _engineState = "Stopped";
            _revision++;
        }
    }

    public void SetLeaseState(string leaseState)
    {
        lock (_sync)
        {
            _leaseState = leaseState;
            _revision++;
        }
    }

    public void SetQuoteStreamConnected(bool connected)
    {
        lock (_sync)
        {
            _quoteStreamConnected = connected;
            if (!connected)
            {
                _lastQuoteStreamFaultAt = timeProvider.GetUtcNow();
            }

            _revision++;
        }
    }

    public void SetRestReachable(bool reachable)
    {
        lock (_sync)
        {
            _restReachable = reachable;
            _revision++;
        }
    }

    public void SetRuntime(LiveRuntimeSnapshot runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        lock (_sync)
        {
            _runtime = new LiveRuntimeStatusDto
            {
                BrokerWritesEnabled = runtime.BrokerWritesEnabled,
                AutomaticExecutionEnabled = runtime.AutomaticExecutionEnabled,
                PartialCloseEnabled = runtime.PartialCloseEnabled,
                DynamicStopReplacementEnabled = runtime.DynamicStopReplacementEnabled,
                CanOpenNewEntries = runtime.CanOpenNewEntries,
                SafetyState = runtime.Safety.State.ToString(),
                SafetyMessage = runtime.Safety.Message,
                Equity = runtime.Account?.Equity,
                MarginUsed = runtime.Account?.Account.MarginUsed,
                PendingManualCandidates = runtime.ManualCandidates.Count(candidate =>
                    candidate.State is ManualApprovalState.Pending or ManualApprovalState.Approved),
                ActiveReservations = runtime.Reservations.Reservations.Count,
                Orders = runtime.Registry.Orders.Count,
                Positions = runtime.Registry.Positions.Count,
                ReconciliationDifferences = runtime.LastReconciliation?.Differences.Count ?? 0,
                LastReconciledAt = runtime.LastReconciliation?.CompletedAt,
                Shadow = new LiveShadowStatusDto
                {
                    Enabled = runtime.Shadow.Enabled,
                    CandidateCount = runtime.Shadow.CandidateCount,
                    AdmittedCount = runtime.Shadow.AdmittedCount,
                    RejectedCount = runtime.Shadow.RejectedCount,
                    OpenPositionCount = runtime.Shadow.OpenPositionCount,
                    CompletedCount = runtime.Shadow.CompletedCount,
                    AmbiguousCount = runtime.Shadow.AmbiguousCount,
                    NetProfitLoss = runtime.Shadow.NetProfitLoss,
                    UnrealizedProfitLoss = runtime.Shadow.UnrealizedProfitLoss,
                    NetR = runtime.Shadow.NetR,
                    LastPlaybookId = runtime.Shadow.LastPlaybookId,
                    LastCandidateId = runtime.Shadow.LastCandidateId,
                    LastRejection = runtime.Shadow.LastRejection,
                    LastStopPrice = runtime.Shadow.LastStopPrice,
                    LastTargetPrice = runtime.Shadow.LastTargetPrice,
                    LastPolicyRevision = runtime.Shadow.LastPolicyRevision
                }
            };
            _revision++;
        }
    }

    public void UpdateMarket(MarketStatusDto market)
    {
        lock (_sync)
        {
            _markets[market.Instrument] = market;
            _revision++;
        }
    }

    public void UpdateAgent(AgentStatusDto agent)
    {
        lock (_sync)
        {
            _agents[$"{agent.DeploymentId}:{agent.Instrument}:{agent.StrategyId}:" +
                    $"{agent.PolicyBundleId:N}:{agent.PolicyRevision}"] = agent;
            _revision++;
        }
    }

    public void UpdateAnalysisProfile(AnalysisProfileStatusDto profile)
    {
        lock (_sync)
        {
            _analysisProfiles[$"{profile.Instrument}:{profile.ProfileHash}"] = profile;
            _revision++;
        }
    }

    public void UpdateDecisionEpoch(DecisionEpochStatusDto epoch)
    {
        lock (_sync)
        {
            _lastDecisionEpoch = epoch;
            _revision++;
        }
    }

    public LiveEngineStatusDto Snapshot()
    {
        lock (_sync)
        {
            return new LiveEngineStatusDto
            {
                Revision = _revision,
                EngineState = _engineState,
                Message = _message,
                Connections = new ConnectionsHealthDto
                {
                    QuoteStreamConnected = _quoteStreamConnected,
                    LastQuoteStreamFaultAt = _lastQuoteStreamFaultAt,
                    RestReachable = _restReachable,
                    LeaseState = _leaseState
                },
                Markets = _markets.Values.OrderBy(m => m.Instrument, StringComparer.Ordinal).ToList(),
                Agents = _agents.Values
                    .OrderBy(a => a.Instrument, StringComparer.Ordinal)
                    .ThenBy(a => a.StrategyId, StringComparer.Ordinal)
                    .ThenBy(a => a.PolicyBundleId)
                    .ThenBy(a => a.PolicyRevision)
                    .ToList(),
                AnalysisProfiles = _analysisProfiles.Values
                    .OrderBy(profile => profile.Instrument, StringComparer.Ordinal)
                    .ThenBy(profile => profile.ProfileHash, StringComparer.Ordinal)
                    .ToList(),
                LastDecisionEpoch = _lastDecisionEpoch,
                Runtime = _runtime,
                AsOf = timeProvider.GetUtcNow()
            };
        }
    }
}
