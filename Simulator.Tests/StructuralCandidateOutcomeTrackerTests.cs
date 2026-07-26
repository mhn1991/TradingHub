using Agent.Models;
using ChartAnnotator.Models;
using Simulator.Models;
using Simulator.Replay;

namespace Simulator.Tests;

[TestFixture]
public sealed class StructuralCandidateOutcomeTrackerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 25, 10, 0, 0, TimeSpan.Zero);

    [Test]
    public void TracksForwardExcursionWithoutUsingTheCatalystCandleOrResettingThePath()
    {
        var tracker = new StructuralCandidateOutcomeTracker(TimeSpan.FromMinutes(60));
        tracker.Record(Event(
            StructuralSetupLifecycle.Armed,
            PriceActionDirection.Bullish,
            setupId: "armed"), 100m);
        tracker.Record(Event(
            StructuralSetupLifecycle.CatalystObserved,
            PriceActionDirection.Bullish,
            setupId: "candidate",
            blocker: "TriggerMissing"), 100m);

        tracker.Advance(Now.AddMinutes(5), high: 102m, low: 99m, close: 101m);
        tracker.Record(Event(
            StructuralSetupLifecycle.AwaitingTrigger,
            PriceActionDirection.Bullish,
            setupId: "candidate",
            blocker: "ConfirmationMissing"), 110m);
        tracker.Record(Event(
            StructuralSetupLifecycle.CandidateProduced,
            PriceActionDirection.Bullish,
            setupId: "candidate",
            isReady: true,
            isSelected: true), 110m);
        tracker.Advance(Now.AddMinutes(61), high: 120m, low: 80m, close: 90m);

        StructuralCandidateOutcome outcome = tracker.BuildOutcomes().Single();

        Assert.Multiple(() =>
        {
            Assert.That(outcome.SetupId, Is.EqualTo("candidate"));
            Assert.That(outcome.ReferencePrice, Is.EqualTo(100m));
            Assert.That(outcome.MaximumFavourableExcursionBps, Is.EqualTo(200m));
            Assert.That(outcome.MaximumAdverseExcursionBps, Is.EqualTo(-100m));
            Assert.That(outcome.ClosingReturnBps, Is.EqualTo(100m));
            Assert.That(outcome.HorizonComplete, Is.True);
            Assert.That(outcome.EventuallyReady, Is.True);
            Assert.That(outcome.EventuallySelected, Is.True);
            Assert.That(outcome.FirstBlockingReasonCode, Is.EqualTo("TriggerMissing"));
            Assert.That(outcome.LastBlockingReasonCode, Is.EqualTo("ConfirmationMissing"));
            Assert.That(outcome.FailedGateReasonCodes,
                Is.EqualTo(new[] { "ConfirmationMissing", "TriggerMissing" }));
        });
    }

    [Test]
    public void NormalizesBearishExcursionIntoThePredictedDirection()
    {
        var tracker = new StructuralCandidateOutcomeTracker(TimeSpan.FromMinutes(15));
        tracker.Record(Event(
            StructuralSetupLifecycle.CatalystObserved,
            PriceActionDirection.Bearish,
            setupId: "bearish"), 200m);

        tracker.Advance(Now.AddMinutes(15), high: 202m, low: 194m, close: 196m);

        StructuralCandidateOutcome outcome = tracker.BuildOutcomes().Single();
        Assert.Multiple(() =>
        {
            Assert.That(outcome.MaximumFavourableExcursionBps, Is.EqualTo(300m));
            Assert.That(outcome.MaximumAdverseExcursionBps, Is.EqualTo(-100m));
            Assert.That(outcome.ClosingReturnBps, Is.EqualTo(200m));
            Assert.That(outcome.HorizonComplete, Is.True);
        });
    }

    private static StrategyReplayEvent Event(
        StructuralSetupLifecycle lifecycle,
        PriceActionDirection direction,
        string setupId,
        string? blocker = null,
        bool isReady = false,
        bool isSelected = false) => new()
    {
        Type = StrategyReplayEventType.StructuralPlaybookEvaluated,
        StrategyId = "structural-confluence",
        Sequence = 1,
        EventTime = Now,
        PlaybookId = "test-playbook",
        EvaluationSetupId = setupId,
        StructuralDirection = direction,
        StructuralLifecycle = lifecycle,
        IsReady = isReady,
        IsSelected = isSelected,
        PrimaryBlockingReasonCode = blocker,
        FailedGateReasonCodes = blocker is null ? [] : [blocker]
    };
}
