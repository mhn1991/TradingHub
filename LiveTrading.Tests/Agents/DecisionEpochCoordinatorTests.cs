using Agent.Models;
using Brokers.Models;
using ChartAnnotator.Regime;
using LiveTrading.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace LiveTrading.Tests.Agents;

[TestFixture]
public sealed class DecisionEpochCoordinatorTests
{
    private static readonly InstrumentKey Eur = new("FX:EUR/USD");
    private static readonly InstrumentKey Gbp = new("FX:GBP/USD");

    private static LiveTradeCandidate Candidate(
        InstrumentKey instrument, string strategyId, string decisionId, long epoch, DateTimeOffset decisionTime,
        long marketSequence = 0) => new()
    {
        CandidateId = Guid.NewGuid().ToString("N"),
        DecisionId = decisionId,
        StrategyId = strategyId,
        Instrument = instrument,
        Action = AgentAction.Buy,
        DecisionTime = decisionTime,
        DecisionEpoch = epoch,
        MarketSequence = marketSequence,
        RawConfidence = 0.7m,
        MultiTimeframeAlignment = 1m,
        EntryRegime = MarketRegime.TrendingUp,
        SetupCalibration = SetupCalibrationAudit.Disabled,
        MetaLabel = MetaLabelAudit.Disabled,
        TradingCondition = null
    };

    [Test]
    public async Task AllInstrumentsReported_ClosesEarly_BeforeBarrierTimeout()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var coordinator = new LiveDecisionEpochCoordinator(
            [Eur, Gbp], clock, new LiveDecisionEpochCoordinatorOptions { BarrierTimeout = TimeSpan.FromSeconds(30) },
            NullLogger<LiveDecisionEpochCoordinator>.Instance);

        DateTimeOffset closeTime = clock.GetUtcNow();
        long epoch = LiveDecisionEpochCoordinator.EpochFor(closeTime);

        coordinator.SubmitCandidate(Candidate(Eur, "strategy-1", "decision-eur", epoch, closeTime));
        coordinator.NotifyEvaluated(epoch, Eur);
        coordinator.NotifyEvaluated(epoch, Gbp);

        LiveDecisionEpochBatch batch = await coordinator.ClosedEpochs.ReadAsync();

        Assert.Multiple(() =>
        {
            Assert.That(batch.Epoch, Is.EqualTo(epoch));
            Assert.That(batch.OrderedCandidates, Has.Count.EqualTo(1));
            Assert.That(batch.UnavailableInstruments, Is.Empty);
        });
    }

    [Test]
    public async Task MissingInstrument_ClosesAtBarrierTimeout_AndAppearsUnavailable()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var options = new LiveDecisionEpochCoordinatorOptions { BarrierTimeout = TimeSpan.FromSeconds(5) };
        var coordinator = new LiveDecisionEpochCoordinator(
            [Eur, Gbp], clock, options, NullLogger<LiveDecisionEpochCoordinator>.Instance);

        DateTimeOffset closeTime = clock.GetUtcNow();
        long epoch = LiveDecisionEpochCoordinator.EpochFor(closeTime);
        coordinator.SubmitCandidate(Candidate(Eur, "strategy-1", "decision-eur", epoch, closeTime));
        coordinator.NotifyEvaluated(epoch, Eur);

        ValueTask<LiveDecisionEpochBatch> pending = coordinator.ClosedEpochs.ReadAsync();
        clock.Advance(options.BarrierTimeout + TimeSpan.FromSeconds(1));
        LiveDecisionEpochBatch batch = await pending;

        Assert.Multiple(() =>
        {
            Assert.That(batch.OrderedCandidates, Has.Count.EqualTo(1));
            Assert.That(batch.UnavailableInstruments, Is.EqualTo(new[] { Gbp }));
        });
    }

    [Test]
    public async Task OrderedCandidates_SortByInstrumentThenStrategyThenDecisionId()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var coordinator = new LiveDecisionEpochCoordinator(
            [Eur, Gbp], clock, new LiveDecisionEpochCoordinatorOptions { BarrierTimeout = TimeSpan.FromSeconds(30) },
            NullLogger<LiveDecisionEpochCoordinator>.Instance);

        DateTimeOffset closeTime = clock.GetUtcNow();
        long epoch = LiveDecisionEpochCoordinator.EpochFor(closeTime);

        // Submitted out of the expected final order on purpose.
        coordinator.SubmitCandidate(Candidate(Gbp, "strategy-b", "decision-3", epoch, closeTime));
        coordinator.SubmitCandidate(Candidate(Eur, "strategy-b", "decision-2", epoch, closeTime));
        coordinator.SubmitCandidate(Candidate(Eur, "strategy-a", "decision-1", epoch, closeTime));
        coordinator.NotifyEvaluated(epoch, Eur);
        coordinator.NotifyEvaluated(epoch, Gbp);

        LiveDecisionEpochBatch batch = await coordinator.ClosedEpochs.ReadAsync();

        Assert.That(
            batch.OrderedCandidates.Select(c => c.DecisionId),
            Is.EqualTo(new[] { "decision-1", "decision-2", "decision-3" }));
    }

    [Test]
    public async Task CandidateSubmission_DoesNotReportInstrumentUntilAllItsAgentsFinish()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var coordinator = new LiveDecisionEpochCoordinator(
            [Eur], clock, new LiveDecisionEpochCoordinatorOptions { BarrierTimeout = TimeSpan.FromSeconds(30) },
            NullLogger<LiveDecisionEpochCoordinator>.Instance);

        DateTimeOffset closeTime = clock.GetUtcNow();
        long epoch = LiveDecisionEpochCoordinator.EpochFor(closeTime);
        coordinator.SubmitCandidate(Candidate(Eur, "strategy-a", "decision-1", epoch, closeTime));
        coordinator.SubmitCandidate(Candidate(Eur, "strategy-b", "decision-2", epoch, closeTime));

        Assert.That(coordinator.ClosedEpochs.TryRead(out _), Is.False);
        coordinator.NotifyEvaluated(epoch, Eur);
        LiveDecisionEpochBatch batch = await coordinator.ClosedEpochs.ReadAsync();

        Assert.That(batch.OrderedCandidates.Select(c => c.DecisionId),
            Is.EqualTo(new[] { "decision-1", "decision-2" }));
    }

    [Test]
    public async Task LateCandidate_CannotReopenAClosedEpoch()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var coordinator = new LiveDecisionEpochCoordinator(
            [Eur], clock, new LiveDecisionEpochCoordinatorOptions { BarrierTimeout = TimeSpan.FromSeconds(30) },
            NullLogger<LiveDecisionEpochCoordinator>.Instance);

        DateTimeOffset closeTime = clock.GetUtcNow();
        long epoch = LiveDecisionEpochCoordinator.EpochFor(closeTime);
        coordinator.SubmitCandidate(Candidate(Eur, "strategy-a", "decision-1", epoch, closeTime));
        coordinator.NotifyEvaluated(epoch, Eur);
        LiveDecisionEpochBatch first = await coordinator.ClosedEpochs.ReadAsync();

        coordinator.SubmitCandidate(Candidate(Eur, "strategy-b", "late-decision", epoch, closeTime));
        coordinator.NotifyEvaluated(epoch, Eur);

        Assert.Multiple(() =>
        {
            Assert.That(first.OrderedCandidates.Select(c => c.DecisionId), Is.EqualTo(new[] { "decision-1" }));
            Assert.That(coordinator.ClosedEpochs.TryRead(out _), Is.False);
        });
    }

    [Test]
    public async Task DistinctCloseTimes_NeverMergeIntoTheSameEpoch()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var coordinator = new LiveDecisionEpochCoordinator(
            [Eur], clock, new LiveDecisionEpochCoordinatorOptions { BarrierTimeout = TimeSpan.FromSeconds(30) },
            NullLogger<LiveDecisionEpochCoordinator>.Instance);

        DateTimeOffset first = clock.GetUtcNow();
        DateTimeOffset second = first.AddMinutes(1);
        long firstEpoch = LiveDecisionEpochCoordinator.EpochFor(first);
        long secondEpoch = LiveDecisionEpochCoordinator.EpochFor(second);

        coordinator.SubmitCandidate(Candidate(Eur, "strategy-1", "decision-1", firstEpoch, first));
        coordinator.NotifyEvaluated(firstEpoch, Eur);
        coordinator.SubmitCandidate(Candidate(Eur, "strategy-1", "decision-2", secondEpoch, second));
        coordinator.NotifyEvaluated(secondEpoch, Eur);

        LiveDecisionEpochBatch firstBatch = await coordinator.ClosedEpochs.ReadAsync();
        LiveDecisionEpochBatch secondBatch = await coordinator.ClosedEpochs.ReadAsync();

        Assert.Multiple(() =>
        {
            Assert.That(firstBatch.Epoch, Is.Not.EqualTo(secondBatch.Epoch));
            Assert.That(firstBatch.OrderedCandidates.Single().DecisionId, Is.EqualTo("decision-1"));
            Assert.That(secondBatch.OrderedCandidates.Single().DecisionId, Is.EqualTo("decision-2"));
        });
    }

    [Test]
    public void UnexpectedInstrument_CannotSatisfyTheEpochBarrier()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var coordinator = new LiveDecisionEpochCoordinator(
            [Eur], clock, new LiveDecisionEpochCoordinatorOptions { BarrierTimeout = TimeSpan.FromSeconds(30) },
            NullLogger<LiveDecisionEpochCoordinator>.Instance);
        DateTimeOffset closeTime = clock.GetUtcNow();
        long epoch = LiveDecisionEpochCoordinator.EpochFor(closeTime);
        coordinator.SubmitCandidate(Candidate(Eur, "strategy-a", "decision-1", epoch, closeTime));

        coordinator.NotifyEvaluated(epoch, Gbp);

        Assert.That(coordinator.ClosedEpochs.TryRead(out _), Is.False);
    }

    [Test]
    public async Task SubmitCandidate_ExactDuplicateDecision_IsRejectedAndCounted()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var coordinator = new LiveDecisionEpochCoordinator(
            [Eur], clock, new LiveDecisionEpochCoordinatorOptions { BarrierTimeout = TimeSpan.FromSeconds(30) },
            NullLogger<LiveDecisionEpochCoordinator>.Instance);
        DateTimeOffset closeTime = clock.GetUtcNow();
        long epoch = LiveDecisionEpochCoordinator.EpochFor(closeTime);

        coordinator.SubmitCandidate(Candidate(Eur, "strategy-1", "decision-1", epoch, closeTime));
        // Exact duplicate: same instrument/strategy/decision - must be rejected, not double-counted.
        coordinator.SubmitCandidate(Candidate(Eur, "strategy-1", "decision-1", epoch, closeTime));
        coordinator.NotifyEvaluated(epoch, Eur);

        LiveDecisionEpochBatch batch = await coordinator.ClosedEpochs.ReadAsync();

        Assert.Multiple(() =>
        {
            Assert.That(batch.OrderedCandidates, Has.Count.EqualTo(1));
            Assert.That(batch.RejectedDuplicateCandidateCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task SubmitCandidate_StaleMarketSequence_IsRejectedAndCounted()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var coordinator = new LiveDecisionEpochCoordinator(
            [Eur], clock, new LiveDecisionEpochCoordinatorOptions { BarrierTimeout = TimeSpan.FromSeconds(30) },
            NullLogger<LiveDecisionEpochCoordinator>.Instance);
        DateTimeOffset closeTime = clock.GetUtcNow();
        long epoch = LiveDecisionEpochCoordinator.EpochFor(closeTime);

        // Opens the epoch group under sequence 1.
        coordinator.SubmitCandidate(Candidate(Eur, "strategy-1", "decision-1", epoch, closeTime, marketSequence: 1));
        // A newer market update for the same instrument has already begun.
        coordinator.NotifyMarketSequence(Eur, 2);
        // A late-arriving candidate still computed under the now-stale sequence must be discarded.
        coordinator.SubmitCandidate(Candidate(Eur, "strategy-2", "decision-2", epoch, closeTime, marketSequence: 1));
        coordinator.NotifyEvaluated(epoch, Eur);

        LiveDecisionEpochBatch batch = await coordinator.ClosedEpochs.ReadAsync();

        Assert.Multiple(() =>
        {
            Assert.That(batch.OrderedCandidates, Has.Count.EqualTo(1));
            Assert.That(batch.OrderedCandidates.Single().DecisionId, Is.EqualTo("decision-1"));
            Assert.That(batch.RejectedStaleCandidateCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task SubmitCandidate_SequenceAtOrAboveLatest_IsAccepted()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var coordinator = new LiveDecisionEpochCoordinator(
            [Eur], clock, new LiveDecisionEpochCoordinatorOptions { BarrierTimeout = TimeSpan.FromSeconds(30) },
            NullLogger<LiveDecisionEpochCoordinator>.Instance);
        DateTimeOffset closeTime = clock.GetUtcNow();
        long epoch = LiveDecisionEpochCoordinator.EpochFor(closeTime);

        coordinator.NotifyMarketSequence(Eur, 5);
        coordinator.SubmitCandidate(Candidate(Eur, "strategy-1", "decision-1", epoch, closeTime, marketSequence: 5));
        coordinator.NotifyEvaluated(epoch, Eur);

        LiveDecisionEpochBatch batch = await coordinator.ClosedEpochs.ReadAsync();

        Assert.Multiple(() =>
        {
            Assert.That(batch.OrderedCandidates, Has.Count.EqualTo(1));
            Assert.That(batch.RejectedStaleCandidateCount, Is.EqualTo(0));
        });
    }
}
