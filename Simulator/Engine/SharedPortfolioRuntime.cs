using Agent.Models;
using Brokers.Abstractions;
using Brokers.Models;
using ExecutionManager;
using PortfolioManager.Allocation;
using PortfolioManager.Correlation;
using PortfolioManager.Risk;
using RiskManager;
using RiskManager.Safety;
using Simulator.Broker;
using Simulator.Models;

namespace Simulator.Engine;

/// <summary>
/// Deterministic shared-account admission layer. Strategy-owned virtual lots keep
/// attribution separate, while one allocator, reservation book, account high-watermark,
/// and aggregate equity/margin view make all entry decisions authoritative per frame.
/// </summary>
public sealed class SharedPortfolioRuntime
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Registration> _registrations = new(StringComparer.Ordinal);
    private readonly List<QueuedOpportunity> _queued = [];
    private readonly Dictionary<string, PendingReservation> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _recordedClosedTrades = new(StringComparer.Ordinal);
    private readonly PositionSizingOptions _sizingOptions;
    private readonly AdaptiveRiskOptions _adaptiveRiskOptions;
    private readonly SimulationOptions _simulationOptions;
    private readonly PortfolioRiskOptions _portfolioRiskOptions;
    private readonly ICurrencyExposureCalculator _currencyExposureCalculator = new CurrencyExposureCalculator();
    private readonly RollingCorrelationClusters _correlationClusters;
    private CorrelationSnapshot? _correlationSnapshot;
    private long _opportunitySequence;
    private long _riskObservationCount;
    private decimal _latestHeat;
    private decimal _peakHeat;
    private decimal _totalMarginUsed;
    private decimal _peakMarginUsed;
    private int _opportunities;
    private int _rejectedOpportunities;
    private int _resizedOpportunities;
    private decimal _opportunityCostScore;
    private decimal _maximumAccountGiveback;
    private readonly Dictionary<string, decimal> _peakCurrencyRisk = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, decimal> _peakClusterHeat = new(StringComparer.Ordinal);

    public SharedPortfolioRuntime(
        SimulationOptions simulationOptions,
        PortfolioRiskOptions portfolioOptions,
        PositionSizingOptions sizingOptions,
        AdaptiveRiskOptions adaptiveRiskOptions,
        TradingSafetyOptions safetyOptions,
        CorrelationRiskOptions? correlationOptions = null)
    {
        _simulationOptions = simulationOptions ?? throw new ArgumentNullException(nameof(simulationOptions));
        _sizingOptions = sizingOptions ?? throw new ArgumentNullException(nameof(sizingOptions));
        _adaptiveRiskOptions = adaptiveRiskOptions ?? throw new ArgumentNullException(nameof(adaptiveRiskOptions));
        _portfolioRiskOptions = portfolioOptions ?? throw new ArgumentNullException(nameof(portfolioOptions));
        Reservations = new PortfolioReservationBook(portfolioOptions);
        RiskManager = new PortfolioRiskManager(portfolioOptions);
        CapitalAllocator = new CapitalAllocator(Reservations);
        AccountSafety = new TradingSafetyController(safetyOptions);
        _correlationClusters = new RollingCorrelationClusters(correlationOptions);
    }

    /// <summary>
    /// Feeds every portfolio instrument's completed trailing-return observation (e.g. a
    /// closed 1h candle's log return) for one timestamp into the correlation engine in a
    /// single call, so pair correlation can match observations by exact timestamp. Must
    /// be called with strictly increasing timestamps. Today's simulator streams one
    /// instrument per run, so the engine always passes a one-entry dictionary; once the
    /// multi-instrument portfolio clock lands, every basket instrument closing at the
    /// same timestamp should be included in one call.
    /// </summary>
    internal void ObserveCompletedReturns(DateTimeOffset availableAt, IReadOnlyDictionary<InstrumentKey, decimal> completedReturns)
    {
        lock (_sync)
        {
            _correlationSnapshot = _correlationClusters.Update(availableAt, completedReturns);
        }
    }

    /// <summary>
    /// Real correlation evaluation for a candidate against currently open/reserved
    /// portfolio directions. Internal (rather than private) so it can be exercised
    /// directly by tests without needing a full multi-instrument market-data feed -
    /// today's simulator only streams one instrument per run, so this is normally
    /// exercised end-to-end only with synthetic multi-instrument decisions until the
    /// multi-instrument portfolio clock lands.
    /// </summary>
    internal CorrelationPenaltyDecision EvaluateCorrelation(
        InstrumentKey candidate,
        OrderSide side,
        IReadOnlyDictionary<InstrumentKey, int> existingDirections)
    {
        Dictionary<InstrumentKey, int> others = existingDirections
            .Where(item => item.Key != candidate)
            .ToDictionary(item => item.Key, item => item.Value);
        if (others.Count == 0)
        {
            string cluster = _correlationSnapshot?.ClusterByInstrument.GetValueOrDefault(candidate) ??
                $"cluster:{candidate.Value}";
            return new CorrelationPenaltyDecision
            {
                RiskMultiplier = 1m,
                MaximumRelevantCorrelation = 0m,
                ClusterId = cluster,
                ReasonCode = "NoOtherPortfolioInstruments"
            };
        }
        int candidateDirection = side == OrderSide.Sell ? -1 : 1;
        return _correlationClusters.Evaluate(candidate, candidateDirection, others);
    }

    private Dictionary<InstrumentKey, int> BuildExistingDirections(
        IReadOnlyList<PortfolioPositionLot> lots,
        PortfolioReservationSnapshot reservationSnapshot)
    {
        var net = new Dictionary<InstrumentKey, decimal>();
        foreach (PortfolioPositionLot lot in lots)
        {
            net[lot.Instrument] = net.GetValueOrDefault(lot.Instrument) +
                (lot.Side == OrderSide.Buy ? lot.Quantity : -lot.Quantity);
        }
        foreach (PortfolioReservation reservation in reservationSnapshot.Reservations)
        {
            if (!_pending.TryGetValue(reservation.ReservationId, out PendingReservation? pending)) continue;
            decimal signed = pending.Decision.Action == AgentAction.Sell
                ? -reservation.RemainingQuantity
                : reservation.RemainingQuantity;
            net[reservation.Instrument] = net.GetValueOrDefault(reservation.Instrument) + signed;
        }
        return net
            .Where(item => item.Value != 0m)
            .ToDictionary(item => item.Key, item => Math.Sign(item.Value));
    }

    public IPortfolioRiskManager RiskManager { get; }
    public ICapitalAllocator CapitalAllocator { get; }
    public IPortfolioReservationBook Reservations { get; }
    public ITradingSafetyController AccountSafety { get; }
    public PortfolioPerformanceSnapshot Performance => new()
    {
        FinalHeat = _latestHeat,
        PeakHeat = _peakHeat,
        AverageMarginUsed = _riskObservationCount == 0 ? 0m : _totalMarginUsed / _riskObservationCount,
        PeakMarginUsed = _peakMarginUsed,
        Opportunities = _opportunities,
        RejectedOpportunities = _rejectedOpportunities,
        ResizedOpportunities = _resizedOpportunities,
        OpportunityCostScore = _opportunityCostScore,
        PeakCurrencyRisk = new Dictionary<string, decimal>(_peakCurrencyRisk, StringComparer.OrdinalIgnoreCase),
        PeakClusterHeat = new Dictionary<string, decimal>(_peakClusterHeat, StringComparer.Ordinal),
        MaximumAccountGiveback = _maximumAccountGiveback,
        ProtectionActivations = AccountSafety.Snapshot.EquityProtection.TotalActivatedTierCount
    };

    internal IExecutionCoordinator Decorate(
        string strategyId,
        IExecutionCoordinator underlying)
    {
        lock (_sync)
        {
            _registrations[strategyId] = new Registration(null, underlying);
        }
        return new DeferredPortfolioExecutionCoordinator(strategyId, underlying, this);
    }

    internal void Register(StrategySimulationSession session)
    {
        lock (_sync)
        {
            if (!_registrations.TryGetValue(session.StrategyId, out Registration? registration))
                throw new InvalidOperationException($"Strategy '{session.StrategyId}' was not decorated for shared admission.");
            _registrations[session.StrategyId] = registration with { Session = session };
        }
    }

    internal void Enqueue(string strategyId, AgentDecision decision, ITradingBrokerClient broker)
    {
        lock (_sync)
        {
            _queued.Add(new QueuedOpportunity(
                strategyId,
                decision,
                broker,
                ++_opportunitySequence));
        }
    }

    public async Task<IReadOnlyList<StrategyReplayEvent>> FlushAsync(
        long frameSequence,
        DateTimeOffset eventTime,
        CancellationToken cancellationToken)
    {
        List<QueuedOpportunity> queued;
        lock (_sync)
        {
            queued = _queued.OrderBy(item => item.Sequence).ToList();
            _queued.Clear();
        }

        var events = new List<StrategyReplayEvent>();
        ReconcileReservations(events, frameSequence, eventTime);
        AccountSnapshot account = AggregateAccount();
        decimal equity = (account.Balance ?? 0m) + (account.UnrealizedProfitLoss ?? 0m);
        TradingSafetySnapshot previousSafety = AccountSafety.Snapshot;
        RecordNewClosedTrades();
        TradingSafetySnapshot currentSafety = AccountSafety.ObserveEquity(equity, eventTime);
        foreach (Registration registration in _registrations.Values)
            registration.RequiredSession.SetSharedEquityProtectionDirective(currentSafety.EquityProtection);
        AddAccountSafetyEvents(events, previousSafety, currentSafety, frameSequence, eventTime);
        IReadOnlyList<PortfolioPositionLot> lots = BuildLots();
        PortfolioReservationSnapshot reservationSnapshot = Reservations.Snapshot;
        PortfolioHeatSnapshot heat = PortfolioHeatCalculator.Calculate(
            equity,
            lots,
            reservationSnapshot.Reservations);
        var openCurrencyRisk = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (PortfolioPositionLot lot in lots)
        {
            decimal risk = LotRisk(lot);
            (string baseCurrency, string quoteCurrency) = CurrencyExposureCalculator.ParseCurrencies(lot.Instrument);
            openCurrencyRisk[baseCurrency] = openCurrencyRisk.GetValueOrDefault(baseCurrency) + risk / 2m;
            openCurrencyRisk[quoteCurrency] = openCurrencyRisk.GetValueOrDefault(quoteCurrency) + risk / 2m;
        }
        foreach (PortfolioReservation reservation in reservationSnapshot.Reservations)
        foreach ((string currency, decimal risk) in reservation.CurrencyExposureDelta)
            openCurrencyRisk[currency] = openCurrencyRisk.GetValueOrDefault(currency) + Math.Abs(risk);

        // Real notional net/gross exposure (distinct from the stop-risk split above).
        // Open positions only - pending reservations aren't folded in here since
        // CurrencyExposureDelta above is stop-risk-shaped, not notional.
        CurrencyExposureResult openExposure = _currencyExposureCalculator.Calculate(
            lots, [], BuildFxConversionSnapshot(eventTime, lots));

        _riskObservationCount++;
        _latestHeat = heat.TotalHeat;
        _peakHeat = Math.Max(_peakHeat, heat.TotalHeat);
        decimal marginUsed = account.MarginUsed ?? 0m;
        _totalMarginUsed += marginUsed;
        _peakMarginUsed = Math.Max(_peakMarginUsed, marginUsed);
        _maximumAccountGiveback = Math.Max(
            _maximumAccountGiveback,
            currentSafety.EquityProtection.DrawdownFromPeak);
        foreach ((string currency, decimal risk) in openCurrencyRisk)
            _peakCurrencyRisk[currency] = Math.Max(_peakCurrencyRisk.GetValueOrDefault(currency), risk);
        foreach ((string cluster, decimal risk) in heat.ClusterHeat)
            _peakClusterHeat[cluster] = Math.Max(_peakClusterHeat.GetValueOrDefault(cluster), risk);

        foreach (Registration registration in _registrations.Values)
        {
            StrategySimulationSession session = registration.RequiredSession;
            session.SetSharedPortfolioRiskStatus(new PortfolioRiskStatusSnapshot
            {
                OpenHeat = heat.OpenHeat,
                PendingHeat = heat.PendingHeat,
                TotalHeat = heat.TotalHeat,
                TotalHeatPercent = heat.TotalHeatPercent,
                StrategyHeat = heat.StrategyHeat.GetValueOrDefault(session.StrategyId),
                InstrumentHeat = heat.InstrumentHeat.Values.Sum(),
                CurrencyRisk = new Dictionary<string, decimal>(openCurrencyRisk, StringComparer.OrdinalIgnoreCase),
                NetCurrencyExposure = openExposure.NetExposureAccountCurrency,
                GrossCurrencyExposure = openExposure.GrossExposureAccountCurrency,
                CurrencyExposureMissingCurrencies = openExposure.MissingCurrencies,
                ClusterHeat = heat.ClusterHeat,
                MarginUsed = account.MarginUsed ?? 0m,
                ReservedMargin = reservationSnapshot.ReservedMargin,
                UnallocatedMargin = Math.Max(0m,
                    equity - (account.MarginUsed ?? 0m) - reservationSnapshot.ReservedMargin),
                AccountPeakEquity = currentSafety.EquityProtection.PeakEquity,
                AccountProtectedFloor = currentSafety.EquityProtection.ProtectedFloor,
                AccountActivatedTierIds = currentSafety.EquityProtection.ActivatedTierIds
                    .OrderBy(item => item, StringComparer.Ordinal)
                    .ToArray()
            });
        }
        if (queued.Count == 0) return events;

        if (!AccountSafety.CanOpenNewTrades)
        {
            foreach (QueuedOpportunity item in queued)
            {
                RejectPending(item, null);
                events.Add(Event(item, frameSequence, eventTime,
                    StrategyReplayEventType.PortfolioRiskReservationRejected,
                    "SharedAccountSafetyBlocked",
                    "The shared-account safety controller blocked new portfolio risk."));
            }
            return events;
        }

        Dictionary<InstrumentKey, int> existingDirections = BuildExistingDirections(lots, reservationSnapshot);
        var opportunities = new List<PortfolioOpportunity>();
        foreach (QueuedOpportunity item in queued)
        {
            AgentDecision decision = item.Decision;
            if (!TryResolveQuoteRate(item, out decimal quoteRate))
            {
                opportunities.Add(new PortfolioOpportunity
                {
                    StrategyId = item.StrategyId,
                    Decision = decision,
                    Sizing = new PositionSizingResult
                    {
                        Approved = false,
                        Quantity = 0m,
                        ReasonCode = "CurrencyConversionUnavailable",
                        Reason = $"A contemporaneous quote-to-account conversion for {decision.Instrument} is required."
                    },
                    SetupQuality = 0m,
                    ExpectedRewardRisk = decision.ExpectedRewardRisk ?? 0m,
                    RegimeSuitability = decision.RegimeRiskMultiplier ?? 1m,
                    TransactionCostPenalty = 0m,
                    CorrelationPenalty = 0m,
                    CurrencyConcentrationPenalty = 1m,
                    MarginConsumptionPenalty = 1m,
                    CreatedAt = decision.CreatedAt,
                    Sequence = item.Sequence,
                    MinimumQuantity = _sizingOptions.MinimumQuantity,
                    QuantityStep = _sizingOptions.QuantityStep
                });
                events.Add(Event(item, frameSequence, eventTime,
                    StrategyReplayEventType.PortfolioRiskReservationRejected,
                    "CurrencyConversionUnavailable",
                    $"Shared admission rejected {decision.Instrument}; conversion data was unavailable."));
                continue;
            }
            CorrelationPenaltyDecision correlation = EvaluateCorrelation(
                decision.Instrument,
                decision.Action == AgentAction.Sell ? OrderSide.Sell : OrderSide.Buy,
                existingDirections);
            decision = decision with
            {
                CorrelationRiskMultiplier = correlation.RiskMultiplier,
                RiskClusterId = correlation.ClusterId
            };
            decimal riskMultiplier = RiskMultiplier(decision);
            PositionSizingResult sizing = new PositionSizer(_sizingOptions).Calculate(new PositionSizingContext
            {
                Decision = decision,
                RequestedQuantity = decision.SuggestedQuantity ?? _sizingOptions.FixedQuantity,
                Accounts = [account],
                Positions = AggregatePositions(),
                QuoteToAccountCurrencyRate = quoteRate,
                InstrumentSpec = InstrumentRiskSpec.ForInstrument(decision.Instrument),
                KnownOpenRiskAccountCurrency = heat.OpenHeat,
                RiskBudgetMultiplier = riskMultiplier
            });
            sizing = AddMonetaryEstimates(sizing, decision, quoteRate);
            if (sizing.Approved && sizing.Quantity > 0m)
            {
                decimal referencePrice = decision.ReferencePrice ?? decision.LimitPrice ?? decision.StopPrice ?? 0m;
                (bool exposureApproved, string? exposureReason, string? exposureExplanation) = EvaluateCurrencyExposureLimits(
                    eventTime, decision, sizing.Quantity, referencePrice, quoteRate, lots, equity);
                if (!exposureApproved)
                    sizing = sizing with { Approved = false, ReasonCode = exposureReason!, Reason = exposureExplanation! };
            }
            opportunities.Add(new PortfolioOpportunity
            {
                StrategyId = item.StrategyId,
                Decision = decision,
                Sizing = sizing,
                // Raw strategy confidence is a rule score, not a calibrated
                // probability. Use a neutral rank contribution unless a validated
                // meta-label probability is present.
                SetupQuality = Math.Clamp(decision.MetaLabelProbability ?? 0.5m, 0m, 1m),
                ExpectedRewardRisk = decision.ExpectedRewardRisk ?? 0m,
                RegimeSuitability = decision.RegimeRiskMultiplier ?? 1m,
                TransactionCostPenalty = _sizingOptions.EstimatedRoundTripCostBasisPoints / 10_000m,
                CorrelationPenalty = 1m - correlation.RiskMultiplier,
                CurrencyConcentrationPenalty = 0m,
                MarginConsumptionPenalty = sizing.EstimatedMargin is decimal margin && equity > 0m
                    ? margin / equity
                    : 1m,
                CreatedAt = decision.CreatedAt,
                Sequence = item.Sequence,
                MinimumQuantity = _sizingOptions.MinimumQuantity,
                QuantityStep = _sizingOptions.QuantityStep
            });
            events.Add(Event(item, frameSequence, eventTime,
                StrategyReplayEventType.PortfolioOpportunityCreated,
                "PortfolioOpportunityCreated",
                $"Created opportunity with estimated quantity {sizing.Quantity} and risk {sizing.EstimatedLossAtStop}."));
        }

        IReadOnlyList<PortfolioAllocationDecision> allocations = CapitalAllocator.Allocate(
            opportunities,
            new PortfolioAllocationContext
            {
                AccountEquity = equity,
                CurrentOpenRiskAccountCurrency = heat.OpenHeat,
                CurrentMarginUsed = account.MarginUsed ?? 0m,
                CurrentOpenPositions = lots.Count,
                OpenStrategyRisk = heat.StrategyHeat,
                OpenInstrumentRisk = heat.InstrumentHeat,
                OpenCurrencyRisk = openCurrencyRisk,
                CorrelationClusterFactory = opportunity =>
                    opportunity.Decision.RiskClusterId ?? $"cluster:{opportunity.Decision.Instrument.Value}",
                CurrencyExposureFactory = (opportunity, quantity) => CurrencyRiskDelta(opportunity, quantity)
            });

        _opportunities += allocations.Count;

        foreach (PortfolioAllocationDecision allocation in allocations)
        {
            QueuedOpportunity item = queued.Single(candidate =>
                candidate.Sequence == allocation.Opportunity.Sequence);
            events.Add(Event(item, frameSequence, eventTime,
                StrategyReplayEventType.PortfolioOpportunityRanked,
                allocation.ReasonCode,
                $"score={allocation.Score:F4}; original={allocation.OriginalQuantity}; allocated={allocation.AllocatedQuantity}.",
                allocation.ReservationId,
                allocation.OriginalQuantity,
                allocation.AllocatedQuantity));
            if (!allocation.Approved)
            {
                _rejectedOpportunities++;
                _opportunityCostScore += Math.Max(0m, allocation.Score);
                RejectPending(item, null);
                events.Add(Event(item, frameSequence, eventTime,
                    StrategyReplayEventType.PortfolioRiskReservationRejected,
                    allocation.ReasonCode,
                    allocation.Explanation));
                continue;
            }

            if (allocation.AllocatedQuantity < allocation.OriginalQuantity)
                _resizedOpportunities++;

            AgentDecision allocatedDecision = allocation.Opportunity.Decision with
            {
                PortfolioReservationId = allocation.ReservationId,
                PortfolioOriginalQuantity = allocation.OriginalQuantity,
                PortfolioAllocatedQuantity = allocation.AllocatedQuantity,
                StrategyAllocationRiskMultiplier = allocation.OriginalQuantity <= 0m
                    ? 0m
                    : Math.Min(1m, allocation.AllocatedQuantity / allocation.OriginalQuantity)
            };
            Registration registration = _registrations[item.StrategyId];
            OrderSubmission? submission = await registration.Underlying
                .ProcessAsync(allocatedDecision, item.Broker, cancellationToken)
                .ConfigureAwait(false);
            if (submission is null || submission.Status == SubmissionStatus.Rejected ||
                string.IsNullOrWhiteSpace(submission.BrokerOrderId))
            {
                Reservations.Release(allocation.ReservationId!, PortfolioReleaseReason.Rejected);
                RejectPending(item, submission);
                events.Add(Event(item, frameSequence, eventTime,
                    StrategyReplayEventType.PortfolioRiskReservationReleased,
                    "SubmissionRejected",
                    submission?.RejectionReason ?? "The allocated order was not submitted.",
                    allocation.ReservationId));
                continue;
            }

            registration.RequiredSession.ApplyPortfolioAdmission(allocatedDecision, submission);
            _pending[allocation.ReservationId!] = new PendingReservation(
                item.StrategyId,
                submission.BrokerOrderId,
                allocatedDecision,
                0m);
            events.Add(Event(item, frameSequence, eventTime,
                allocation.AllocatedQuantity < allocation.OriginalQuantity
                    ? StrategyReplayEventType.PortfolioQuantityReduced
                    : StrategyReplayEventType.PortfolioRiskReserved,
                allocation.ReasonCode,
                allocation.Explanation,
                allocation.ReservationId,
                allocation.OriginalQuantity,
                allocation.AllocatedQuantity));
        }
        return events;
    }

    private void RecordNewClosedTrades()
    {
        foreach (Registration registration in _registrations.Values
                     .OrderBy(item => item.RequiredSession.StrategyId, StringComparer.Ordinal))
        {
            foreach (SimulatedTradeRecord trade in registration.RequiredSession.Trades
                         .Where(item => item.ClosedAt is not null)
                         .OrderBy(item => item.ClosedAt)
                         .ThenBy(item => item.SetupId, StringComparer.Ordinal))
            {
                string key = $"{trade.StrategyId}|{trade.SetupId}|{trade.PositionId}|{trade.ClosedAt:O}";
                if (_recordedClosedTrades.Add(key))
                    AccountSafety.RecordClosedTrade(trade.NetProfitLoss, trade.ClosedAt!.Value);
            }
        }
    }

    private void AddAccountSafetyEvents(
        List<StrategyReplayEvent> events,
        TradingSafetySnapshot previous,
        TradingSafetySnapshot current,
        long sequence,
        DateTimeOffset eventTime)
    {
        EquityHighWatermarkSnapshot before = previous.EquityProtection;
        EquityHighWatermarkSnapshot after = current.EquityProtection;
        if (after.PeakEquity > before.PeakEquity ||
            after.CurrentRiskMultiplier != before.CurrentRiskMultiplier)
        {
            AddForEveryStrategy(StrategyReplayEventType.AccountHighWatermarkUpdated,
                "AccountHighWatermarkUpdated",
                $"Shared equity={after.CurrentEquity:F2}; peak={after.PeakEquity:F2}; drawdown={after.DrawdownPercent:F3}%.",
                before.PeakEquity,
                after.PeakEquity);
        }

        HashSet<string> beforeTiers = before.ActivatedTierIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] newlyActivated = after.ActivatedTierIds
            .Where(tier => !beforeTiers.Contains(tier))
            .OrderBy(tier => tier, StringComparer.Ordinal)
            .ToArray();
        foreach (string tier in newlyActivated)
        {
            AddForEveryStrategy(StrategyReplayEventType.EquityProtectionTierActivated,
                tier,
                $"Shared-account equity protection tier '{tier}' activated.");
        }

        if (before.ActivatedTierIds.Count > 0 && after.ActivatedTierIds.Count == 0)
        {
            AddForEveryStrategy(StrategyReplayEventType.EquityProtectionRecovered,
                "AccountEquityProtectionRecovered",
                "Shared-account equity protection completed its configured recovery confirmation.");
        }

        if (after.PendingPositionAction != before.PendingPositionAction ||
            !string.Equals(after.PendingPositionTierId, before.PendingPositionTierId, StringComparison.Ordinal))
        {
            StrategyReplayEventType type = after.PendingPositionAction == EquityProtectionAction.FlattenAllPositions
                ? StrategyReplayEventType.EquityProtectionFlattenRequested
                : StrategyReplayEventType.EquityProtectionReductionRequested;
            if (after.PendingPositionAction is not null)
            {
                AddForEveryStrategy(type,
                    after.PendingPositionTierId ?? after.PendingPositionAction.ToString()!,
                    $"Shared-account safety requested {after.PendingPositionAction} across owned positions.",
                    before.PendingPositionReductionFraction,
                    after.PendingPositionReductionFraction);
            }
        }

        void AddForEveryStrategy(
            StrategyReplayEventType type,
            string reasonCode,
            string reason,
            decimal? previousValue = null,
            decimal? newValue = null)
        {
            foreach (Registration registration in _registrations.Values
                         .OrderBy(item => item.RequiredSession.StrategyId, StringComparer.Ordinal))
            {
                events.Add(new StrategyReplayEvent
                {
                    Type = type,
                    StrategyId = registration.RequiredSession.StrategyId,
                    Sequence = sequence,
                    EventTime = eventTime,
                    ReasonCode = reasonCode,
                    Reason = reason,
                    PreviousValue = previousValue,
                    NewValue = newValue,
                    OptionOrModelVersion = "shared-account-safety-v1"
                });
            }
        }
    }

    private void ReconcileReservations(
        List<StrategyReplayEvent> events,
        long sequence,
        DateTimeOffset eventTime)
    {
        foreach ((string reservationId, PendingReservation stored) in _pending.ToArray()
                     .OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            PendingReservation pending = stored;
            Registration registration = _registrations[pending.StrategyId];
            BrokerOrder? order = registration.RequiredSession.Broker.State.GetOrder(pending.BrokerOrderId);
            if (order is null) continue;
            decimal filledQuantity = order.FilledQuantity ?? 0m;
            decimal newlyFilled = Math.Max(0m, filledQuantity - pending.CommittedQuantity);
            if (newlyFilled > 0m)
            {
                PortfolioReservation? reservation = Reservations.Snapshot.Reservations
                    .FirstOrDefault(item => item.ReservationId == reservationId);
                if (reservation is not null)
                {
                    decimal fraction = newlyFilled / reservation.Quantity;
                    Reservations.CommitFill(reservationId, new PortfolioFillAllocation
                    {
                        FilledQuantity = newlyFilled,
                        FillRiskAccountCurrency = reservation.PlannedStopRiskAccountCurrency * fraction,
                        FillMarginAccountCurrency = reservation.EstimatedMargin * fraction
                    });
                }
                pending = pending with { CommittedQuantity = filledQuantity };
                _pending[reservationId] = pending;
            }
            if (order.NormalizedStatus is OrderStatus.Cancelled or OrderStatus.Expired or OrderStatus.Rejected)
            {
                Reservations.Release(reservationId, PortfolioReleaseReason.Cancelled);
                _pending.Remove(reservationId);
                events.Add(new StrategyReplayEvent
                {
                    Type = StrategyReplayEventType.PortfolioRiskReservationReleased,
                    StrategyId = pending.StrategyId,
                    DecisionId = pending.Decision.DecisionId,
                    OrderId = pending.BrokerOrderId,
                    ReservationId = reservationId,
                    Sequence = sequence,
                    EventTime = eventTime,
                    ReasonCode = order.NormalizedStatus.ToString(),
                    Reason = "The pending portfolio reservation was released exactly once."
                });
            }
            else if (order.NormalizedStatus == OrderStatus.Filled)
            {
                _pending.Remove(reservationId);
            }
        }
    }

    /// <summary>
    /// Balance is reconciled to one shared starting pool (not summed per strategy). Margin is
    /// netted per instrument across strategies' virtual lots — two strategies opposite-sided on
    /// the same instrument offset, matching real broker margin economics, instead of each being
    /// charged full margin independently. Positions themselves stay separate per-strategy broker
    /// records: merging them into one broker-side position would require rebuilding fill-matching,
    /// OCO-group management, and the exclusive full-quantity-ownership assumptions baked into
    /// StrategySimulationSession's exit-management paths and
    /// ExecutionCoordinator.AmendProtectiveStopCoreAsync's exact-quantity match — out of scope here.
    /// </summary>
    private AccountSnapshot AggregateAccount()
    {
        AccountSnapshot[] accounts = _registrations.Values
            .Select(item => item.RequiredSession.Broker.State.GetAccount())
            .ToArray();
        decimal realisedDelta = accounts.Sum(item => (item.Balance ?? _simulationOptions.StartingBalance) -
            _simulationOptions.StartingBalance);
        decimal balance = _simulationOptions.StartingBalance + realisedDelta;
        decimal unrealised = accounts.Sum(item => item.UnrealizedProfitLoss ?? 0m);
        decimal marginUsed = BuildLots()
            .GroupBy(lot => lot.Instrument)
            .Sum(group =>
            {
                decimal netQuantity = group.Sum(lot => lot.Side == OrderSide.Buy ? lot.Quantity : -lot.Quantity);
                PortfolioPositionLot reference = group.First();
                return Math.Abs(netQuantity) * reference.CurrentPrice * reference.QuoteToAccountCurrencyRate /
                    _simulationOptions.Leverage;
            });
        decimal available = balance + unrealised - marginUsed;
        return new AccountSnapshot
        {
            AccountId = "shared-portfolio-account",
            AccountType = "Shared virtual-lot portfolio",
            Currency = _simulationOptions.BaseCurrency,
            Balance = balance,
            UnrealizedProfitLoss = unrealised,
            MarginUsed = marginUsed,
            Available = available,
            CanTrade = available > 0m
        };
    }

    private IReadOnlyList<BrokerPosition> AggregatePositions() => _registrations.Values
        .SelectMany(item => item.RequiredSession.Broker.State.GetPositions())
        .OrderBy(item => item.Instrument.Value, StringComparer.Ordinal)
        .ThenBy(item => item.PositionId, StringComparer.Ordinal)
        .ToArray();

    private IReadOnlyList<PortfolioPositionLot> BuildLots()
    {
        var lots = new List<PortfolioPositionLot>();
        foreach (Registration registration in _registrations.Values.OrderBy(item => item.RequiredSession.StrategyId, StringComparer.Ordinal))
        {
            SimulatedTradeRecord? trade = registration.RequiredSession.ActiveTrade;
            if (trade?.EntryPrice is not decimal entry) continue;
            BrokerPosition? position = registration.RequiredSession.Broker.State.GetPositions()
                .FirstOrDefault(item => item.Instrument == trade.Instrument);
            if (position is null) continue;
            decimal conversion = registration.RequiredSession.Broker.TryGetQuoteToAccountCurrencyRate(trade.Instrument, out decimal rate)
                ? rate
                : 0m;
            lots.Add(new PortfolioPositionLot
            {
                LotId = $"{registration.RequiredSession.StrategyId}:{position.PositionId}",
                StrategyId = registration.RequiredSession.StrategyId,
                DecisionId = trade.SetupId,
                Instrument = trade.Instrument,
                Side = trade.Side,
                Quantity = position.Quantity,
                EntryPrice = entry,
                CurrentPrice = registration.RequiredSession.LastExecutablePrice ?? entry,
                ProtectiveStopPrice = trade.CurrentStopLossPrice ?? trade.InitialStopLossPrice,
                EstimatedExitCostAccountCurrency = position.Quantity * entry * conversion *
                    _sizingOptions.EstimatedRoundTripCostBasisPoints / 20_000m,
                MarginAccountCurrency = position.Quantity * entry * conversion / _simulationOptions.Leverage,
                QuoteToAccountCurrencyRate = conversion,
                CorrelationClusterId = _correlationSnapshot?.ClusterByInstrument.GetValueOrDefault(trade.Instrument) ??
                    $"cluster:{trade.Instrument.Value}"
            });
        }
        return lots;
    }

    private static bool TryResolveQuoteRate(QueuedOpportunity item, out decimal rate)
    {
        if (item.Broker is IAccountCurrencyConversionProvider conversion &&
            conversion.TryGetQuoteToAccountCurrencyRate(item.Decision.Instrument, out rate) && rate > 0m)
            return true;
        rate = 0m;
        return false;
    }

    private decimal RiskMultiplier(AgentDecision decision)
    {
        EquityHighWatermarkSnapshot safety = AccountSafety.Snapshot.EquityProtection;
        return new RiskBudgetPolicy(_adaptiveRiskOptions).Evaluate(new RiskBudgetContext
        {
            AccountEquity = AggregateAccount().Balance ?? 0m,
            DrawdownPercent = safety.DrawdownPercent,
            VolatilityPercentile = decision.AtrPercentile,
            RegimeMultiplier = decision.RegimeRiskMultiplier ?? 1m,
            LiquidityMultiplier = decision.TradingConditionRiskMultiplier ?? 1m,
            CorrelationMultiplier = decision.CorrelationRiskMultiplier ?? 1m,
            StrategyAllocationMultiplier = 1m,
            EquityProtectionMultiplier = safety.CurrentRiskMultiplier,
            CalibrationMultiplier = decision.SetupCalibrationRiskMultiplier ?? 1m,
            MetaLabelMultiplier = decision.MetaLabelRiskMultiplier ?? 1m,
            NeoWaveMultiplier = decision.NeoWaveRiskMultiplier ?? 1m,
            StructuralEvidenceMultiplier = decision.StructuralEvidenceRiskMultiplier ?? 1m
        }).CombinedMultiplier;
    }

    private PositionSizingResult AddMonetaryEstimates(
        PositionSizingResult sizing,
        AgentDecision decision,
        decimal quoteRate)
    {
        if (!sizing.Approved || sizing.Quantity <= 0m) return sizing;
        decimal reference = decision.ReferencePrice ?? decision.LimitPrice ?? decision.StopPrice ?? 0m;
        if (reference <= 0m || decision.StopLossPrice is not decimal stop)
            return sizing with { Approved = false, ReasonCode = "PortfolioRiskEstimateUnavailable", Reason = "Shared admission requires reference and protective-stop prices." };
        decimal risk = sizing.EstimatedLossAtStop ??
            (Math.Abs(reference - stop) * sizing.Quantity * quoteRate +
             reference * sizing.Quantity * quoteRate * _sizingOptions.EstimatedRoundTripCostBasisPoints / 10_000m);
        decimal margin = sizing.EstimatedMargin ??
            reference * sizing.Quantity * quoteRate / _simulationOptions.Leverage;
        return sizing with { EstimatedLossAtStop = risk, EstimatedMargin = margin };
    }

    private IReadOnlyDictionary<string, decimal> CurrencyRiskDelta(
        PortfolioOpportunity opportunity,
        decimal quantity)
    {
        (string baseCurrency, string quoteCurrency) = CurrencyExposureCalculator.ParseCurrencies(
            opportunity.Decision.Instrument);
        decimal risk = (opportunity.Sizing.EstimatedLossAtStop ?? 0m) *
            quantity / opportunity.Sizing.Quantity;
        return new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            [baseCurrency] = risk / 2m,
            [quoteCurrency] = risk / 2m
        };
    }

    /// <summary>
    /// Derives an account-currency conversion rate for every currency present in the
    /// open book (plus, optionally, one candidate instrument) purely from data already
    /// on each lot - no extra broker calls needed. For a BASE/QUOTE instrument,
    /// QuoteToAccountCurrencyRate already converts the quote currency; the base
    /// currency's account-currency rate is CurrentPrice * QuoteToAccountCurrencyRate
    /// (one unit of base = CurrentPrice units of quote = that many account-currency units).
    /// </summary>
    private FxConversionSnapshot BuildFxConversionSnapshot(
        DateTimeOffset availableAt,
        IReadOnlyList<PortfolioPositionLot> lots,
        InstrumentKey? candidateInstrument = null,
        decimal candidatePrice = 0m,
        decimal candidateQuoteRate = 0m)
    {
        var rates = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        void AddRates(InstrumentKey instrument, decimal price, decimal quoteToAccountRate)
        {
            if (price <= 0m || quoteToAccountRate <= 0m) return;
            (string baseCurrency, string quoteCurrency) = CurrencyExposureCalculator.ParseCurrencies(instrument);
            rates[quoteCurrency] = quoteToAccountRate;
            rates[baseCurrency] = price * quoteToAccountRate;
        }
        foreach (PortfolioPositionLot lot in lots)
            AddRates(lot.Instrument, lot.CurrentPrice, lot.QuoteToAccountCurrencyRate);
        if (candidateInstrument is InstrumentKey candidate)
            AddRates(candidate, candidatePrice, candidateQuoteRate);
        return new FxConversionSnapshot
        {
            AvailableAt = availableAt,
            AccountCurrency = _simulationOptions.BaseCurrency,
            CurrencyToAccountRates = rates
        };
    }

    /// <summary>
    /// Projects real net/gross notional currency exposure (via <see cref="CurrencyExposureCalculator"/>)
    /// with the candidate added to the current open book, and rejects the candidate if it
    /// would breach the configured notional-exposure limits. Distinct from the existing
    /// stop-risk-based <see cref="PortfolioReservationBook"/> currency gate.
    /// </summary>
    internal (bool Approved, string? ReasonCode, string? Explanation) EvaluateCurrencyExposureLimits(
        DateTimeOffset availableAt,
        AgentDecision decision,
        decimal quantity,
        decimal referencePrice,
        decimal quoteRate,
        IReadOnlyList<PortfolioPositionLot> lots,
        decimal equity)
    {
        if (quantity <= 0m || referencePrice <= 0m || quoteRate <= 0m || equity <= 0m)
            return (true, null, null);

        FxConversionSnapshot snapshot = BuildFxConversionSnapshot(
            availableAt, lots, decision.Instrument, referencePrice, quoteRate);
        var candidateLot = new PortfolioPositionLot
        {
            LotId = "candidate",
            StrategyId = "candidate",
            DecisionId = "candidate",
            Instrument = decision.Instrument,
            Side = decision.Action == AgentAction.Sell ? OrderSide.Sell : OrderSide.Buy,
            Quantity = quantity,
            EntryPrice = referencePrice,
            CurrentPrice = referencePrice,
            QuoteToAccountCurrencyRate = quoteRate
        };
        CurrencyExposureResult projected = _currencyExposureCalculator.Calculate(
            [.. lots, candidateLot], [], snapshot);
        if (!projected.Complete)
        {
            return (false, "CurrencyConversionUnavailable",
                $"Currency exposure could not be evaluated; missing conversions for " +
                $"{string.Join(",", projected.MissingCurrencies)}.");
        }
        // Exposure to the account currency itself is the funding leg of every FX trade,
        // not foreign-currency concentration risk - margin/heat limits already cover
        // overall leveraged size, so only gate genuinely foreign currencies here.
        foreach ((string currency, decimal net) in projected.NetExposureAccountCurrency)
        {
            if (string.Equals(currency, snapshot.AccountCurrency, StringComparison.OrdinalIgnoreCase)) continue;
            if (Math.Abs(net) / equity * 100m > _portfolioRiskOptions.MaximumNetCurrencyExposurePercent)
            {
                return (false, "NetCurrencyExposureLimit",
                    $"{currency} net exposure would reach {Math.Abs(net) / equity * 100m:F2}% of equity, " +
                    $"exceeding the configured {_portfolioRiskOptions.MaximumNetCurrencyExposurePercent:F2}% limit.");
            }
        }
        foreach ((string currency, decimal gross) in projected.GrossExposureAccountCurrency)
        {
            if (string.Equals(currency, snapshot.AccountCurrency, StringComparison.OrdinalIgnoreCase)) continue;
            if (gross / equity * 100m > _portfolioRiskOptions.MaximumGrossCurrencyExposurePercent)
            {
                return (false, "GrossCurrencyExposureLimit",
                    $"{currency} gross exposure would reach {gross / equity * 100m:F2}% of equity, " +
                    $"exceeding the configured {_portfolioRiskOptions.MaximumGrossCurrencyExposurePercent:F2}% limit.");
            }
        }
        return (true, null, null);
    }

    private static decimal LotRisk(PortfolioPositionLot lot)
    {
        if (lot.ProtectiveStopPrice is not decimal stop) return 0m;
        decimal distance = lot.Side == OrderSide.Buy
            ? Math.Max(0m, lot.CurrentPrice - stop)
            : Math.Max(0m, stop - lot.CurrentPrice);
        return distance * lot.Quantity * lot.QuoteToAccountCurrencyRate +
            lot.EstimatedExitCostAccountCurrency;
    }

    private void RejectPending(QueuedOpportunity item, OrderSubmission? submission)
    {
        if (_registrations.TryGetValue(item.StrategyId, out Registration? registration))
            registration.RequiredSession.RejectPortfolioAdmission(item.Decision, submission?.RejectionReason);
    }

    private static StrategyReplayEvent Event(
        QueuedOpportunity item,
        long sequence,
        DateTimeOffset eventTime,
        StrategyReplayEventType type,
        string reasonCode,
        string reason,
        string? reservationId = null,
        decimal? previousValue = null,
        decimal? newValue = null) => new()
    {
        Type = type,
        StrategyId = item.StrategyId,
        SetupId = item.Decision.SetupId,
        DecisionId = item.Decision.DecisionId,
        ReservationId = reservationId,
        Sequence = sequence,
        EventTime = eventTime,
        ReasonCode = reasonCode,
        Reason = reason,
        PreviousValue = previousValue,
        NewValue = newValue,
        OptionOrModelVersion = "shared-portfolio-v1"
    };

    private sealed record Registration(
        StrategySimulationSession? Session,
        IExecutionCoordinator Underlying)
    {
        public StrategySimulationSession RequiredSession => Session ??
            throw new InvalidOperationException("The shared portfolio strategy registration is incomplete.");
    }
    private sealed record QueuedOpportunity(
        string StrategyId,
        AgentDecision Decision,
        ITradingBrokerClient Broker,
        long Sequence);
    private sealed record PendingReservation(
        string StrategyId,
        string BrokerOrderId,
        AgentDecision Decision,
        decimal CommittedQuantity);
}

internal sealed class DeferredPortfolioExecutionCoordinator(
    string strategyId,
    IExecutionCoordinator underlying,
    SharedPortfolioRuntime portfolio) : IExecutionCoordinator
{
    public Task<OrderSubmission?> ProcessAsync(
        AgentDecision decision,
        ITradingBrokerClient broker,
        CancellationToken cancellationToken = default)
    {
        if (decision.Action is not (AgentAction.Buy or AgentAction.Sell))
            return underlying.ProcessAsync(decision, broker, cancellationToken);
        portfolio.Enqueue(strategyId, decision, broker);
        return Task.FromResult<OrderSubmission?>(new OrderSubmission
        {
            ClientOrderId = $"portfolio-pending:{strategyId}:{decision.DecisionId}",
            Status = SubmissionStatus.Pending,
            Certainty = ExecutionCertainty.NotSent
        });
    }

    public Task<ProtectiveStopAmendmentResult> AmendProtectiveStopAsync(
        ProtectiveStopAmendmentCommand command,
        ITradingBrokerClient broker,
        CancellationToken cancellationToken = default) =>
        underlying.AmendProtectiveStopAsync(command, broker, cancellationToken);
}
