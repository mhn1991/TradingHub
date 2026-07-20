using Agent.Configuration;
using Agent.Models;
using Brokers.Models;
using ChartAnnotator.Models;
using ChartAnnotator.Regime;
using LiveTrading.Account;
using LiveTrading.Actors;
using LiveTrading.Execution;
using LiveTrading.Configuration;
using LiveTrading.MarketData;
using LiveTrading.Persistence;
using LiveTrading.Registry;
using LiveTrading.Runtime;
using Microsoft.Extensions.Logging;
using RiskManager.Safety;
using TradeManager;

namespace LiveTrading.Management;

public interface ILivePositionManagementService
{
    Task OnQuoteAsync(LiveQuoteSnapshot quote, CancellationToken cancellationToken);

    Task OnAnalysisAsync(
        MarketAnalysisUpdate update,
        LiveQuoteSnapshot? quote,
        CancellationToken cancellationToken);
}

/// <summary>
/// Creates one deterministic management state per strategy-owned broker trade. Quotes drive MFE,
/// MAE and mechanical protection; completed fast/main/thesis candles drive structural management.
/// Every mutation is delegated to the serialized execution gateway and then confirmed by the
/// transaction stream/REST reconciliation.
/// </summary>
public sealed class LivePositionManagementService(
    ILiveOrderPositionRegistry registry,
    ILiveExecutionGateway execution,
    ILiveAccountStateService accountState,
    ILivePolicyRegistry policyRegistry,
    LiveExecutionRuntimeOptions executionOptions,
    ITradingSafetyController safety,
    ILiveTradingPersistence persistence,
    TimeProvider timeProvider,
    ILogger<LivePositionManagementService> logger) : ILivePositionManagementService
{
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly Dictionary<string, PositionRuntimeState> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<InstrumentKey, MarketAnalysisUpdate> _latestAnalysis = [];

    public async Task OnQuoteAsync(
        LiveQuoteSnapshot quote,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(quote);
        MarketAnalysisUpdate? update;
        lock (_latestAnalysis)
        {
            _latestAnalysis.TryGetValue(quote.Instrument, out update);
        }
        if (update is null)
            return;

        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AnalysisSnapshot analysis = update.Analysis.Timeframes
                .OrderBy(item => BarIntervalParser.ApproximateSeconds(item.Key))
                .First().Value;
            foreach (LivePositionRecord position in registry.Snapshot.Positions.Where(position =>
                         position.Instrument == quote.Instrument))
            {
                LiveManagementPolicy management = policyRegistry.ResolveManagement(
                    position.StrategyId,
                    position.Instrument);
                PositionManagementOptions options = ResolveOptions(position.StrategyId, management.Policy);
                if (!options.EvaluateMechanicalProtectionOnEveryExecutionFrame)
                    continue;
                await EvaluateAsync(
                    position,
                    quote,
                    analysis,
                    TradeManagementEvaluationScope.Mechanical,
                    update.MarketSequence,
                    management,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _serial.Release();
        }
    }

    public async Task OnAnalysisAsync(
        MarketAnalysisUpdate update,
        LiveQuoteSnapshot? quote,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_latestAnalysis)
        {
            _latestAnalysis[update.Instrument] = update;
        }
        if (quote is null || quote.IsStale || !quote.IsTradeable)
            return;

        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (LivePositionRecord position in registry.Snapshot.Positions.Where(position =>
                         position.Instrument == update.Instrument))
            {
                LiveManagementPolicy management = policyRegistry.ResolveManagement(
                    position.StrategyId,
                    position.Instrument);
                PositionManagementOptions options = ResolveOptions(position.StrategyId, management.Policy);
                (TradeManagementEvaluationScope Scope, BarInterval? Interval) evaluation =
                    SelectEvaluation(update, options);
                if (evaluation.Interval is not BarInterval interval ||
                    !update.Analysis.TryGet(interval, out AnalysisSnapshot analysis))
                {
                    continue;
                }
                PositionRuntimeState runtime = StateFor(position, management, options);
                runtime.AnalysisBarsSinceLastAmendment = SaturatingIncrement(runtime.AnalysisBarsSinceLastAmendment);
                runtime.AnalysisBarsSinceLastReduction = SaturatingIncrement(runtime.AnalysisBarsSinceLastReduction);
                await EvaluateAsync(
                    position,
                    quote,
                    analysis,
                    evaluation.Scope,
                    update.MarketSequence,
                    management,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _serial.Release();
        }
    }

    private async Task EvaluateAsync(
        LivePositionRecord position,
        LiveQuoteSnapshot quote,
        AnalysisSnapshot analysis,
        TradeManagementEvaluationScope scope,
        long sequence,
        LiveManagementPolicy management,
        CancellationToken cancellationToken)
    {
        if (position.AveragePrice is not > 0m || position.InitialStopPrice is not > 0m ||
            position.ProtectiveStopPrice is not > 0m || position.Quantity <= 0m)
        {
            logger.LogCritical(
                "Owned position {PositionId} cannot be managed because entry/stop/quantity state is incomplete.",
                position.PositionId);
            safety.Pause(
                $"Owned position {position.PositionId} lacks entry/stop/quantity management state.",
                timeProvider.GetUtcNow());
            return;
        }

        LiveTradingPolicyBundle policy = management.Policy;
        if (!string.Equals(position.ConfigurationHash, policy.ConfigurationHash, StringComparison.Ordinal) ||
            position.PolicyBundleId != policy.PolicyBundleId || position.PolicyRevision != policy.Revision)
        {
            // The registry's "current" resolution for this strategy/instrument no longer matches
            // what this position was opened under - most likely a newer policy revision was
            // activated (LivePolicyRegistry.Activate) since entry, not a corruption. Keep managing
            // this position under its OWN original revision rather than pausing the whole account;
            // only fall back to a hard pause if that specific revision genuinely cannot be found.
            LiveManagementPolicy? ownRevision = policyRegistry.TryResolveManagementByRevision(
                position.StrategyId, position.Instrument, position.PolicyBundleId, position.PolicyRevision);
            if (ownRevision is null)
            {
                logger.LogCritical(
                    "Position {PositionId} was opened under policy {PolicyId}/{Revision}/{Hash} but runtime resolved {RuntimePolicyId}/{RuntimeRevision}/{RuntimeHash}, and that original revision is no longer resolvable.",
                    position.PositionId,
                    position.PolicyBundleId,
                    position.PolicyRevision,
                    position.ConfigurationHash,
                    policy.PolicyBundleId,
                    policy.Revision,
                    policy.ConfigurationHash);
                safety.Pause(
                    $"Policy mismatch for owned position {position.PositionId}; management requires operator review.",
                    timeProvider.GetUtcNow());
                return;
            }
            logger.LogInformation(
                "Position {PositionId} continues under its original policy {PolicyId}/{Revision} after a newer revision was activated for {StrategyId}/{Instrument}.",
                position.PositionId, position.PolicyBundleId, position.PolicyRevision, position.StrategyId, position.Instrument);
            management = ownRevision;
            policy = management.Policy;
        }

        PositionManagementOptions options = ResolveOptions(position.StrategyId, policy);
        PositionRuntimeState runtime = StateFor(position, management, options);
        decimal executablePrice = position.Side == OrderSide.Buy ? quote.Bid : quote.Ask;
        decimal initialRisk = Math.Abs(position.AveragePrice.Value - position.InitialStopPrice.Value);
        if (initialRisk <= 0m)
            return;
        decimal signedMove = position.Side == OrderSide.Buy
            ? executablePrice - position.AveragePrice.Value
            : position.AveragePrice.Value - executablePrice;
        decimal openR = signedMove / initialRisk;
        runtime.MaximumFavourableExcursionR = Math.Max(runtime.MaximumFavourableExcursionR, openR);
        runtime.MaximumAdverseExcursionR = Math.Max(runtime.MaximumAdverseExcursionR, -openR);
        if (openR >= runtime.LastObservedMfeR + options.StagnationMinimumMfeAdvanceR)
        {
            runtime.LastObservedMfeR = openR;
            runtime.AnalysisBarsWithoutNewMfe = 0;
        }
        else if (scope != TradeManagementEvaluationScope.Mechanical)
        {
            runtime.AnalysisBarsWithoutNewMfe = SaturatingIncrement(runtime.AnalysisBarsWithoutNewMfe);
        }

        MarketRegime entryRegime = Enum.TryParse(position.EntryRegime, out MarketRegime parsedRegime)
            ? parsedRegime
            : MarketRegime.Unknown;
        EquityProtectionDirective? equityDirective = TranslateEquityProtection(safety.Snapshot.EquityProtection);
        TradeManagementRecommendation recommendation = runtime.Manager.Evaluate(
            new ManagedTradeState
            {
                StrategyId = position.StrategyId,
                InstrumentGroup = InstrumentGroupResolver.Resolve(position.Instrument),
                SetupType = position.EntrySetupType,
                EntrySession = position.EntrySession,
                EntryVolatilityBucket = "Live",
                EntryConfidence = position.EntryConfidence,
                Instrument = position.Instrument,
                Side = position.Side,
                EntryPrice = position.AveragePrice.Value,
                InitialStopPrice = position.InitialStopPrice.Value,
                CurrentStopPrice = position.ProtectiveStopPrice.Value,
                CurrentPrice = executablePrice,
                TakeProfitPrice = position.TakeProfitPrice ?? 0m,
                InitialQuantity = position.InitialQuantity > 0m ? position.InitialQuantity : position.Quantity,
                CurrentQuantity = position.Quantity,
                MinimumQuantityIncrement = MinimumQuantityIncrement(position.Instrument),
                MaximumFavourableExcursionR = runtime.MaximumFavourableExcursionR,
                CompletedReductionStageIds = runtime.CompletedReductionStageIds,
                HasPendingReduction = runtime.PendingReduction,
                LastReductionSnapshotVersion = runtime.LastReductionSequence,
                AnalysisBarsSinceLastReduction = runtime.AnalysisBarsSinceLastReduction,
                AnalysisBarsWithoutNewMfe = runtime.AnalysisBarsWithoutNewMfe,
                StagnationReductionCompleted = runtime.StagnationReductionCompleted,
                StructuralDeteriorationReductionCount = runtime.StructuralDeteriorationReductionCount,
                MomentumDecayReductionCount = runtime.MomentumDecayReductionCount,
                VolatilityExhaustionReductionCount = runtime.VolatilityExhaustionReductionCount,
                RegimeDegradationReductionCount = runtime.RegimeDegradationReductionCount,
                VolatilityExpansionSeenSinceEntry = runtime.VolatilityExpansionSeenSinceEntry,
                RiskWindowReductionCompleted = runtime.RiskWindowReductionCompleted,
                ExecutionCostStressReductionCompleted = runtime.ExecutionCostStressReductionCompleted,
                EvaluatedAt = timeProvider.GetUtcNow(),
                MinimumPriceIncrement = MinimumPriceIncrement(position.Instrument),
                SpreadPrice = quote.Spread,
                LastAmendmentSnapshotVersion = runtime.LastAmendmentSequence,
                AnalysisBarsSinceLastAmendment = runtime.AnalysisBarsSinceLastAmendment,
                EntryRegime = entryRegime,
                EntryManagementProfileId = position.EntryManagementProfileId,
                EntryNeoWaveHypothesisId = position.EntryNeoWaveHypothesisId,
                EntryNeoWaveInvalidationPrice = position.EntryNeoWaveInvalidationPrice,
                EntrySupplyDemandZoneId = position.EntrySupplyDemandZoneId,
                TargetLiquidityPoolId = position.TargetLiquidityPoolId,
                EntrySupplyDemandManagementEnabled = position.EntrySupplyDemandManagementEnabled,
                EntryLiquidityManagementEnabled = position.EntryLiquidityManagementEnabled,
                StructuralManagementPolicyRevision = position.StructuralManagementPolicyRevision
            },
            analysis,
            scope,
            equityDirective);

        registry.ApplyManagementObservation(
            position.PositionId,
            runtime.MaximumFavourableExcursionR,
            runtime.MaximumAdverseExcursionR,
            recommendation.Action.ToString(),
            $"[{scope}] {recommendation.Reason}",
            timeProvider.GetUtcNow());

        // Ordinary unchanged management evaluations are intentionally aggregate-only telemetry.
        // Persisting one journal row per Hold would make event volume proportional to candle count.
        if (recommendation.Action == TradeManagementAction.Hold)
            return;

        await persistence.AppendAsync("management-events", new
        {
            position.PositionId,
            position.StrategyId,
            position.Instrument,
            Scope = scope,
            Sequence = sequence,
            Recommendation = recommendation
        }, cancellationToken).ConfigureAwait(false);

        switch (recommendation.Action)
        {
            case TradeManagementAction.Exit:
                await ReduceAsync(
                    position,
                    position.Quantity,
                    $"TradeManager exit: {recommendation.Reason}",
                    stageId: null,
                    runtime,
                    sequence,
                    cancellationToken).ConfigureAwait(false);
                return;
            case TradeManagementAction.ReducePosition:
                await ApplyReductionRecommendationAsync(
                    position,
                    recommendation,
                    runtime,
                    sequence,
                    cancellationToken).ConfigureAwait(false);
                return;
            case TradeManagementAction.MoveStop:
                await ApplyStopRecommendationAsync(
                    position,
                    executablePrice,
                    recommendation,
                    runtime,
                    sequence,
                    cancellationToken).ConfigureAwait(false);
                return;
            case TradeManagementAction.ReduceAndMoveStop:
                await ApplyReductionRecommendationAsync(
                    position,
                    recommendation,
                    runtime,
                    sequence,
                    cancellationToken).ConfigureAwait(false);
                LivePositionRecord? remaining = registry.Snapshot.Positions.FirstOrDefault(item =>
                    item.PositionId == position.PositionId);
                if (remaining is not null)
                {
                    await ApplyStopRecommendationAsync(
                        remaining,
                        executablePrice,
                        recommendation,
                        runtime,
                        sequence,
                        cancellationToken).ConfigureAwait(false);
                }
                return;
        }
    }

    private async Task ApplyReductionRecommendationAsync(
        LivePositionRecord position,
        TradeManagementRecommendation recommendation,
        PositionRuntimeState runtime,
        long sequence,
        CancellationToken cancellationToken)
    {
        if (recommendation.PositionReduction is not { } reduction)
            return;
        decimal quantity = Math.Min(position.Quantity, reduction.QuantityToClose);
        if (quantity <= 0m)
            return;
        await ReduceAsync(
            position,
            quantity,
            recommendation.Reason,
            reduction.StageId,
            runtime,
            sequence,
            cancellationToken).ConfigureAwait(false);
        if (reduction.Reason == PositionReductionReason.Stagnation)
            runtime.StagnationReductionCompleted = true;
        else if (reduction.Reason == PositionReductionReason.StructuralDeterioration)
            runtime.StructuralDeteriorationReductionCount++;
        else if (reduction.Reason == PositionReductionReason.MomentumDecay)
            runtime.MomentumDecayReductionCount++;
        else if (reduction.Reason == PositionReductionReason.VolatilityExhaustion)
            runtime.VolatilityExhaustionReductionCount++;
        else if (reduction.Reason == PositionReductionReason.RegimeDegradation)
            runtime.RegimeDegradationReductionCount++;
        else if (reduction.Reason == PositionReductionReason.SessionRisk)
            runtime.RiskWindowReductionCompleted = true;
        else if (reduction.Reason == PositionReductionReason.ExecutionCostStress)
            runtime.ExecutionCostStressReductionCompleted = true;
    }

    private async Task ReduceAsync(
        LivePositionRecord position,
        decimal quantity,
        string reason,
        string? stageId,
        PositionRuntimeState runtime,
        long sequence,
        CancellationToken cancellationToken)
    {
        bool partialClose = quantity < position.Quantity;
        if (partialClose && !executionOptions.PartialCloseEnabled)
        {
            if (!runtime.PartialCloseSuppressed)
            {
                runtime.PartialCloseSuppressed = true;
                await persistence.AppendAsync("management-events", new
                {
                    position.PositionId,
                    position.StrategyId,
                    position.Instrument,
                    Action = "PartialCloseSuppressed",
                    Reason = "Partial close remains disabled until OANDA Practice certification is explicitly enabled.",
                    RequestedQuantity = quantity,
                    Sequence = sequence
                }, cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        runtime.PendingReduction = true;
        try
        {
            PositionReductionResult result = await execution.ReducePositionAsync(
                position,
                quantity,
                reason,
                cancellationToken).ConfigureAwait(false);
            await persistence.AppendAsync("management-events", result, cancellationToken).ConfigureAwait(false);
            if (result.Certainty == ExecutionCertainty.Unknown)
            {
                safety.Pause(
                    "A position reduction has unknown broker certainty; entries are paused pending reconciliation.",
                    timeProvider.GetUtcNow());
                return;
            }
            if (result.Certainty == ExecutionCertainty.Accepted)
            {
                if (!string.IsNullOrWhiteSpace(stageId))
                    runtime.CompletedReductionStageIds.Add(stageId);
                runtime.LastReductionSequence = sequence;
                runtime.AnalysisBarsSinceLastReduction = 0;
            }
        }
        finally
        {
            runtime.PendingReduction = false;
        }
    }

    private async Task ApplyStopRecommendationAsync(
        LivePositionRecord position,
        decimal executablePrice,
        TradeManagementRecommendation recommendation,
        PositionRuntimeState runtime,
        long sequence,
        CancellationToken cancellationToken)
    {
        if (recommendation.ProposedStopPrice is not > 0m)
            return;
        if (!executionOptions.DynamicStopReplacementEnabled)
        {
            if (!runtime.StopAmendmentSuppressed)
            {
                runtime.StopAmendmentSuppressed = true;
                await persistence.AppendAsync("management-events", new
                {
                    position.PositionId,
                    position.StrategyId,
                    position.Instrument,
                    Action = "ProtectiveStopAmendmentSuppressed",
                    Reason = "The original broker stop remains active; dynamic replacement is disabled until OANDA Practice certification is explicitly enabled.",
                    ProposedStopPrice = recommendation.ProposedStopPrice.Value,
                    Sequence = sequence
                }, cancellationToken).ConfigureAwait(false);
            }
            return;
        }
        ProtectiveStopAmendmentResult result = await execution.AmendProtectiveStopAsync(
            position,
            recommendation.ProposedStopPrice.Value,
            executablePrice,
            MinimumPriceIncrement(position.Instrument),
            recommendation.OpenProfitR,
            recommendation.AmendmentReason,
            recommendation.Reason,
            sequence,
            cancellationToken).ConfigureAwait(false);
        await persistence.AppendAsync("management-events", result, cancellationToken).ConfigureAwait(false);
        if (result.Certainty == ExecutionCertainty.Unknown)
        {
            safety.Pause(
                "A protective-stop amendment has unknown broker certainty; entries are paused pending reconciliation.",
                timeProvider.GetUtcNow());
        }
        else if (result.Certainty == ExecutionCertainty.Accepted)
        {
            runtime.LastAmendmentSequence = sequence;
            runtime.AnalysisBarsSinceLastAmendment = 0;
        }
    }

    private PositionRuntimeState StateFor(
        LivePositionRecord position,
        LiveManagementPolicy management,
        PositionManagementOptions options)
    {
        LiveTradingPolicyBundle policy = management.Policy;
        if (_states.TryGetValue(position.PositionId, out PositionRuntimeState? existing) &&
            string.Equals(existing.ConfigurationHash, policy.ConfigurationHash, StringComparison.Ordinal))
        {
            return existing;
        }
        IStructureBasedTradeManager manager = management.CalibrationOptions.Enabled &&
            management.Calibration is not null
            ? new CalibratedStructureBasedTradeManager(
                options,
                management.Calibration,
                management.CalibrationOptions,
                policy.RegimeManagement)
            : policy.RegimeManagement.Enabled
                ? new RegimeAwareStructureBasedTradeManager(options, policy.RegimeManagement)
                : new StructureBasedTradeManager(options);
        var state = new PositionRuntimeState
        {
            ConfigurationHash = policy.ConfigurationHash,
            Manager = manager,
            MaximumFavourableExcursionR = position.MaximumFavourableExcursionR,
            MaximumAdverseExcursionR = position.MaximumAdverseExcursionR
        };
        _states[position.PositionId] = state;
        return state;
    }

    private static PositionManagementOptions ResolveOptions(
        string strategyId,
        LiveTradingPolicyBundle policy)
    {
        if (string.Equals(
                strategyId.Trim(),
                TradingAgentTypeIds.StructuralConfluence,
                StringComparison.OrdinalIgnoreCase))
        {
            return policy.StructuralManagement;
        }

        return strategyId.Contains("legacy", StringComparison.OrdinalIgnoreCase)
            ? policy.LegacyManagement
            : policy.ImprovedManagement;
    }

    // TODO(task #9 follow-up): LiveTradingPolicyBundle carries no reference to the paired agent's
    // own RequiredIntervals/TriggerInterval, so unlike StrategySimulationSession (backtesting) this
    // path cannot yet derive Fast/Main/Thesis from the agent that actually produced the trade.
    // These fallbacks only preserve pre-existing live behavior (the values StructuralDefaults/
    // LegacyDefaults/ImprovedDefaults used to hardcode) so removing those hardcoded preset values
    // for backtesting's benefit doesn't silently disable live structural/thesis-scope management.
    // Real agent-derived alignment for live still needs to be built.
    private static readonly BarInterval FallbackFastStructureInterval = BarInterval.Minutes(5);
    private static readonly BarInterval FallbackMainStructureInterval = BarInterval.Minutes(15);
    private static readonly BarInterval FallbackThesisInterval = BarInterval.Hours(1);

    private static (TradeManagementEvaluationScope Scope, BarInterval? Interval) SelectEvaluation(
        MarketAnalysisUpdate update,
        PositionManagementOptions options)
    {
        BarInterval? thesis = options.ThesisInterval ?? FallbackThesisInterval;
        BarInterval? main = options.MainStructureInterval ?? options.ManagementInterval ?? FallbackMainStructureInterval;
        BarInterval? fast = options.FastStructureInterval ?? FallbackFastStructureInterval;
        if (thesis is BarInterval thesisInterval && update.ClosedIntervals.Contains(thesisInterval))
            return (TradeManagementEvaluationScope.Thesis, thesisInterval);
        if (main is BarInterval mainInterval && update.ClosedIntervals.Contains(mainInterval))
            return (TradeManagementEvaluationScope.MainStructure, mainInterval);
        if (fast is BarInterval fastInterval && update.ClosedIntervals.Contains(fastInterval))
            return (TradeManagementEvaluationScope.FastStructure, fastInterval);
        return (TradeManagementEvaluationScope.Mechanical, null);
    }

    private static EquityProtectionDirective? TranslateEquityProtection(
        EquityHighWatermarkSnapshot snapshot)
    {
        if (snapshot.PendingPositionAction is not EquityProtectionAction action ||
            snapshot.PendingPositionTierId is not string tierId)
            return null;
        return new EquityProtectionDirective
        {
            TierId = tierId,
            Action = action == EquityProtectionAction.FlattenAllPositions
                ? EquityProtectionPositionAction.FlattenAllPositions
                : EquityProtectionPositionAction.ReduceOpenPositions,
            ReductionFraction = snapshot.PendingPositionReductionFraction
        };
    }

    private static int SaturatingIncrement(int value) =>
        value == int.MaxValue ? int.MaxValue : value + 1;

    private decimal MinimumPriceIncrement(InstrumentKey instrument)
    {
        LiveAccountStateSnapshot? snapshot = accountState.Snapshot;
        if (snapshot is not null &&
            snapshot.InstrumentMetadata.TryGetValue(instrument, out InstrumentTradingMetadata? metadata))
        {
            return metadata.PriceIncrement;
        }
        return instrument.Value.EndsWith("/JPY", StringComparison.OrdinalIgnoreCase)
            ? 0.001m
            : 0.00001m;
    }

    private decimal MinimumQuantityIncrement(InstrumentKey instrument)
    {
        LiveAccountStateSnapshot? snapshot = accountState.Snapshot;
        if (snapshot is not null &&
            snapshot.InstrumentMetadata.TryGetValue(instrument, out InstrumentTradingMetadata? metadata))
        {
            return metadata.QuantityStep;
        }
        return instrument.Value.StartsWith("FX:", StringComparison.OrdinalIgnoreCase)
            ? 1m
            : 0.00000001m;
    }

    private sealed class PositionRuntimeState
    {
        public required string ConfigurationHash { get; init; }
        public required IStructureBasedTradeManager Manager { get; init; }
        public HashSet<string> CompletedReductionStageIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool PendingReduction { get; set; }
        public long? LastReductionSequence { get; set; }
        public long? LastAmendmentSequence { get; set; }
        public int AnalysisBarsSinceLastReduction { get; set; } = int.MaxValue;
        public int AnalysisBarsSinceLastAmendment { get; set; } = int.MaxValue;
        public int AnalysisBarsWithoutNewMfe { get; set; }
        public decimal LastObservedMfeR { get; set; }
        public decimal MaximumFavourableExcursionR { get; set; }
        public decimal MaximumAdverseExcursionR { get; set; }
        public bool StagnationReductionCompleted { get; set; }
        public int StructuralDeteriorationReductionCount { get; set; }
        public int MomentumDecayReductionCount { get; set; }
        public int VolatilityExhaustionReductionCount { get; set; }
        public int RegimeDegradationReductionCount { get; set; }
        public bool VolatilityExpansionSeenSinceEntry { get; set; }
        public bool RiskWindowReductionCompleted { get; set; }
        public bool ExecutionCostStressReductionCompleted { get; set; }
        public bool PartialCloseSuppressed { get; set; }
        public bool StopAmendmentSuppressed { get; set; }
    }
}
