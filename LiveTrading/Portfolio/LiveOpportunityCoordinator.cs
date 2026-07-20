using Agent.Models;
using Brokers.Models;
using PortfolioManager.Allocation;
using PortfolioManager.Risk;
using RiskManager;
using LiveTrading.Agents;

namespace LiveTrading.Portfolio;

/// <summary>
/// Serialized Phase-3 admission point. It composes only non-increasing risk multipliers, performs
/// account-currency position sizing, ranks simultaneous opportunities deterministically, and
/// reserves risk/margin before a candidate can reach an execution gateway.
/// </summary>
public sealed class LiveOpportunityCoordinator : ILiveOpportunityCoordinator
{
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly IPortfolioReservationBook _reservations;
    private readonly CapitalAllocatorOptions _allocatorOptions;

    public LiveOpportunityCoordinator(
        IPortfolioReservationBook reservations,
        CapitalAllocatorOptions? allocatorOptions = null)
    {
        _reservations = reservations ?? throw new ArgumentNullException(nameof(reservations));
        _allocatorOptions = allocatorOptions ?? new CapitalAllocatorOptions();
        _allocatorOptions.Validate();
    }

    public async Task<IReadOnlyList<PortfolioDecision>> EvaluateEpochAsync(
        IReadOnlyList<LiveTradeCandidate> candidates,
        LiveOpportunityEvaluationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(context);
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return EvaluateCore(candidates, context);
        }
        finally
        {
            _serial.Release();
        }
    }

    private IReadOnlyList<PortfolioDecision> EvaluateCore(
        IReadOnlyList<LiveTradeCandidate> candidates,
        LiveOpportunityEvaluationContext context)
    {
        var rejected = new List<PortfolioDecision>();
        var prepared = new Dictionary<string, PreparedOpportunity>(StringComparer.Ordinal);
        var seenCandidates = new HashSet<string>(StringComparer.Ordinal);
        var opportunities = new List<PortfolioOpportunity>();

        foreach (LiveTradeCandidate candidate in candidates
                     .OrderBy(c => c.Instrument.Value, StringComparer.Ordinal)
                     .ThenBy(c => c.StrategyId, StringComparer.Ordinal)
                     .ThenBy(c => c.DecisionId, StringComparer.Ordinal))
        {
            string candidateKey = CandidateKey(candidate);
            if (!seenCandidates.Add(candidateKey))
            {
                rejected.Add(Reject(candidate, "DuplicateCandidate", "The same strategy/instrument/decision candidate appeared more than once in one epoch."));
                continue;
            }

            PortfolioDecision? invalid = ValidateCandidate(candidate);
            if (invalid is not null)
            {
                rejected.Add(invalid);
                continue;
            }

            LiveTradingPolicyBundle policy = context.PolicyResolver(candidate);
            decimal equity = context.Portfolio.Equity;
            decimal baseRisk = policy.PositionSizing.Mode switch
            {
                PositionSizingMode.FixedCashRisk => policy.PositionSizing.FixedCashRisk,
                PositionSizingMode.FixedFractionalRisk => equity * policy.PositionSizing.RiskPercentOfEquity / 100m,
                _ => 0m
            };

            decimal conditionMultiplier = Clamp(candidate.TradingCondition?.RiskMultiplier ?? 1m);
            decimal regimeMultiplier = 1m;
            decimal liquidityMultiplier = Clamp(context.LiquidityMultipliers.GetValueOrDefault(candidate.Instrument, 1m));
            decimal correlationMultiplier = Clamp(context.CorrelationMultipliers.GetValueOrDefault(candidate.Instrument, 1m));
            decimal strategyMultiplier = Clamp(context.StrategyMultipliers.GetValueOrDefault(candidate.StrategyId, 1m));
            decimal equityMultiplier = Clamp(context.EquityProtectionMultipliers.GetValueOrDefault(candidate.Instrument, 1m));

            var riskPolicy = new RiskBudgetPolicy(policy.AdaptiveRisk);
            RiskBudgetDecision risk = riskPolicy.Evaluate(new RiskBudgetContext
            {
                AccountEquity = equity,
                DrawdownPercent = context.DrawdownPercent,
                VolatilityPercentile = context.VolatilityPercentiles.TryGetValue(candidate.Instrument, out decimal percentile)
                    ? percentile
                    : null,
                RegimeMultiplier = regimeMultiplier,
                LiquidityMultiplier = liquidityMultiplier,
                CorrelationMultiplier = correlationMultiplier,
                StrategyAllocationMultiplier = strategyMultiplier,
                EquityProtectionMultiplier = equityMultiplier,
                CalibrationMultiplier = Clamp(candidate.SetupCalibration.RiskMultiplier),
                MetaLabelMultiplier = Clamp(candidate.MetaLabel.RiskMultiplier),
                NeoWaveMultiplier = Clamp(candidate.NeoWaveRiskMultiplier),
                StructuralEvidenceMultiplier = Clamp(candidate.StructuralEvidenceRiskMultiplier)
            });
            decimal combined = Clamp(risk.CombinedMultiplier * conditionMultiplier);
            var riskAudit = new LiveRiskBudgetAudit
            {
                BaseRiskAmount = baseRisk,
                SetupMultiplier = risk.CalibrationMultiplier,
                MetaLabelMultiplier = risk.MetaLabelMultiplier,
                NeoWaveMultiplier = risk.NeoWaveMultiplier,
                StructuralEvidenceMultiplier = risk.StructuralEvidenceMultiplier,
                RegimeMultiplier = risk.RegimeMultiplier,
                TradingConditionMultiplier = conditionMultiplier,
                DrawdownMultiplier = risk.DrawdownMultiplier,
                VolatilityMultiplier = risk.VolatilityMultiplier,
                LiquidityMultiplier = risk.LiquidityMultiplier,
                CorrelationMultiplier = risk.CorrelationMultiplier,
                StrategyMultiplier = risk.StrategyAllocationMultiplier,
                EquityProtectionMultiplier = risk.EquityProtectionMultiplier,
                CombinedMultiplier = combined,
                FinalRiskAmount = baseRisk * combined,
                ReasonCode = combined <= 0m ? "RiskBudgetRejected" : combined < 1m ? "RiskBudgetAdjusted" : "BaseRiskBudget"
            };
            if (combined <= 0m)
            {
                rejected.Add(Reject(candidate, riskAudit.ReasonCode, "The composed risk budget is zero."));
                continue;
            }

            if (context.RequireInstrumentMetadata &&
                !context.InstrumentMetadata.TryGetValue(candidate.Instrument, out _))
            {
                rejected.Add(Reject(
                    candidate,
                    "BrokerInstrumentMetadataUnavailable",
                    "The broker did not provide minimum size, precision and margin metadata for this instrument."));
                continue;
            }

            context.InstrumentMetadata.TryGetValue(
                candidate.Instrument,
                out InstrumentTradingMetadata? instrumentMetadata);
            PositionSizingOptions effectiveSizing;
            AgentDecision decision;
            try
            {
                effectiveSizing = ApplyBrokerExecutionConstraints(
                    policy.PositionSizing,
                    instrumentMetadata);
                decision = NormalizeDecisionForBroker(
                    ToDecision(candidate, policy, combined),
                    instrumentMetadata);
            }
            catch (InvalidOperationException exception)
            {
                rejected.Add(Reject(candidate, "BrokerNormalizationFailed", exception.Message));
                continue;
            }
            decimal quoteRate = context.QuoteToAccountCurrencyRates.GetValueOrDefault(candidate.Instrument, 0m);
            InstrumentRiskSpec spec = context.InstrumentRiskSpecs.GetValueOrDefault(
                candidate.Instrument, InstrumentRiskSpec.UnitNotional);
            var sizer = new PositionSizer(effectiveSizing);
            PositionSizingResult sizing = sizer.Calculate(new PositionSizingContext
            {
                Decision = decision,
                RequestedQuantity = effectiveSizing.FixedQuantity,
                Accounts = [context.Portfolio.Account],
                Positions = context.Portfolio.Positions,
                QuoteToAccountCurrencyRate = quoteRate,
                InstrumentSpec = spec,
                KnownOpenRiskAccountCurrency = context.Portfolio.CurrentOpenRiskAccountCurrency,
                InstrumentSpecs = context.InstrumentRiskSpecs,
                QuoteToAccountRatesByInstrument = context.QuoteToAccountCurrencyRates,
                RiskBudgetMultiplier = combined,
                RiskBudgetDecision = risk
            });
            if (!sizing.Approved)
            {
                rejected.Add(Reject(candidate, sizing.ReasonCode, sizing.Reason));
                continue;
            }

            if (policy.PositionSizing.Mode == PositionSizingMode.FixedQuantity &&
                sizing.EstimatedLossAtStop is decimal fixedEstimatedRisk)
            {
                decimal fixedBaseRisk = combined > 0m ? fixedEstimatedRisk / combined : fixedEstimatedRisk;
                riskAudit = riskAudit with
                {
                    BaseRiskAmount = fixedBaseRisk,
                    FinalRiskAmount = fixedEstimatedRisk
                };
            }

            AgentDecision sizedDecision = decision with { SuggestedQuantity = sizing.Quantity };
            var sizingAudit = new LivePositionSizingAudit
            {
                RequestedRisk = sizing.RiskBudget ?? riskAudit.FinalRiskAmount,
                RawQuantity = sizing.Quantity,
                BrokerNormalizedQuantity = sizing.Quantity,
                EstimatedStopLoss = sizing.EstimatedLossAtStop ?? 0m,
                EstimatedMargin = sizing.EstimatedMargin ?? 0m,
                ExpectedCosts = EstimateExpectedCosts(
                    effectiveSizing,
                    spec,
                    decision.ReferencePrice!.Value,
                    sizing.Quantity,
                    quoteRate),
                QuoteConversionPath = quoteRate > 0m
                    ? $"{candidate.Instrument.Value}:quote->{context.Portfolio.Account.Currency ?? "account"}@{quoteRate}"
                    : "unavailable",
                ReasonCode = sizing.ReasonCode
            };

            prepared[candidateKey] = new PreparedOpportunity(
                candidate,
                policy,
                sizedDecision,
                riskAudit,
                sizingAudit);
            opportunities.Add(new PortfolioOpportunity
            {
                StrategyId = candidate.StrategyId,
                Decision = sizedDecision,
                Sizing = sizing,
                SetupQuality = Math.Clamp(candidate.RawConfidence / 100m, 0m, 1m),
                ExpectedRewardRisk = Math.Max(0m, sizedDecision.ExpectedRewardRisk ?? 0m),
                RegimeSuitability = Math.Clamp(candidate.MultiTimeframeAlignment, 0m, 1m),
                TransactionCostPenalty = candidate.TradingCondition?.SpreadAtr ?? 0m,
                CorrelationPenalty = 1m - correlationMultiplier,
                CurrencyConcentrationPenalty = 0m,
                MarginConsumptionPenalty = equity > 0m ? (sizing.EstimatedMargin ?? 0m) / equity : 1m,
                CreatedAt = candidate.DecisionTime,
                Sequence = candidate.DecisionEpoch,
                MinimumQuantity = effectiveSizing.MinimumQuantity,
                QuantityStep = effectiveSizing.QuantityStep,
                AllowPartialAllocation = true
            });
        }

        var allocator = new CapitalAllocator(_reservations, _allocatorOptions);
        IReadOnlyList<PortfolioAllocationDecision> allocations = allocator.Allocate(
            opportunities,
            new PortfolioAllocationContext
            {
                AccountEquity = context.Portfolio.Equity,
                CurrentOpenRiskAccountCurrency = context.Portfolio.CurrentOpenRiskAccountCurrency,
                CurrentMarginUsed = context.Portfolio.Account.MarginUsed ?? 0m,
                CurrentOpenPositions = context.Portfolio.Positions.Count,
                OpenStrategyRisk = context.Portfolio.OpenStrategyRisk,
                OpenInstrumentRisk = context.Portfolio.OpenInstrumentRisk,
                OpenCurrencyRisk = context.Portfolio.OpenCurrencyRisk,
                CurrencyExposureFactory = BuildCurrencyExposure,
                CorrelationClusterFactory = opportunity => opportunity.Decision.RiskClusterId ?? "unclassified"
            });

        foreach (PortfolioAllocationDecision allocation in allocations)
        {
            string key = CandidateKey(allocation.Opportunity.Decision);
            PreparedOpportunity item = prepared[key];
            if (!allocation.Approved || string.IsNullOrWhiteSpace(allocation.ReservationId))
            {
                rejected.Add(Reject(item.Candidate, allocation.ReasonCode, allocation.Explanation));
                continue;
            }

            decimal allocationRatio = allocation.OriginalQuantity > 0m
                ? allocation.AllocatedQuantity / allocation.OriginalQuantity
                : 0m;
            LivePositionSizingAudit allocatedSizing = item.PositionSizing with
            {
                RequestedRisk = item.PositionSizing.RequestedRisk * allocationRatio,
                BrokerNormalizedQuantity = allocation.AllocatedQuantity,
                EstimatedStopLoss = item.PositionSizing.EstimatedStopLoss * allocationRatio,
                EstimatedMargin = item.PositionSizing.EstimatedMargin * allocationRatio,
                ExpectedCosts = item.PositionSizing.ExpectedCosts * allocationRatio
            };
            LiveRiskBudgetAudit allocatedRisk = item.RiskBudget with
            {
                FinalRiskAmount = allocatedSizing.EstimatedStopLoss
            };
            AgentDecision approvedDecision = item.Decision with
            {
                SuggestedQuantity = allocation.AllocatedQuantity,
                PortfolioReservationId = allocation.ReservationId,
                PortfolioOriginalQuantity = allocation.OriginalQuantity,
                PortfolioAllocatedQuantity = allocation.AllocatedQuantity,
                FinalRiskBudgetMultiplier = item.RiskBudget.CombinedMultiplier,
                QuantityIsPortfolioApproved = true
            };
            rejected.Add(new PortfolioDecision
            {
                Candidate = item.Candidate,
                Approved = true,
                ReasonCode = allocation.ReasonCode,
                Explanation = allocation.Explanation,
                ApprovedDecision = new PortfolioApprovedDecision
                {
                    Candidate = item.Candidate,
                    Decision = approvedDecision,
                    ReservationId = allocation.ReservationId,
                    PortfolioScore = allocation.Score,
                    RiskBudget = allocatedRisk,
                    PositionSizing = allocatedSizing,
                    PolicyBundleId = item.Policy.PolicyBundleId,
                    PolicyRevision = item.Policy.Revision,
                    ConfigurationHash = item.Policy.ConfigurationHash
                }
            });
        }

        return rejected
            .OrderByDescending(d => d.ApprovedDecision?.PortfolioScore ?? decimal.MinValue)
            .ThenBy(d => d.ApprovedDecision?.RiskBudget.FinalRiskAmount ?? decimal.MaxValue)
            .ThenBy(d => d.Candidate.Instrument.Value, StringComparer.Ordinal)
            .ThenBy(d => d.Candidate.StrategyId, StringComparer.Ordinal)
            .ThenBy(d => d.Candidate.DecisionId, StringComparer.Ordinal)
            .ToArray();
    }

    private static PortfolioDecision? ValidateCandidate(LiveTradeCandidate candidate)
    {
        if (candidate.Action is not (AgentAction.Buy or AgentAction.Sell))
            return Reject(candidate, "UnsupportedCandidateAction", "Only opening Buy/Sell candidates enter portfolio admission.");
        if (candidate.ReferencePrice is not > 0m || candidate.StopLossPrice is not > 0m)
            return Reject(candidate, "MissingInitialProtection", "A positive reference price and broker-side initial stop are mandatory.");
        if (!candidate.SetupCalibration.Trade || !candidate.MetaLabel.Trade)
            return Reject(candidate, "ModelRejected", "Setup calibration or meta-label rejected the candidate.");
        if (candidate.MetaLabel.RiskMultiplier is < 0m or > 1m ||
            candidate.SetupCalibration.RiskMultiplier is < 0m or > 1m ||
            candidate.NeoWaveRiskMultiplier is < 0m or > 1m ||
            candidate.StructuralEvidenceRiskMultiplier is < 0m or > 1m ||
            candidate.TradingCondition?.RiskMultiplier is < 0m or > 1m)
            return Reject(candidate, "InvalidRiskMultiplier", "All candidate risk multipliers must remain within 0..1.");
        return null;
    }

    private static AgentDecision ToDecision(
        LiveTradeCandidate candidate,
        LiveTradingPolicyBundle policy,
        decimal combinedMultiplier)
    {
        decimal reference = candidate.ReferencePrice!.Value;
        decimal stop = candidate.StopLossPrice!.Value;
        decimal? expectedR = candidate.TakeProfitPrice is decimal target
            ? Math.Abs(target - reference) / Math.Abs(reference - stop)
            : null;
        return new AgentDecision
        {
            DecisionId = candidate.DecisionId,
            SetupId = candidate.SetupId,
            StrategyId = candidate.StrategyId,
            StrategyName = candidate.StrategyId,
            Action = candidate.Action,
            Instrument = candidate.Instrument,
            SuggestedQuantity = policy.PositionSizing.FixedQuantity,
            QuantityUnit = QuantityUnit.Units,
            OrderType = StandardOrderType.Market,
            ReferencePrice = reference,
            StopLossPrice = stop,
            TakeProfitPrice = candidate.TakeProfitPrice,
            ExpectedRewardRisk = expectedR,
            Confidence = candidate.RawConfidence,
            CreatedAt = candidate.DecisionTime,
            Reason = "Approved live candidate pending portfolio allocation.",
            ReasonCode = "LiveCandidate",
            RegimeLabel = candidate.EntryRegime,
            RegimeRiskMultiplier = 1m,
            TradingConditionRiskMultiplier = candidate.TradingCondition?.RiskMultiplier,
            TradingConditionReasonCode = candidate.TradingCondition?.ReasonCode,
            SpreadAtr = candidate.TradingCondition?.SpreadAtr,
            SetupCalibrationRiskMultiplier = candidate.SetupCalibration.RiskMultiplier,
            MetaLabelRiskMultiplier = candidate.MetaLabel.RiskMultiplier,
            MetaLabelProbability = candidate.MetaLabel.Probability,
            MetaLabelModelVersion = candidate.MetaLabel.ModelVersion,
            MetaLabelReasonCode = candidate.MetaLabel.ReasonCode,
            NeoWaveRiskMultiplier = candidate.NeoWaveRiskMultiplier,
            NeoWaveHypothesisId = candidate.NeoWaveHypothesisId,
            NeoWavePatternType = candidate.NeoWavePatternType,
            NeoWaveDirection = candidate.NeoWaveDirection,
            NeoWaveStructuralScore = candidate.NeoWaveStructuralScore,
            NeoWaveConflictScore = candidate.NeoWaveConflictScore,
            NeoWaveInvalidationPrice = candidate.NeoWaveInvalidationPrice,
            StructuralEvidenceRiskMultiplier = candidate.StructuralEvidenceRiskMultiplier,
            EntrySupplyDemandZoneId = candidate.EntrySupplyDemandZoneId,
            EntrySupplyDemandZoneLowerPrice = candidate.EntrySupplyDemandZoneLowerPrice,
            EntrySupplyDemandZoneUpperPrice = candidate.EntrySupplyDemandZoneUpperPrice,
            EntrySupplyDemandZoneState = candidate.EntrySupplyDemandZoneState,
            EntrySupplyDemandProfileHash = candidate.EntrySupplyDemandProfileHash,
            OriginatingLiquidityPoolId = candidate.OriginatingLiquidityPoolId,
            OriginatingLiquiditySweepId = candidate.OriginatingLiquiditySweepId,
            OriginatingLiquidityProfileHash = candidate.OriginatingLiquidityProfileHash,
            TargetLiquidityPoolId = candidate.TargetLiquidityPoolId,
            TargetLiquidityProfileHash = candidate.TargetLiquidityProfileHash,
            StructuralInvalidationReference = candidate.StructuralInvalidationReference,
            EntrySupplyDemandManagementEnabled = candidate.EntrySupplyDemandManagementEnabled,
            EntryLiquidityManagementEnabled = candidate.EntryLiquidityManagementEnabled,
            StructuralManagementPolicyRevision = candidate.StructuralManagementPolicyRevision,
            FinalRiskBudgetMultiplier = combinedMultiplier
        };
    }

    private static PositionSizingOptions ApplyBrokerExecutionConstraints(
        PositionSizingOptions configured,
        InstrumentTradingMetadata? metadata)
    {
        if (metadata is null)
            return configured;
        metadata.Validate();
        decimal? maximum = configured.MaximumQuantity;
        if (metadata.MaximumOrderQuantity is decimal brokerMaximum)
            maximum = maximum is decimal configuredMaximum
                ? Math.Min(configuredMaximum, brokerMaximum)
                : brokerMaximum;
        decimal minimum = Math.Max(configured.MinimumQuantity, metadata.MinimumQuantity);
        if (maximum is decimal effectiveMaximum && effectiveMaximum < minimum)
        {
            throw new InvalidOperationException(
                $"Broker maximum quantity {effectiveMaximum} is below the effective minimum {minimum} for {metadata.Instrument}.");
        }
        return configured with
        {
            MinimumQuantity = minimum,
            MaximumQuantity = maximum,
            QuantityStep = Math.Max(configured.QuantityStep, metadata.QuantityStep)
        };
    }

    private static AgentDecision NormalizeDecisionForBroker(
        AgentDecision decision,
        InstrumentTradingMetadata? metadata)
    {
        if (metadata is null)
            return decision;
        decimal reference = decision.ReferencePrice ?? throw new InvalidOperationException(
            "A live decision requires a reference price before broker normalization.");
        decimal stop = decision.StopLossPrice ?? throw new InvalidOperationException(
            "A live decision requires an initial stop before broker normalization.");
        decimal normalizedStop = decision.Action == AgentAction.Buy
            ? RoundDown(stop, metadata.PriceIncrement)
            : RoundUp(stop, metadata.PriceIncrement);
        decimal? normalizedTarget = decision.TakeProfitPrice is decimal target
            ? decision.Action == AgentAction.Buy
                ? RoundDown(target, metadata.PriceIncrement)
                : RoundUp(target, metadata.PriceIncrement)
            : null;
        if (decision.Action == AgentAction.Buy && normalizedStop >= reference ||
            decision.Action == AgentAction.Sell && normalizedStop <= reference ||
            normalizedTarget is decimal normalizedTakeProfit &&
            (decision.Action == AgentAction.Buy && normalizedTakeProfit <= reference ||
             decision.Action == AgentAction.Sell && normalizedTakeProfit >= reference))
        {
            throw new InvalidOperationException(
                $"Broker price normalization invalidated the stop/target geometry for {decision.Instrument}.");
        }
        decimal? expectedR = normalizedTarget is decimal takeProfit
            ? Math.Abs(takeProfit - reference) / Math.Abs(reference - normalizedStop)
            : null;
        return decision with
        {
            StopLossPrice = normalizedStop,
            TakeProfitPrice = normalizedTarget,
            ExpectedRewardRisk = expectedR
        };
    }

    private static decimal RoundDown(decimal value, decimal increment) =>
        Math.Floor(value / increment) * increment;

    private static decimal RoundUp(decimal value, decimal increment) =>
        Math.Ceiling(value / increment) * increment;

    private static IReadOnlyDictionary<string, decimal> BuildCurrencyExposure(
        PortfolioOpportunity opportunity,
        decimal quantity)
    {
        (string baseCurrency, string quoteCurrency) = CurrencyExposureCalculator.ParseCurrencies(
            opportunity.Decision.Instrument);
        decimal risk = opportunity.Sizing.EstimatedLossAtStop.GetValueOrDefault();
        decimal scaledRisk = opportunity.Sizing.Quantity > 0m
            ? risk * quantity / opportunity.Sizing.Quantity
            : risk;
        return new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            [baseCurrency] = scaledRisk / 2m,
            [quoteCurrency] = scaledRisk / 2m
        };
    }

    private static decimal EstimateExpectedCosts(
        PositionSizingOptions options,
        InstrumentRiskSpec spec,
        decimal referencePrice,
        decimal quantity,
        decimal quoteRate)
    {
        if (options.EstimatedRoundTripCostBasisPoints <= 0m ||
            referencePrice <= 0m || quantity <= 0m || quoteRate <= 0m)
            return 0m;
        decimal costDistance = referencePrice * options.EstimatedRoundTripCostBasisPoints / 10_000m;
        return spec.EstimateStopLossAccountCurrency(costDistance, quantity, quoteRate);
    }

    private static string CandidateKey(LiveTradeCandidate candidate) =>
        $"{candidate.StrategyId}|{candidate.Instrument.Value}|{candidate.DecisionId}";

    private static string CandidateKey(AgentDecision decision) =>
        $"{decision.StrategyId}|{decision.Instrument.Value}|{decision.DecisionId}";

    private static PortfolioDecision Reject(LiveTradeCandidate candidate, string code, string explanation) => new()
    {
        Candidate = candidate,
        Approved = false,
        ReasonCode = code,
        Explanation = explanation
    };

    private static decimal Clamp(decimal value) => Math.Clamp(value, 0m, 1m);

    private sealed record PreparedOpportunity(
        LiveTradeCandidate Candidate,
        LiveTradingPolicyBundle Policy,
        AgentDecision Decision,
        LiveRiskBudgetAudit RiskBudget,
        LivePositionSizingAudit PositionSizing);
}
