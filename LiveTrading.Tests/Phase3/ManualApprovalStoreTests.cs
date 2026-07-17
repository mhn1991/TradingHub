using LiveTrading.ManualApproval;
using LiveTrading.Portfolio;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using PortfolioManager.Risk;

namespace LiveTrading.Tests.Phase3;

[TestFixture]
public sealed class ManualApprovalStoreTests
{
    [Test]
    public void Approval_IsFingerprintBoundAndExactlyOnceConsumable()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero));
        var store = new ManualApprovalStore(clock, new PortfolioReservationBook(Phase3TestData.Policy().PortfolioRisk));
        ManualApprovalCandidate item = store.Add(Phase3TestData.ApprovedDecision(), TimeSpan.FromMinutes(2));

        store.Approve(new ManualApprovalRequest
        {
            CandidateId = item.Decision.Candidate.CandidateId,
            CandidateFingerprint = item.CandidateFingerprint,
            ApprovedBy = "operator"
        });

        Assert.That(store.TryConsume(item.Decision.Candidate.CandidateId, item.CandidateFingerprint, out _), Is.True);
        Assert.That(store.TryConsume(item.Decision.Candidate.CandidateId, item.CandidateFingerprint, out _), Is.False);
    }

    [Test]
    public void ApprovedCandidate_StillExpiresBeforeConsumption()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero));
        var store = new ManualApprovalStore(clock, new PortfolioReservationBook(Phase3TestData.Policy().PortfolioRisk));
        ManualApprovalCandidate item = store.Add(Phase3TestData.ApprovedDecision(), TimeSpan.FromSeconds(30));
        store.Approve(new ManualApprovalRequest
        {
            CandidateId = item.Decision.Candidate.CandidateId,
            CandidateFingerprint = item.CandidateFingerprint,
            ApprovedBy = "operator"
        });

        clock.Advance(TimeSpan.FromSeconds(31));

        Assert.That(store.TryConsume(item.Decision.Candidate.CandidateId, item.CandidateFingerprint, out _), Is.False);
        Assert.That(store.Snapshot.Single().State, Is.EqualTo(ManualApprovalState.Expired));
    }

    [Test]
    public void WrongFingerprint_CannotApproveCandidate()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero));
        var store = new ManualApprovalStore(clock, new PortfolioReservationBook(Phase3TestData.Policy().PortfolioRisk));
        ManualApprovalCandidate item = store.Add(Phase3TestData.ApprovedDecision(), TimeSpan.FromMinutes(1));

        Assert.Throws<InvalidOperationException>(() => store.Approve(new ManualApprovalRequest
        {
            CandidateId = item.Decision.Candidate.CandidateId,
            CandidateFingerprint = "wrong",
            ApprovedBy = "operator"
        }));
    }

    [Test]
    public void Expiry_ReleasesTheActivePortfolioReservation()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero));
        LiveTradingPolicyBundle policy = Phase3TestData.Policy();
        var reservations = new PortfolioReservationBook(policy.PortfolioRisk);
        PortfolioApprovedDecision approved = Phase3TestData.ApprovedDecision();
        PortfolioReservationResult reserved = reservations.TryReserve(new PortfolioReservationRequest
        {
            Reservation = new PortfolioReservation
            {
                ReservationId = approved.ReservationId,
                StrategyId = approved.Candidate.StrategyId,
                DecisionId = approved.Candidate.DecisionId,
                Instrument = approved.Candidate.Instrument,
                Quantity = approved.Decision.SuggestedQuantity!.Value,
                PlannedStopRiskAccountCurrency = approved.PositionSizing.EstimatedStopLoss,
                EstimatedMargin = approved.PositionSizing.EstimatedMargin,
                CurrencyExposureDelta = new Dictionary<string, decimal>
                {
                    ["EUR"] = approved.PositionSizing.EstimatedStopLoss,
                    ["USD"] = -approved.PositionSizing.EstimatedStopLoss
                },
                CorrelationClusterId = "test",
                CreatedAt = clock.GetUtcNow(),
                CreatedSequence = 1
            },
            AccountEquity = 100_000m
        });
        Assert.That(reserved.Approved, Is.True, reserved.Explanation);
        var store = new ManualApprovalStore(clock, reservations);
        ManualApprovalCandidate item = store.Add(approved, TimeSpan.FromSeconds(30));

        clock.Advance(TimeSpan.FromSeconds(31));
        _ = store.Snapshot;

        Assert.Multiple(() =>
        {
            Assert.That(store.Snapshot.Single().State, Is.EqualTo(ManualApprovalState.Expired));
            Assert.That(reservations.Snapshot.Reservations, Is.Empty);
        });
    }
}
