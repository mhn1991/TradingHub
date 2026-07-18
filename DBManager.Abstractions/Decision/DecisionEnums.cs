namespace DBManager.Abstractions.Decision;

/// <summary>Coarse classification of what an Agent evaluation produced (section 7.4).</summary>
public enum AgentEvaluationAction
{
    /// <summary>No setup/candidate state changed this evaluation (section 1.2's high-volume "Observe" case).</summary>
    Observe,

    /// <summary>The evaluation produced a <see cref="TradeCandidateRecord"/> (section 34's "Neutral candidate").</summary>
    CandidateEmitted
}

/// <summary>
/// Agent FSM state (section 7.4's <c>state_before</c>/<c>state_after</c>), mirroring the setup
/// lifecycle fields on <c>runtime.agent_state</c> (section 7.3: setup_id/setup_direction/...).
/// </summary>
public enum AgentState
{
    Idle,
    Watching,
    SetupForming,
    SetupActive,
    SetupInvalidated,
    CandidateEmitted
}

/// <summary>Which evaluation clock triggered this evaluation, mirroring section 7.7's fast/main/thesis intervals.</summary>
public enum TriggerInterval
{
    Fast,
    Main,
    Thesis
}

/// <summary>
/// Market regime classification. Values mirror <c>ChartAnnotator.Regime.MarketRegime</c> exactly
/// (a 1:1 ordinal mapping) without creating a compile-time dependency — section 4.1 keeps
/// <c>DBManager.Abstractions</c> dependency-free, so the domain-to-persistence mapping happens at
/// the call site instead.
/// </summary>
public enum Regime
{
    Unknown,
    TrendingUp,
    TrendingDown,
    Range,
    Compression,
    BreakoutExpansionUp,
    BreakoutExpansionDown,
    HighVolatilityDisorder,
    IlliquidUnsafe
}

public enum TradeDirection
{
    Long,
    Short
}

/// <summary>Overall candidate funnel status (section 13.2 references <see cref="AwaitingApproval"/>).</summary>
public enum CandidateStatus
{
    Open,
    AwaitingApproval,
    Approved,
    Rejected,
    Expired,
    Consumed
}

/// <summary>
/// A funnel stage a candidate passes through, mirroring the audit columns already on
/// <c>decision.trade_candidates</c> (setup_calibration_audit/meta_label_audit/trading_condition_audit)
/// plus the downstream risk/execution stages.
/// </summary>
public enum CandidateStage
{
    SetupCalibration,
    MetaLabel,
    TradingCondition,
    PositionSizing,
    PortfolioAdmission,
    ManualApproval,
    Submission
}

public enum StageOutcome
{
    Passed,
    Rejected,
    Deferred
}
