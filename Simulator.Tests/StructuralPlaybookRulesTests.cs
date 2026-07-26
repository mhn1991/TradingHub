using Agent.Models;
using Agent.Strategies;
using Agent.Strategies.StructuralConfluence.Evidence;
using Agent.Strategies.StructuralConfluence.Playbooks;
using Brokers.Models;
using ChartAnnotator.Models;
using NUnit.Framework;

namespace Simulator.Tests;

[TestFixture]
public sealed class StructuralPlaybookRulesTests
{
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");
    private static readonly DateTimeOffset Now =
        new(2026, 7, 25, 10, 0, 0, TimeSpan.Zero);

    [Test]
    public void RepresentativeCandidate_PrefersProgressThenConfidenceWithoutIgnoringReadySignal()
    {
        PlaybookEvaluation freshCatalyst = Evaluation(
            "fresh", StructuralSetupLifecycle.CatalystObserved, confidence: 90m,
            catalystAt: Now);
        PlaybookEvaluation progressed = Evaluation(
            "progressed", StructuralSetupLifecycle.AwaitingTrigger, confidence: 60m,
            catalystAt: Now.AddMinutes(-10));
        PlaybookEvaluation ready = Evaluation(
            "ready", StructuralSetupLifecycle.CandidateProduced, confidence: 55m,
            catalystAt: Now.AddMinutes(-15), isReady: true);

        PlaybookEvaluation selectedProgressed =
            StructuralPlaybookRules.SelectRepresentativeCandidate([freshCatalyst, progressed]);
        PlaybookEvaluation selectedReady =
            StructuralPlaybookRules.SelectRepresentativeCandidate(
                [freshCatalyst, progressed, ready]);

        Assert.Multiple(() =>
        {
            Assert.That(selectedProgressed.SetupId, Is.EqualTo("progressed"));
            Assert.That(selectedReady.SetupId, Is.EqualTo("ready"));
        });
    }

    [Test]
    public void TriggerProfiles_AdmitOnlyEventsThatConfirmTheirHypothesis()
    {
        Assert.Multiple(() =>
        {
            Assert.That(StructuralPlaybookRules.IsTriggerType(
                StructuralTriggerProfile.BreakRetestContinuation,
                PriceActionEventType.BullishRetestHeld), Is.True);
            Assert.That(StructuralPlaybookRules.IsTriggerType(
                StructuralTriggerProfile.BreakRetestContinuation,
                PriceActionEventType.BullishRejection), Is.False);
            Assert.That(StructuralPlaybookRules.IsTriggerType(
                StructuralTriggerProfile.BreakRetestContinuation,
                PriceActionEventType.BullishDisplacement), Is.True);
            Assert.That(StructuralPlaybookRules.IsTriggerType(
                StructuralTriggerProfile.BreakRetestContinuation,
                PriceActionEventType.BullishChangeOfCharacter), Is.False);

            Assert.That(StructuralPlaybookRules.IsTriggerType(
                StructuralTriggerProfile.SweepReversal,
                PriceActionEventType.BullishChangeOfCharacter), Is.True);
            Assert.That(StructuralPlaybookRules.IsTriggerType(
                StructuralTriggerProfile.SweepReversal,
                PriceActionEventType.BullishRejection), Is.False);

            Assert.That(StructuralPlaybookRules.IsTriggerType(
                StructuralTriggerProfile.TrendPullback,
                PriceActionEventType.BullishRejection), Is.True);
            Assert.That(StructuralPlaybookRules.IsTriggerType(
                StructuralTriggerProfile.TrendPullback,
                PriceActionEventType.BullishDisplacement), Is.False);

            Assert.That(StructuralPlaybookRules.IsTriggerType(
                StructuralTriggerProfile.IndicatorTrendContinuation,
                PriceActionEventType.BullishBreakOfStructure), Is.True);
            Assert.That(StructuralPlaybookRules.IsTriggerType(
                StructuralTriggerProfile.IndicatorTrendContinuation,
                PriceActionEventType.BullishDisplacement), Is.False);
            Assert.That(StructuralPlaybookRules.IsTriggerType(
                StructuralTriggerProfile.IndicatorTrendContinuation,
                PriceActionEventType.BullishChangeOfCharacter), Is.False);

            Assert.That(StructuralPlaybookRules.IsTriggerType(
                StructuralTriggerProfile.IndicatorStructuralInvalidation,
                PriceActionEventType.BearishChangeOfCharacter), Is.True);
            Assert.That(StructuralPlaybookRules.IsTriggerType(
                StructuralTriggerProfile.IndicatorStructuralInvalidation,
                PriceActionEventType.BearishRejection), Is.False);
        });
    }

    private static PlaybookEvaluation Evaluation(
        string setupId,
        StructuralSetupLifecycle lifecycle,
        decimal confidence,
        DateTimeOffset catalystAt,
        bool isReady = false) => new()
    {
        PlaybookId = "test",
        Version = "1",
        Direction = PriceActionDirection.Bullish,
        Lifecycle = lifecycle,
        SetupId = setupId,
        CatalystAt = catalystAt,
        Confidence = confidence,
        ReasonCode = "Test",
        IsReady = isReady
    };

    [Test]
    public void TriggerProfiles_AdmitOnlyMatchingCompositeSetups()
    {
        Assert.Multiple(() =>
        {
            Assert.That(StructuralPlaybookRules.IsSetupType(
                StructuralTriggerProfile.BreakRetestContinuation,
                PriceActionSetupType.BullishBreakRetestHold), Is.True);
            Assert.That(StructuralPlaybookRules.IsSetupType(
                StructuralTriggerProfile.BreakRetestContinuation,
                PriceActionSetupType.BullishSweepChoCh), Is.False);

            Assert.That(StructuralPlaybookRules.IsSetupType(
                StructuralTriggerProfile.SweepReversal,
                PriceActionSetupType.BullishSweepChoCh), Is.True);
            Assert.That(StructuralPlaybookRules.IsSetupType(
                StructuralTriggerProfile.SweepReversal,
                PriceActionSetupType.BullishSweepDisplacement), Is.False);

            Assert.That(StructuralPlaybookRules.IsSetupType(
                StructuralTriggerProfile.TrendPullback,
                PriceActionSetupType.BullishBreakRetestHold), Is.True);

            Assert.That(StructuralPlaybookRules.IsSetupType(
                StructuralTriggerProfile.IndicatorTrendContinuation,
                PriceActionSetupType.BullishBreakRetestHold), Is.True);
            Assert.That(StructuralPlaybookRules.IsSetupType(
                StructuralTriggerProfile.IndicatorTrendContinuation,
                PriceActionSetupType.BullishChoChRetestHold), Is.False);
        });
    }

    [Test]
    public void TriggerAnchor_RequiresPriceActionProvenanceAtSelectedStructure()
    {
        var anchor = new StructuralTriggerAnchor(1.0990m, 1.1010m);
        var matchingEvent = Event(referenceLevel: 1.1000m);
        PriceActionEvent unrelatedEvent = matchingEvent with
        {
            EventId = "unrelated",
            ReferenceLevel = 1.1200m
        };
        var matchingSetup = new PriceActionSetup
        {
            SetupId = "matching",
            Type = PriceActionSetupType.BullishSweepDisplacement,
            Direction = PriceActionDirection.Bullish,
            Phase = PriceActionSetupPhase.Triggered,
            ArmedAt = DateTimeOffset.UnixEpoch,
            TriggeredAt = DateTimeOffset.UnixEpoch.AddMinutes(5),
            ArmedSequence = 1,
            TriggeredSequence = 2,
            Confidence = 70m,
            ReferenceLevel = 1.1005m,
            EntryReference = 1.1150m,
            ReasonCode = "Test",
            Explanation = "Test"
        };

        Assert.Multiple(() =>
        {
            Assert.That(StructuralPlaybookRules.ReferencesAnchor(matchingEvent, anchor), Is.True);
            Assert.That(StructuralPlaybookRules.ReferencesAnchor(unrelatedEvent, anchor), Is.False);
            Assert.That(StructuralPlaybookRules.ReferencesAnchor(matchingSetup, anchor), Is.True);
            Assert.That(StructuralPlaybookRules.ReferencesAnchor(
                matchingSetup with { SetupId = "unrelated-setup", ReferenceLevel = 1.1200m },
                anchor), Is.False);
        });
    }

    [Test]
    public void TriggerAnchor_NormalizesReversedZoneBoundaries()
    {
        var anchor = new StructuralTriggerAnchor(1.1010m, 1.0990m);

        Assert.That(StructuralPlaybookRules.ReferencesAnchor(Event(referenceLevel: 1.1000m), anchor), Is.True);
    }

    [Test]
    public void Trigger_PrefersAnchoredEvidenceOverHigherConfidenceUnrelatedEvent()
    {
        PriceActionEvent anchored = Event(referenceLevel: 1.1000m) with
        {
            EventId = "anchored",
            Type = PriceActionEventType.BullishChangeOfCharacter,
            ConfirmedAt = Now,
            Confidence = 65m
        };
        PriceActionEvent unrelated = anchored with
        {
            EventId = "unrelated",
            ReferenceLevel = 1.1200m,
            Confidence = 95m
        };
        StructuralEvidencePacket evidence = Evidence([unrelated, anchored]);

        (PriceActionEvent? selected, PriceActionSetup? setup, decimal quality) =
            StructuralPlaybookRules.Trigger(
                evidence,
                PriceActionDirection.Bullish,
                StructuralTriggerProfile.SweepReversal,
                minimumConfidence: 50m,
                maximumBars: 3,
                notBefore: Now.AddMinutes(-5),
                anchor: new StructuralTriggerAnchor(1.0990m, 1.1010m));

        Assert.Multiple(() =>
        {
            Assert.That(selected?.EventId, Is.EqualTo("anchored"));
            Assert.That(setup, Is.Null);
            Assert.That(quality, Is.EqualTo(65m));
        });
    }

    [Test]
    public void Trigger_IgnoresFutureEvidenceEvenWhenItHasHigherConfidence()
    {
        PriceActionEvent causal = Event(referenceLevel: 1.1000m) with
        {
            EventId = "causal",
            Type = PriceActionEventType.BullishChangeOfCharacter,
            ConfirmedAt = Now,
            Confidence = 65m
        };
        PriceActionEvent future = causal with
        {
            EventId = "future",
            ConfirmedAt = Now.AddMinutes(5),
            Confidence = 95m
        };

        (PriceActionEvent? selected, _, _) = StructuralPlaybookRules.Trigger(
            Evidence([future, causal]),
            PriceActionDirection.Bullish,
            StructuralTriggerProfile.SweepReversal,
            minimumConfidence: 50m,
            maximumBars: 3,
            notBefore: Now.AddMinutes(-5),
            anchor: new StructuralTriggerAnchor(1.0990m, 1.1010m));

        Assert.That(selected?.EventId, Is.EqualTo("causal"));
    }

    [Test]
    public void Trigger_RejectsFreshEvidenceOutsideCatalystResponseWindow()
    {
        PriceActionEvent late = Event(referenceLevel: 1.1000m) with
        {
            EventId = "late",
            Type = PriceActionEventType.BullishChangeOfCharacter,
            ConfirmedAt = Now,
            Confidence = 80m
        };

        (PriceActionEvent? selected, PriceActionSetup? setup, decimal quality) =
            StructuralPlaybookRules.Trigger(
                Evidence([late]),
                PriceActionDirection.Bullish,
                StructuralTriggerProfile.SweepReversal,
                minimumConfidence: 50m,
                maximumBars: 3,
                notBefore: Now.AddMinutes(-20));

        Assert.Multiple(() =>
        {
            Assert.That(selected, Is.Null);
            Assert.That(setup, Is.Null);
            Assert.That(quality, Is.Zero);
        });
    }

    [TestCase(CciRelationshipType.HiddenBullishDivergence, true, EvidenceAlignment.Aligned)]
    [TestCase(CciRelationshipType.RegularBullishDivergence, true, EvidenceAlignment.Neutral)]
    [TestCase(CciRelationshipType.HiddenBullishDivergence, false, EvidenceAlignment.Neutral)]
    [TestCase(CciRelationshipType.RegularBullishDivergence, false, EvidenceAlignment.Aligned)]
    [TestCase(CciRelationshipType.BullishConvergence, true, EvidenceAlignment.Aligned)]
    [TestCase(CciRelationshipType.BullishConvergence, false, EvidenceAlignment.Aligned)]
    [TestCase(CciRelationshipType.RegularBearishDivergence, true, EvidenceAlignment.Conflicting)]
    public void CciRelationships_AreClassifiedByContinuationOrReversalHypothesis(
        CciRelationshipType type,
        bool continuation,
        EvidenceAlignment expected)
    {
        EvidenceAlignment actual = StructuralPlaybookRules.ClassifyCciRelationship(
            type, PriceActionDirection.Bullish, continuation);

        Assert.That(actual, Is.EqualTo(expected));
    }

    [TestCase(RsiRelationshipType.HiddenBullishDivergence, true, EvidenceAlignment.Aligned)]
    [TestCase(RsiRelationshipType.RegularBullishDivergence, true, EvidenceAlignment.Neutral)]
    [TestCase(RsiRelationshipType.HiddenBullishDivergence, false, EvidenceAlignment.Neutral)]
    [TestCase(RsiRelationshipType.RegularBullishDivergence, false, EvidenceAlignment.Aligned)]
    [TestCase(RsiRelationshipType.BullishConvergence, true, EvidenceAlignment.Aligned)]
    [TestCase(RsiRelationshipType.BullishConvergence, false, EvidenceAlignment.Aligned)]
    [TestCase(RsiRelationshipType.RegularBearishDivergence, false, EvidenceAlignment.Conflicting)]
    public void RsiRelationships_AreClassifiedByContinuationOrReversalHypothesis(
        RsiRelationshipType type,
        bool continuation,
        EvidenceAlignment expected)
    {
        EvidenceAlignment actual = StructuralPlaybookRules.ClassifyRsiRelationship(
            type, PriceActionDirection.Bullish, continuation);

        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void Confidence_SoftEvidenceDoesNotBecomeAHiddenMandatoryFloor()
    {
        MandatoryGate[] gates =
        [
            new("Structure", true, 65m, "StructurePassed"),
            new("SoftCci", true, 0m, "SoftCciUnavailable", LimitsConfidenceFloor: false)
        ];

        decimal confidence = StructuralPlaybookRules.Confidence(
            gates, contextAdjustment: 0m, confirmationAdjustment: 0m, confluenceAdjustment: 0m);
        var evaluation = new PlaybookEvaluation
        {
            PlaybookId = "test",
            Version = "1",
            Direction = PriceActionDirection.Bullish,
            Lifecycle = StructuralSetupLifecycle.CandidateProduced,
            MandatoryGates = gates,
            ReasonCode = "Test"
        };

        Assert.Multiple(() =>
        {
            Assert.That(confidence, Is.EqualTo(65m));
            Assert.That(evaluation.MandatoryQualityFloor, Is.EqualTo(65m));
        });
    }

    [Test]
    public void Confidence_FailedOptionalConditionStillRejectsCandidate()
    {
        MandatoryGate[] gates =
        [
            new("Structure", true, 65m, "StructurePassed"),
            new("ConfiguredRequirement", false, 0m, "RequirementFailed",
                LimitsConfidenceFloor: false)
        ];

        decimal confidence = StructuralPlaybookRules.Confidence(
            gates, contextAdjustment: 0m, confirmationAdjustment: 0m, confluenceAdjustment: 0m);

        Assert.That(confidence, Is.Zero);
    }

    [Test]
    public void LocalTrendOpposition_RequiresThreeIndependentOpposingFeatureFamilies()
    {
        var opposingDmi = new AdxAnalysisSnapshot
        {
            Adx = 28m,
            DirectionalBias = PriceActionDirection.Bearish,
            IsTrendStrengthening = true
        };
        var cciConflict = new CciAssessment(
            EvidenceAlignment.Conflicting, 20m, "Conflicting:Momentum");
        var bollingerConflict = new RsiBollingerSignalAssessment
        {
            BollingerExpansionOpposes = true,
            Explanation = "Test"
        };

        LocalTrendOppositionAssessment coherent =
            StructuralPlaybookRules.AssessLocalTrendOpposition(
                PriceActionDirection.Bullish, opposingDmi, cciConflict, bollingerConflict);
        LocalTrendOppositionAssessment weakeningDmi =
            StructuralPlaybookRules.AssessLocalTrendOpposition(
                PriceActionDirection.Bullish,
                opposingDmi with { IsTrendStrengthening = false },
                cciConflict,
                bollingerConflict);
        LocalTrendOppositionAssessment neutralCci =
            StructuralPlaybookRules.AssessLocalTrendOpposition(
                PriceActionDirection.Bullish,
                opposingDmi,
                cciConflict with { Alignment = EvidenceAlignment.Neutral },
                bollingerConflict);
        LocalTrendOppositionAssessment stableBands =
            StructuralPlaybookRules.AssessLocalTrendOpposition(
                PriceActionDirection.Bullish,
                opposingDmi,
                cciConflict,
                bollingerConflict with { BollingerExpansionOpposes = false });

        Assert.Multiple(() =>
        {
            Assert.That(coherent.IsCoherent, Is.True);
            Assert.That(weakeningDmi.IsCoherent, Is.False);
            Assert.That(neutralCci.IsCoherent, Is.False);
            Assert.That(stableBands.IsCoherent, Is.False);
        });
    }

    private static PriceActionEvent Event(decimal? referenceLevel) => new()
    {
        EventId = "event",
        Type = PriceActionEventType.BullishRejection,
        Direction = PriceActionDirection.Bullish,
        ConfirmedAt = DateTimeOffset.UnixEpoch,
        ConfirmedSequence = 1,
        ReferenceLevel = referenceLevel,
        Strength = 70m,
        Confidence = 70m,
        ReasonCode = "Test",
        Explanation = "Test"
    };

    private static StructuralEvidencePacket Evidence(IReadOnlyList<PriceActionEvent> events)
    {
        IndicatorSnapshot indicators = new();
        AnalysisSnapshot context = Analysis(BarInterval.Hours(1), indicators);
        AnalysisSnapshot setup = Analysis(BarInterval.Minutes(15), indicators);
        AnalysisSnapshot trigger = Analysis(BarInterval.Minutes(5), indicators);
        return new StructuralEvidencePacket
        {
            Instrument = Instrument,
            AvailableAt = Now,
            Context = context,
            Setup = setup,
            Trigger = trigger,
            AdditionalContexts = [],
            ContextEvidence = new StructuralContextEvidence(
                EvidenceAlignment.Neutral, EvidenceAlignment.Neutral, 50m, 50m),
            SupplyDemand = new SupplyDemandPacket([], []),
            Liquidity = new LiquidityPacket([], [], []),
            TriggerEvidence = new TriggerPacket(events, []),
            Indicators = new IndicatorConfirmationPacket(
                null, null, CciAnalysisSnapshot.Empty, null, RsiAnalysisSnapshot.Empty,
                StochRsiSnapshot.Empty, BollingerAnalysisSnapshot.Empty, AdxAnalysisSnapshot.Empty, null)
        };
    }

    private static AnalysisSnapshot Analysis(BarInterval interval, IndicatorSnapshot indicators) => new()
    {
        Instrument = Instrument,
        Interval = interval,
        AvailableAt = Now,
        Version = 1,
        LatestCandle = TestCandles.Create(
            Instrument,
            Now - TimeSpan.FromSeconds(BarIntervalParser.ApproximateSeconds(interval)),
            interval, 1.1000m, 1.1010m, 1.0990m, 1.1005m),
        Indicators = indicators,
        Swings = [],
        PriceZones = [],
        Trendlines = [],
        Channels = [],
        Confidence = new ConfidenceScore { Total = 50m, Contributions = [] }
    };
}
