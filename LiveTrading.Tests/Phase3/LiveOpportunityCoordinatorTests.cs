using Brokers.Models;
using LiveTrading.Agents;
using LiveTrading.Portfolio;
using NUnit.Framework;
using PortfolioManager.Risk;

namespace LiveTrading.Tests.Phase3;

[TestFixture]
public sealed class LiveOpportunityCoordinatorTests
{
    [Test]
    public async Task ManualCandidate_IsSizedRankedAndReservedExactlyOnce()
    {
        LiveTradingPolicyBundle policy = Phase3TestData.Policy();
        var reservations = new PortfolioReservationBook(policy.PortfolioRisk);
        var coordinator = new LiveOpportunityCoordinator(reservations);

        IReadOnlyList<PortfolioDecision> result = await coordinator.EvaluateEpochAsync(
            [Phase3TestData.Candidate()],
            Phase3TestData.Context(policy, reservations),
            CancellationToken.None);

        PortfolioDecision decision = result.Single();
        Assert.Multiple(() =>
        {
            Assert.That(decision.Approved, Is.True, decision.Explanation);
            Assert.That(decision.ApprovedDecision, Is.Not.Null);
            Assert.That(decision.ApprovedDecision!.Decision.QuantityIsPortfolioApproved, Is.True);
            Assert.That(decision.ApprovedDecision.Decision.SuggestedQuantity, Is.GreaterThan(0m));
            Assert.That(decision.ApprovedDecision.Decision.PortfolioReservationId, Is.EqualTo(decision.ApprovedDecision.ReservationId));
            Assert.That(reservations.Snapshot.Reservations, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task PortfolioAdmission_IsDeploymentModeAgnostic()
    {
        LiveTradingPolicyBundle policy = Phase3TestData.Policy();
        var reservations = new PortfolioReservationBook(policy.PortfolioRisk);
        var coordinator = new LiveOpportunityCoordinator(reservations);

        PortfolioDecision result = (await coordinator.EvaluateEpochAsync(
            [Phase3TestData.Candidate()],
            Phase3TestData.Context(policy, reservations),
            CancellationToken.None)).Single();

        Assert.Multiple(() =>
        {
            Assert.That(result.Approved, Is.True, result.Explanation);
            Assert.That(reservations.Snapshot.Reservations, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task MultiplierAboveOne_FailsClosedInsteadOfBeingSilentlyLeveragedUp()
    {
        LiveTradingPolicyBundle policy = Phase3TestData.Policy();
        var reservations = new PortfolioReservationBook(policy.PortfolioRisk);
        var coordinator = new LiveOpportunityCoordinator(reservations);

        PortfolioDecision result = (await coordinator.EvaluateEpochAsync(
            [Phase3TestData.Candidate(metaMultiplier: 1.01m)],
            Phase3TestData.Context(policy, reservations),
            CancellationToken.None)).Single();

        Assert.That(result.ReasonCode, Is.EqualTo("InvalidRiskMultiplier"));
        Assert.That(reservations.Snapshot.Reservations, Is.Empty);
    }

    [Test]
    public async Task DuplicateCandidate_CreatesOnlyOneReservation()
    {
        LiveTradingPolicyBundle policy = Phase3TestData.Policy();
        var reservations = new PortfolioReservationBook(policy.PortfolioRisk);
        var coordinator = new LiveOpportunityCoordinator(reservations);
        LiveTradeCandidate candidate = Phase3TestData.Candidate();

        IReadOnlyList<PortfolioDecision> results = await coordinator.EvaluateEpochAsync(
            [candidate, candidate],
            Phase3TestData.Context(policy, reservations),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(results.Count(item => item.Approved), Is.EqualTo(1));
            Assert.That(results.Count(item => item.ReasonCode == "DuplicateCandidate"), Is.EqualTo(1));
            Assert.That(reservations.Snapshot.Reservations, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task PartialAllocation_ScalesRiskMarginAndAuditToReservedQuantity()
    {
        LiveTradingPolicyBundle baseline = Phase3TestData.Policy();
        LiveTradingPolicyBundle policy = baseline with
        {
            PortfolioRisk = baseline.PortfolioRisk with
            {
                MaximumPendingRiskPercent = 0.05m,
                MaximumTotalOpenRiskPercent = 0.05m,
                MaximumStrategyRiskPercent = 0.05m,
                MaximumInstrumentRiskPercent = 0.05m,
                MaximumCurrencyStopRiskPercent = 0.05m
            }
        };
        policy.Validate();
        var reservations = new PortfolioReservationBook(policy.PortfolioRisk);
        var coordinator = new LiveOpportunityCoordinator(reservations);

        PortfolioApprovedDecision approved = (await coordinator.EvaluateEpochAsync(
            [Phase3TestData.Candidate()],
            Phase3TestData.Context(policy, reservations),
            CancellationToken.None)).Single(item => item.Approved).ApprovedDecision!;
        PortfolioReservation reservation = reservations.Snapshot.Reservations.Single();

        Assert.Multiple(() =>
        {
            Assert.That(approved.Decision.SuggestedQuantity, Is.LessThan(approved.PositionSizing.RawQuantity));
            Assert.That(approved.PositionSizing.BrokerNormalizedQuantity, Is.EqualTo(reservation.Quantity));
            Assert.That(approved.PositionSizing.EstimatedStopLoss,
                Is.EqualTo(reservation.PlannedStopRiskAccountCurrency));
            Assert.That(approved.PositionSizing.EstimatedMargin, Is.EqualTo(reservation.EstimatedMargin));
            Assert.That(approved.RiskBudget.FinalRiskAmount,
                Is.EqualTo(reservation.PlannedStopRiskAccountCurrency));
        });
    }

    [Test]
    public async Task FixedQuantity_WithLiveInputsProducesMonetaryRiskAudit()
    {
        LiveTradingPolicyBundle baseline = Phase3TestData.Policy();
        LiveTradingPolicyBundle policy = baseline with
        {
            PositionSizing = baseline.PositionSizing with
            {
                Mode = RiskManager.PositionSizingMode.FixedQuantity,
                FixedQuantity = 10_000m
            }
        };
        policy.Validate();
        var reservations = new PortfolioReservationBook(policy.PortfolioRisk);
        var coordinator = new LiveOpportunityCoordinator(reservations);

        PortfolioDecision result = (await coordinator.EvaluateEpochAsync(
            [Phase3TestData.Candidate()],
            Phase3TestData.Context(policy, reservations),
            CancellationToken.None)).Single();

        Assert.Multiple(() =>
        {
            Assert.That(result.Approved, Is.True, result.Explanation);
            Assert.That(result.ApprovedDecision!.PositionSizing.EstimatedStopLoss, Is.GreaterThan(0m));
            Assert.That(result.ApprovedDecision.PositionSizing.EstimatedMargin, Is.GreaterThan(0m));
            Assert.That(reservations.Snapshot.PendingRisk, Is.GreaterThan(0m));
        });
    }
    [Test]
    public async Task BrokerMetadata_NormalizesPriceQuantityAndMaximumOrderSize()
    {
        LiveTradingPolicyBundle policy = Phase3TestData.Policy();
        var reservations = new PortfolioReservationBook(policy.PortfolioRisk);
        var coordinator = new LiveOpportunityCoordinator(reservations);
        var metadata = new InstrumentTradingMetadata
        {
            Instrument = Phase3TestData.Instrument,
            MinimumQuantity = 1_000m,
            MaximumOrderQuantity = 15_000m,
            QuantityStep = 1_000m,
            PriceIncrement = 0.0001m,
            PipSize = 0.0001m,
            MarginRate = 0.10m,
            PricePrecision = 4,
            QuantityPrecision = 0
        };
        LiveOpportunityEvaluationContext baseline = Phase3TestData.Context(policy, reservations);
        LiveOpportunityEvaluationContext context = baseline with
        {
            InstrumentMetadata = new Dictionary<InstrumentKey, InstrumentTradingMetadata>
            {
                [Phase3TestData.Instrument] = metadata
            },
            RequireInstrumentMetadata = true,
            InstrumentRiskSpecs = new Dictionary<InstrumentKey, RiskManager.InstrumentRiskSpec>
            {
                [Phase3TestData.Instrument] = new RiskManager.InstrumentRiskSpec
                {
                    ContractMultiplier = 1m,
                    PipSize = metadata.PipSize,
                    MarginRate = metadata.MarginRate
                }
            }
        };
        LiveTradeCandidate candidate = Phase3TestData.Candidate() with
        {
            StopLossPrice = 1.09503m,
            TakeProfitPrice = 1.11009m
        };

        PortfolioApprovedDecision approved = (await coordinator.EvaluateEpochAsync(
            [candidate],
            context,
            CancellationToken.None)).Single(item => item.Approved).ApprovedDecision!;

        Assert.Multiple(() =>
        {
            Assert.That(approved.Decision.StopLossPrice, Is.EqualTo(1.0950m));
            Assert.That(approved.Decision.TakeProfitPrice, Is.EqualTo(1.1100m));
            Assert.That(approved.Decision.SuggestedQuantity, Is.LessThanOrEqualTo(15_000m));
            Assert.That(approved.Decision.SuggestedQuantity % 1_000m, Is.EqualTo(0m));
            Assert.That(approved.PositionSizing.EstimatedMargin, Is.GreaterThan(0m));
        });
    }

    [Test]
    public async Task RequiredBrokerMetadataMissing_FailsClosedWithoutReservation()
    {
        LiveTradingPolicyBundle policy = Phase3TestData.Policy();
        var reservations = new PortfolioReservationBook(policy.PortfolioRisk);
        var coordinator = new LiveOpportunityCoordinator(reservations);
        LiveOpportunityEvaluationContext context = Phase3TestData.Context(policy, reservations) with
        {
            RequireInstrumentMetadata = true
        };

        PortfolioDecision result = (await coordinator.EvaluateEpochAsync(
            [Phase3TestData.Candidate()],
            context,
            CancellationToken.None)).Single();

        Assert.Multiple(() =>
        {
            Assert.That(result.Approved, Is.False);
            Assert.That(result.ReasonCode, Is.EqualTo("BrokerInstrumentMetadataUnavailable"));
            Assert.That(reservations.Snapshot.Reservations, Is.Empty);
        });
    }

}
