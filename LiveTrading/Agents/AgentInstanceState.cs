using LiveTrading.Configuration;
using Agent.Models;
using TradingCore.Pipeline;

namespace LiveTrading.Agents;

/// <summary>Per-agent-instance mutable state: identity, its constructed runtime, and running
/// shadow-outcome counters plus the last candidate produced - the "counters + last candidate"
/// granularity confirmed with the user (no candidate-outcome replay/ring-buffer this phase).
/// There is deliberately no mutable broker-position ownership field: even after execution is
/// enabled, the account-wide registry remains the sole authority for strategy-owned orders and
/// broker trades.</summary>
public sealed class AgentInstanceState
{
    public required AgentInstanceKey Key { get; init; }
    public required StrategyDecisionRuntime Runtime { get; init; }
    public required LiveTradingPolicyBundle PolicyBundle { get; init; }
    public required LiveStrategyAssignment Assignment { get; init; }
    /// <summary>The exact immutable analysis configuration pinned at startup.</summary>
    public AnalysisProfileKey? AnalysisProfile { get; init; }
    /// <summary>Production runtime boundary. Null is retained only for legacy/test construction.</summary>
    public IAgentRuntime? IsolatedRuntime { get; init; }

    public long LastEvaluatedMarketSequence { get; set; }
    public DateTimeOffset? LastEvaluatedAt { get; set; }
    public long LastEvaluatedEpoch { get; set; }
    public long LastSnapshotVersion { get; set; }
    public int MailboxDepth;
    public int PeakMailboxDepth;

    /// <summary>AGENT-10: total pipeline evaluations, regardless of outcome - the missing
    /// "Evaluations" counter the diagnostics audit flagged as absent.</summary>
    public long Evaluations { get; set; }
    public long CandidatesObserved { get; set; }
    public long CandidatesBuy { get; set; }
    public long CandidatesSell { get; set; }
    public long CandidatesRejectedByMetaLabel { get; set; }
    public long CandidatesRejectedBySetupCalibration { get; set; }
    public long CandidatesRejectedByTradingCondition { get; set; }
    /// <summary>AGENT-10: TradingPipelineStatus.RejectedByDataQuality - the "missing data"
    /// category the diagnostics audit flagged as absent.</summary>
    public long CandidatesRejectedByDataQuality { get; set; }
    public long CandidatesRejectedBySafety { get; set; }
    /// <summary>
    /// AGENT-10: generic breakdown of every Observe decision's AgentDecision.ReasonCode (e.g.
    /// "PrimaryTrendNotReady", "SetupConsensusNotReady", "ConfirmationConsensusNotReady",
    /// "OpposingPriceAction", "RegimeBlocked:*", "InvalidStopSide", "ZeroRisk", ...). Deliberately
    /// keyed by the Agent's own reason code string rather than a hand-maintained enum, so newly
    /// added reason codes are automatically tracked without a matching diagnostics-side change -
    /// this is the "Trend/Setup/Confirmation/Price-action/Regime/Invalid-stop-target rejections"
    /// breakdown the diagnostics audit found only existed as scattered per-decision strings, not
    /// as aggregated counts. Purely additive bookkeeping - never read by decision logic.
    /// </summary>
    public Dictionary<string, long> ObserveReasonCounts { get; } = [];
    /// <summary>AGENT-10: a Buy/Sell candidate whose DecisionId exactly matches the immediately
    /// preceding one - the "duplicate decisions" category the diagnostics audit flagged as
    /// absent. DecisionId is a deterministic function of ordered inputs (see ProgressiveStrategyBase),
    /// so an exact repeat is a genuine duplicate emission, not a coincidence.</summary>
    public long DuplicateDecisions { get; set; }
    public LiveTradeCandidate? LastCandidate { get; set; }
    public string? LastStatus { get; set; }
    public string? LastRejection { get; set; }

    /// <summary>Phase 4: the most recent evaluation's decision identity, recorded independently
    /// of <see cref="LastCandidate"/> so a Phase 6 per-agent timeout can still record which
    /// decision was in flight even when no candidate was ultimately produced (candidate stays
    /// null on Observe/reject outcomes and on timeout). Matches <c>LiveTradeCandidate.DecisionId</c>'s
    /// <c>string</c> type, not a synthesized <c>Guid</c> - this codebase's decision identity is
    /// always a deterministic string derived from ordered inputs (see <c>ProgressiveStrategyBase</c>).</summary>
    public string? LastDecisionId { get; set; }
    public AgentAction? LastAction { get; set; }
    public decimal? LastConfidence { get; set; }
    public string? LastReasonCode { get; set; }

    /// <summary>Multi-agent architecture Phase 6: incremented each time this instance's
    /// evaluation does not complete within <c>AgentSupervisor</c>'s per-agent timeout. The
    /// evaluation is cancelled cooperatively and its instance gate stays held until the task has
    /// actually stopped; a late synchronous return is rejected before it can mutate diagnostics
    /// or emit a candidate.</summary>
    public long TimeoutCount { get; set; }

    public Exception? LastEvaluationError { get; set; }
    public DateTimeOffset? LastEvaluationErrorAt { get; set; }

    private readonly object _durationSync = new();
    private readonly Queue<double> _recentEvaluationMilliseconds = new();

    public void RecordEvaluationDuration(TimeSpan duration)
    {
        lock (_durationSync)
        {
            _recentEvaluationMilliseconds.Enqueue(duration.TotalMilliseconds);
            while (_recentEvaluationMilliseconds.Count > 256)
                _recentEvaluationMilliseconds.Dequeue();
        }
    }

    public (double Average, double P95) EvaluationDurationSummary()
    {
        lock (_durationSync)
        {
            if (_recentEvaluationMilliseconds.Count == 0)
                return (0, 0);
            double[] ordered = _recentEvaluationMilliseconds.Order().ToArray();
            int p95Index = Math.Clamp((int)Math.Ceiling(ordered.Length * 0.95) - 1, 0, ordered.Length - 1);
            return (ordered.Average(), ordered[p95Index]);
        }
    }
}
