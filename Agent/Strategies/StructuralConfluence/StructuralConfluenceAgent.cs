using Agent.Abstractions;
using Agent.Configuration;
using Agent.Models;
using Agent.Strategies.StructuralConfluence.Evidence;
using Agent.Strategies.StructuralConfluence.Playbooks;
using Brokers.Models;
using ChartAnnotator.Models;
using ChartAnnotator.TargetManagement;

namespace Agent.Strategies.StructuralConfluence;

public sealed class StructuralConfluenceAgent : ITradingAgent
{
    private readonly StructuralConfluenceStrategyOptions _options;
    private readonly StructuralEvidencePacketFactory _evidenceFactory = new();
    private readonly PlaybookStateStore _stateStore = new();
    private readonly IReadOnlyList<IStructuralPlaybook> _playbooks;
    private readonly StructuralCandidateArbitrator _arbitrator;
    private readonly Dictionary<InstrumentKey, EvaluationEpoch> _epochs = [];
    private readonly object _gate = new();

    public StructuralConfluenceAgent(StructuralConfluenceStrategyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        var playbooks = new List<IStructuralPlaybook>();
        if (options.LiquidityBreakRetest.Enabled)
            playbooks.Add(new LiquidityBreakRetestPlaybook(options));
        if (options.LiquiditySweepReversal.Enabled)
            playbooks.Add(new LiquiditySweepReversalPlaybook(options));
        if (options.SupplyDemandPullback.Enabled)
            playbooks.Add(new SupplyDemandPullbackPlaybook(options));
        if (options.IndicatorConfluence.Enabled)
            playbooks.Add(new IndicatorConfluencePlaybook(options));
        _playbooks = playbooks.OrderBy(item => item.PlaybookId, StringComparer.Ordinal).ToArray();
        _arbitrator = new StructuralCandidateArbitrator(options.Arbitration);
        RequiredIntervals = options.RequiredIntervals;
    }

    public string Name => "Structural Confluence";
    public IReadOnlySet<BarInterval> RequiredIntervals { get; }
    public BarInterval TriggerInterval => _options.TriggerInterval;

    /// <summary>
    /// A structural-confluence-v2 profile (<see cref="AdaptiveTargetManagementOptions.Enabled"/>)
    /// can produce managed (non-bracket) decisions, so the whole agent runs under
    /// <see cref="AgentExitManagementMode.ProtectiveStopAndStrategyExit"/> instead - required so
    /// pre-trade risk stops demanding a take-profit on every entry (plan §4.3) and, once wired,
    /// so the trade manager is required rather than optional. FixedStructuralTarget decisions
    /// still carry a real <see cref="AgentDecision.TakeProfitPrice"/> and are still submitted as
    /// a genuine broker bracket order - this mode only changes pre-trade admission, not whether
    /// an individual decision has a target order.
    /// </summary>
    public AgentExitManagementMode ExitManagementMode => _options.AdaptiveTargetManagement.Enabled
        ? AgentExitManagementMode.ProtectiveStopAndStrategyExit
        : AgentExitManagementMode.Bracket;

    public Task<AgentDecision> EvaluateAsync(
        AgentMarketContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Instrument.IsEmpty || context.Analysis.Instrument != context.Instrument)
            throw new ArgumentException("The structural context instrument is invalid or inconsistent.", nameof(context));

        long snapshotVersion = GetSnapshotVersion(context);
        lock (_gate)
        {
            if (_epochs.TryGetValue(context.Instrument, out EvaluationEpoch? previous))
            {
                if (context.Timestamp == previous.AvailableAt && snapshotVersion == previous.SnapshotVersion)
                    return Task.FromResult(previous.Decision);
                if (context.Timestamp < previous.AvailableAt ||
                    context.Timestamp == previous.AvailableAt && snapshotVersion < previous.SnapshotVersion)
                    return Task.FromResult(Observe(context, "StructuralStaleEvaluationEpoch", snapshotVersion));
            }

            string effectiveStrategyId = context.StrategyId ?? TradingAgentTypeIds.StructuralConfluence;
            if (context.Positions.Any(position => position.Instrument == context.Instrument && position.Quantity > 0m &&
                    string.Equals(position.StrategyId, effectiveStrategyId, StringComparison.OrdinalIgnoreCase)))
            {
                AgentDecision existingPosition = Observe(context, "StructuralPositionAlreadyOpen", snapshotVersion);
                Remember(context.Instrument, context.Timestamp, snapshotVersion, existingPosition);
                return Task.FromResult(existingPosition);
            }

            if (!_evidenceFactory.TryCreate(context, _options, out StructuralEvidencePacket? evidence, out string reasonCode))
            {
                AgentDecision notReady = Observe(context, reasonCode, snapshotVersion);
                Remember(context.Instrument, context.Timestamp, snapshotVersion, notReady);
                return Task.FromResult(notReady);
            }

            var evaluations = new List<PlaybookEvaluation>(_playbooks.Count);
            foreach (IStructuralPlaybook playbook in _playbooks)
            {
                PlaybookRuntimeState state = _stateStore.Get(context.Instrument, playbook.PlaybookId);
                PlaybookEvaluation evaluation = playbook.Evaluate(evidence!, state);
                _stateStore.TryAdvance(context.Instrument, playbook.PlaybookId, evidence!.AvailableAt,
                    snapshotVersion, evaluation);
                evaluations.Add(evaluation);
            }

            StructuralArbitrationResult arbitration = _arbitrator.Select(evaluations);
            AgentDecision decision = arbitration.Selected is { } selected
                ? Trade(context, evidence!, selected, snapshotVersion, effectiveStrategyId)
                : Observe(context, arbitration.ReasonCode, snapshotVersion, MostRelevant(evaluations));
            Remember(context.Instrument, context.Timestamp, snapshotVersion, decision);
            return Task.FromResult(decision);
        }
    }

    private AgentDecision Trade(
        AgentMarketContext context,
        StructuralEvidencePacket evidence,
        PlaybookEvaluation candidate,
        long snapshotVersion,
        string strategyId)
    {
        StructuralGeometry geometry = candidate.Geometry!;
        string setupId = candidate.SetupId!;
        string decisionId = StructuralIdentity.Decision(setupId, evidence.AvailableAt, snapshotVersion);
        bool buy = candidate.Direction == PriceActionDirection.Bullish;
        // Geometry distances in ATR use setup ATR (same units as stop buffer / risk).
        decimal? atr = evidence.Setup.Indicators.Atr is > 0m
            ? evidence.Setup.Indicators.Atr
            : evidence.Indicators.Atr is > 0m ? evidence.Indicators.Atr : null;
        IReadOnlyList<string> reasons = candidate.MandatoryGates.Select(item => item.ReasonCode)
            .Concat(candidate.SupportingEvidence).Distinct(StringComparer.Ordinal).ToArray();

        return new AgentDecision
        {
            DecisionId = decisionId,
            ClientOrderId = decisionId,
            SetupId = setupId,
            StrategyName = Name,
            StrategyId = strategyId,
            SetupStartedAt = candidate.CatalystAt,
            ConfirmationAt = candidate.TriggerEvent?.ConfirmedAt ?? candidate.TriggerSetup?.TriggeredAt,
            SignalInterval = TriggerInterval,
            Action = buy ? AgentAction.Buy : AgentAction.Sell,
            Instrument = context.Instrument,
            SuggestedQuantity = _options.Quantity,
            QuantityUnit = QuantityUnit.Units,
            OrderType = StandardOrderType.Market,
            ReferencePrice = geometry.Entry,
            StopLossPrice = geometry.Stop,
            // A managed policy never submits a hard broker target (plan §4.3); legacy v1
            // decisions (ExitPolicy null) and FixedStructuralTarget keep the bracket TP.
            TakeProfitPrice = geometry.ExitPolicy is null or TradeExitPolicy.FixedStructuralTarget ? geometry.Target : null,
            StopSource = geometry.StopSource,
            TargetSource = geometry.TargetSource,
            ExpectedRewardRisk = geometry.RewardRisk,
            ExitPolicy = geometry.ExitPolicy,
            TargetPlan = geometry.TargetPlan,
            Confidence = candidate.Confidence,
            CreatedAt = context.Timestamp,
            Reason = candidate.ReasonCode,
            ReasonCode = candidate.ReasonCode,
            PriceActionTrigger = candidate.TriggerEvent?.Type,
            PriceActionConfidence = candidate.TriggerEvent?.Confidence ?? candidate.TriggerSetup?.Confidence,
            PriceActionSetupType = candidate.TriggerSetup?.Type,
            PriceActionSetupId = candidate.TriggerSetup?.SetupId,
            PriceActionSetupReferenceLevel = candidate.TriggerSetup?.ReferenceLevel,
            PlaybookId = candidate.PlaybookId,
            PlaybookVersion = candidate.Version,
            StructuralSetupId = setupId,
            StructuralLifecycle = candidate.Lifecycle,
            ContextQuality = candidate.ContextQuality,
            LocationQuality = candidate.LocationQuality,
            CatalystQuality = candidate.CatalystQuality,
            TriggerQuality = candidate.TriggerQuality,
            ConfirmationQuality = candidate.ConfirmationQuality,
            GeometryQuality = candidate.GeometryQuality,
            CciConfirmationState = candidate.CciConfirmationState,
            EntryCci = evidence.Indicators.Cci,
            EntryCciMomentumChange = evidence.Indicators.CciAnalysis.MomentumChange,
            EntryCciRelationship = evidence.Indicators.CciAnalysis.LatestRelationship?.Type.ToString(),
            SweepPenetrationAtr = candidate.Sweep?.PenetrationAtr,
            ReclaimStrength = candidate.Sweep?.RejectionStrength,
            LiquidityPoolTouchCount = candidate.Pool?.TouchCount,
            SupplyDemandZoneTouchCount = candidate.Zone?.TouchCount,
            SupplyDemandPenetrationRatio = candidate.Zone?.PenetrationRatio,
            SupplyDemandLiquidityConfluence = candidate.Zone is not null && candidate.Pool is not null,
            DistanceToNearestTargetAtr = atr is null || geometry.Target is null
                ? null
                : Math.Abs(geometry.Target.Value - geometry.Entry) / atr.Value,
            DistanceToInvalidationAtr = atr is null || geometry.Stop is null
                ? null
                : Math.Abs(geometry.Entry - geometry.Stop.Value) / atr.Value,
            StructuralSetupReasonCodes = reasons,
            SupplyDemandZoneId = candidate.Zone?.ZoneId,
            SupplyDemandZoneType = candidate.Zone?.Type,
            SupplyDemandZoneState = candidate.Zone?.State,
            SupplyDemandZoneQuality = candidate.Zone?.QualityScore,
            SupplyDemandProfileHash = candidate.Zone?.ProfileHash,
            SupplyDemandReasonCodes = candidate.Zone is null ? [] : ["StructuralEntryZone"],
            LiquidityPoolId = candidate.Pool?.PoolId,
            LiquidityPoolType = candidate.Pool?.Type,
            LiquidityPoolState = candidate.Pool?.State,
            LiquiditySide = candidate.Pool?.Side,
            LiquidityPoolQuality = candidate.Pool?.QualityScore,
            LiquiditySweepId = candidate.Sweep?.SweepId,
            LiquidityProfileHash = candidate.Pool?.ProfileHash,
            LiquidityReasonCodes = candidate.Pool is null ? [] : ["StructuralCatalystPool"],
            // Always full risk (1m), not computed like ProgressiveStrategyBase's dynamic
            // StructuralEvidenceEvaluator-derived value: that field exists so a NON-structural-aware
            // strategy (Progressive's core signal is trend/momentum, not S&D/liquidity) can apply
            // structural evidence as a secondary risk-scaling overlay. This agent's own Confidence
            // and mandatory gates already fully incorporate that same evidence into the decision -
            // scaling risk by it again here would double-count it.
            StructuralEvidenceRiskMultiplier = 1m,
            EntrySupplyDemandZoneId = candidate.Zone?.ZoneId,
            EntrySupplyDemandZoneLowerPrice = candidate.Zone is null ? null : Math.Min(candidate.Zone.ProximalPrice, candidate.Zone.DistalPrice),
            EntrySupplyDemandZoneUpperPrice = candidate.Zone is null ? null : Math.Max(candidate.Zone.ProximalPrice, candidate.Zone.DistalPrice),
            EntrySupplyDemandZoneState = candidate.Zone?.State,
            EntrySupplyDemandProfileHash = candidate.Zone?.ProfileHash,
            OriginatingLiquidityPoolId = candidate.Pool?.PoolId,
            OriginatingLiquiditySweepId = candidate.Sweep?.SweepId,
            OriginatingLiquidityProfileHash = candidate.Pool?.ProfileHash,
            TargetLiquidityPoolId = geometry.TargetPool?.PoolId,
            TargetLiquidityProfileHash = geometry.TargetPool?.ProfileHash,
            StructuralInvalidationReference = geometry.Stop,
            EntrySupplyDemandManagementEnabled = candidate.Zone is not null,
            EntryLiquidityManagementEnabled = candidate.Pool is not null,
            StructuralManagementPolicyRevision = _options.StrategyVersion
        };
    }

    private AgentDecision Observe(
        AgentMarketContext context,
        string reasonCode,
        long snapshotVersion,
        PlaybookEvaluation? evaluation = null)
    {
        string seed = evaluation?.SetupId ?? $"{context.Instrument.Value}|{reasonCode}";
        return new AgentDecision
        {
            DecisionId = StructuralIdentity.Decision(seed, context.Timestamp, snapshotVersion),
            StrategyName = Name,
            StrategyId = context.StrategyId ?? TradingAgentTypeIds.StructuralConfluence,
            Action = AgentAction.Observe,
            Instrument = context.Instrument,
            Confidence = 0m,
            CreatedAt = context.Timestamp,
            Reason = reasonCode,
            ReasonCode = reasonCode,
            PlaybookId = evaluation?.PlaybookId,
            PlaybookVersion = evaluation?.Version,
            StructuralSetupId = evaluation?.SetupId,
            StructuralLifecycle = evaluation?.Lifecycle,
            ContextQuality = evaluation?.ContextQuality,
            LocationQuality = evaluation?.LocationQuality,
            CatalystQuality = evaluation?.CatalystQuality,
            TriggerQuality = evaluation?.TriggerQuality,
            ConfirmationQuality = evaluation?.ConfirmationQuality,
            GeometryQuality = evaluation?.GeometryQuality,
            CciConfirmationState = evaluation?.CciConfirmationState ?? "Unavailable",
            StructuralSetupReasonCodes = evaluation?.MandatoryGates.Select(item => item.ReasonCode).ToArray() ?? [reasonCode]
        };
    }

    private long GetSnapshotVersion(AgentMarketContext context) => RequiredIntervals
        .Select(interval => context.Analysis.TryGet(interval, out AnalysisSnapshot snapshot) ? snapshot.Version : -1L)
        .DefaultIfEmpty(-1L)
        .Max();

    private static PlaybookEvaluation? MostRelevant(IReadOnlyList<PlaybookEvaluation> evaluations) => evaluations
        .OrderByDescending(item => item.Lifecycle)
        .ThenByDescending(item => item.CatalystAt)
        .ThenBy(item => item.PlaybookId, StringComparer.Ordinal)
        .FirstOrDefault();

    private void Remember(InstrumentKey instrument, DateTimeOffset availableAt, long version, AgentDecision decision) =>
        _epochs[instrument] = new EvaluationEpoch(availableAt, version, decision);

    private sealed record EvaluationEpoch(DateTimeOffset AvailableAt, long SnapshotVersion, AgentDecision Decision);
}
