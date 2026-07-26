using Agent.Strategies.StructuralConfluence;
using Agent.Strategies.StructuralConfluence.Evidence;
using Agent.Strategies.StructuralConfluence.Playbooks;
using Brokers.Models;
using ChartAnnotator.Models;
using NUnit.Framework;

namespace Simulator.Tests;

[TestFixture]
public sealed class IndicatorConfluencePlaybookTests
{
    private static readonly InstrumentKey Instrument = new("FX:GBP/USD");
    private static readonly DateTimeOffset Now =
        new(2026, 7, 25, 10, 0, 0, TimeSpan.Zero);

    [Test]
    public void HiddenDivergence_WithCurrentMomentumAndConfirmedExpansion_ProducesContinuationCandidate()
    {
        PlaybookEvaluation result = Playbook().Evaluate(
            Evidence(Relationship(RsiRelationshipType.HiddenBullishDivergence)),
            new PlaybookRuntimeState());

        Assert.Multiple(() =>
        {
            Assert.That(result.IsReady, Is.True);
            Assert.That(result.Lifecycle, Is.EqualTo(Agent.Models.StructuralSetupLifecycle.CandidateProduced));
            Assert.That(result.Direction, Is.EqualTo(PriceActionDirection.Bullish));
            Assert.That(result.CatalystAt, Is.EqualTo(Now.AddMinutes(-5)));
            Assert.That(result.SupportingEvidence,
                Does.Contain("StructuralIndicatorRsiHiddenBullishDivergence"));
            Assert.That(result.SupportingEvidence,
                Does.Contain("StructuralIndicatorPriceActionBullishBreakOfStructure"));
            Assert.That(result.SupportingEvidence,
                Does.Contain("StructuralIndicatorBollingerConfirmedExpansionAligned"));
        });
    }

    [Test]
    public void StructuralResumption_DoesNotWaitForLaggingBollingerExpansion()
    {
        PlaybookEvaluation result = Playbook().Evaluate(
            Evidence(
                Relationship(RsiRelationshipType.HiddenBullishDivergence),
                isExpansion: false),
            new PlaybookRuntimeState());

        Assert.Multiple(() =>
        {
            Assert.That(result.IsReady, Is.True);
            Assert.That(result.Lifecycle,
                Is.EqualTo(Agent.Models.StructuralSetupLifecycle.CandidateProduced));
            Assert.That(result.TriggerEvent?.Type,
                Is.EqualTo(PriceActionEventType.BullishBreakOfStructure));
            Assert.That(result.MandatoryGates.Select(gate => gate.Name),
                Does.Not.Contain("ConfirmedExpansion"));
            Assert.That(result.SupportingEvidence,
                Does.Contain("StructuralIndicatorBollingerPositionAligned"));
            Assert.That(result.SupportingEvidence,
                Does.Not.Contain("StructuralIndicatorBollingerConfirmedExpansionAligned"));
            Assert.That(result.ConfirmationQuality, Is.Zero);
        });
    }

    [Test]
    public void Convergence_WithoutHiddenDivergence_DoesNotArmContinuation()
    {
        PlaybookEvaluation result = Playbook().Evaluate(
            Evidence(Relationship(RsiRelationshipType.BullishConvergence)),
            new PlaybookRuntimeState());

        Assert.Multiple(() =>
        {
            Assert.That(result.IsReady, Is.False);
            Assert.That(result.Lifecycle, Is.EqualTo(Agent.Models.StructuralSetupLifecycle.Armed));
            Assert.That(result.ReasonCode,
                Is.EqualTo("StructuralIndicatorContinuationSignalMissing"));
        });
    }

    [Test]
    public void FreshSetupWithoutCatalyst_IsNotMisreportedAsAlreadySignaled()
    {
        PlaybookEvaluation result = Playbook().Evaluate(
            Evidence(null, includeAlignedTrigger: false),
            new PlaybookRuntimeState());

        Assert.Multiple(() =>
        {
            Assert.That(result.IsReady, Is.False);
            Assert.That(result.ReasonCode,
                Is.EqualTo("StructuralIndicatorContinuationSignalMissing"));
            Assert.That(
                result.MandatoryGates.Single(gate => gate.Name == "NotAlreadySignaled").Passed,
                Is.True);
        });
    }

    [Test]
    public void Convergence_CanConfirmPersistedHiddenDivergenceWithoutReplacingItsIdentity()
    {
        IndicatorConfluencePlaybook playbook = Playbook();
        var store = new PlaybookStateStore();
        RsiRelationshipSnapshot hidden =
            Relationship(RsiRelationshipType.HiddenBullishDivergence);

        PlaybookEvaluation catalyst = playbook.Evaluate(
            Evidence(hidden, includeAlignedTrigger: false),
            store.Get(Instrument, playbook.PlaybookId));
        store.TryAdvance(Instrument, playbook.PlaybookId, Now, 1, catalyst);

        DateTimeOffset resumedAt = Now.AddMinutes(5);
        PlaybookEvaluation resumed = playbook.Evaluate(
            Evidence(
                Relationship(
                    RsiRelationshipType.BullishConvergence,
                    confirmedAt: resumedAt,
                    secondPivotAt: Now),
                availableAt: resumedAt),
            store.Get(Instrument, playbook.PlaybookId));

        Assert.Multiple(() =>
        {
            Assert.That(catalyst.Lifecycle,
                Is.EqualTo(Agent.Models.StructuralSetupLifecycle.CatalystObserved));
            Assert.That(resumed.IsReady, Is.True);
            Assert.That(resumed.CatalystIdentity, Is.EqualTo(catalyst.CatalystIdentity));
            Assert.That(resumed.CatalystAt, Is.EqualTo(catalyst.CatalystAt));
        });
    }

    [Test]
    public void RegularDivergence_DoesNotTriggerTrendContinuation()
    {
        PlaybookEvaluation result = Playbook().Evaluate(
            Evidence(Relationship(RsiRelationshipType.RegularBullishDivergence)),
            new PlaybookRuntimeState());

        Assert.Multiple(() =>
        {
            Assert.That(result.IsReady, Is.False);
            Assert.That(result.Lifecycle, Is.EqualTo(Agent.Models.StructuralSetupLifecycle.Armed));
            Assert.That(result.ReasonCode,
                Is.EqualTo("StructuralIndicatorContinuationSignalMissing"));
        });
    }

    [Test]
    public void SqueezeRelease_WithoutContinuationRelationship_DoesNotTrigger()
    {
        PlaybookEvaluation result = Playbook().Evaluate(
            Evidence(relationship: null, squeezeReleased: true),
            new PlaybookRuntimeState());

        Assert.Multiple(() =>
        {
            Assert.That(result.IsReady, Is.False);
            Assert.That(result.Lifecycle, Is.EqualTo(Agent.Models.StructuralSetupLifecycle.Armed));
            Assert.That(result.ReasonCode,
                Is.EqualTo("StructuralIndicatorContinuationSignalMissing"));
        });
    }

    [Test]
    public void ContinuationIndicators_WithoutPriceActionResumption_DoesNotTrigger()
    {
        PlaybookEvaluation result = Playbook().Evaluate(
            Evidence(
                Relationship(RsiRelationshipType.HiddenBullishDivergence),
                includeAlignedTrigger: false),
            new PlaybookRuntimeState());

        Assert.Multiple(() =>
        {
            Assert.That(result.IsReady, Is.False);
            Assert.That(result.Lifecycle,
                Is.EqualTo(Agent.Models.StructuralSetupLifecycle.CatalystObserved));
            Assert.That(result.ReasonCode,
                Is.EqualTo("StructuralIndicatorPriceActionTriggerMissing"));
            Assert.That(result.TriggerEvent, Is.Null);
        });
    }

    [TestCase(PriceActionEventType.BullishDisplacement)]
    [TestCase(PriceActionEventType.BullishChangeOfCharacter)]
    [TestCase(PriceActionEventType.BullishImpulse)]
    public void RawDirectionalEvent_DoesNotProveTrendResumption(PriceActionEventType eventType)
    {
        PlaybookEvaluation result = Playbook().Evaluate(
            Evidence(
                Relationship(RsiRelationshipType.HiddenBullishDivergence),
                alignedTriggerType: eventType),
            new PlaybookRuntimeState());

        Assert.Multiple(() =>
        {
            Assert.That(result.IsReady, Is.False);
            Assert.That(result.Lifecycle,
                Is.EqualTo(Agent.Models.StructuralSetupLifecycle.CatalystObserved));
            Assert.That(result.ReasonCode,
                Is.EqualTo("StructuralIndicatorPriceActionTriggerMissing"));
        });
    }

    [Test]
    public void SameBarOpposingPriceAction_InvalidatesRegardlessOfConfidenceRank()
    {
        PlaybookEvaluation result = Playbook().Evaluate(
            Evidence(
                Relationship(RsiRelationshipType.HiddenBullishDivergence),
                opposingTrigger: Trigger(
                    PriceActionDirection.Bearish,
                    PriceActionEventType.BearishRejection,
                    65m,
                    Now)),
            new PlaybookRuntimeState());

        Assert.Multiple(() =>
        {
            Assert.That(result.IsReady, Is.False);
            Assert.That(result.Lifecycle,
                Is.EqualTo(Agent.Models.StructuralSetupLifecycle.Invalidated));
            Assert.That(result.ReasonCode,
                Is.EqualTo("StructuralIndicatorStrongOpposingPriceAction"));
            Assert.That(result.ConflictingEvidence,
                Does.Contain("StructuralIndicatorOpposingPriceActionBearishRejection"));
        });
    }

    [Test]
    public void OpposingStructureBreakAfterCatalyst_InvalidatesBeforeEntry()
    {
        PlaybookEvaluation result = Playbook().Evaluate(
            Evidence(
                Relationship(RsiRelationshipType.HiddenBullishDivergence),
                includeAlignedTrigger: false,
                opposingTrigger: Trigger(
                    PriceActionDirection.Bearish,
                    PriceActionEventType.BearishBreakOfStructure,
                    65m,
                    Now.AddMinutes(-1))),
            new PlaybookRuntimeState());

        Assert.Multiple(() =>
        {
            Assert.That(result.IsReady, Is.False);
            Assert.That(result.Lifecycle,
                Is.EqualTo(Agent.Models.StructuralSetupLifecycle.Invalidated));
            Assert.That(result.ReasonCode,
                Is.EqualTo("StructuralIndicatorStrongOpposingPriceAction"));
        });
    }

    [Test]
    public void OpposingNonStructuralEventBeforeResumption_DoesNotVetoEntry()
    {
        PlaybookEvaluation result = Playbook().Evaluate(
            Evidence(
                Relationship(RsiRelationshipType.HiddenBullishDivergence),
                opposingTrigger: Trigger(
                    PriceActionDirection.Bearish,
                    PriceActionEventType.BearishRejection,
                    95m,
                    Now.AddMinutes(-1))),
            new PlaybookRuntimeState());

        Assert.That(result.IsReady, Is.True);
    }

    [Test]
    public void OpposingPriceActionBeforeCatalyst_DoesNotVetoLaterResumption()
    {
        PlaybookEvaluation result = Playbook().Evaluate(
            Evidence(
                Relationship(RsiRelationshipType.HiddenBullishDivergence),
                opposingTrigger: Trigger(
                    PriceActionDirection.Bearish,
                    PriceActionEventType.BearishRejection,
                    95m,
                    Now.AddMinutes(-10))),
            new PlaybookRuntimeState());

        Assert.Multiple(() =>
        {
            Assert.That(result.IsReady, Is.True);
            Assert.That(result.ConflictingEvidence,
                Does.Not.Contain("StructuralIndicatorOpposingPriceActionBearishRejection"));
        });
    }

    [Test]
    public void OpposingSetupStructure_PreventsIndicatorHypothesisFromArming()
    {
        PlaybookEvaluation result = Playbook().Evaluate(
            Evidence(
                Relationship(RsiRelationshipType.HiddenBullishDivergence),
                setupStructureDirection: MarketStructureDirection.Falling),
            new PlaybookRuntimeState());

        Assert.Multiple(() =>
        {
            Assert.That(result.IsReady, Is.False);
            Assert.That(result.Lifecycle,
                Is.EqualTo(Agent.Models.StructuralSetupLifecycle.Dormant));
            Assert.That(result.ReasonCode,
                Is.EqualTo("StructuralIndicatorSetupStructureOpposed"));
        });
    }

    [Test]
    public void RelationshipSeenBeforeTrendExists_IsNotTerminatedBeforeArming()
    {
        IndicatorConfluencePlaybook playbook = Playbook();
        var store = new PlaybookStateStore();
        RsiRelationshipSnapshot relationship =
            Relationship(RsiRelationshipType.HiddenBullishDivergence);

        PlaybookEvaluation dormant = playbook.Evaluate(
            Evidence(relationship, trendStrengthening: false),
            store.Get(Instrument, playbook.PlaybookId));
        store.TryAdvance(Instrument, playbook.PlaybookId, Now, 1, dormant);

        PlaybookEvaluation candidate = playbook.Evaluate(
            Evidence(relationship, availableAt: Now.AddMinutes(5)),
            store.Get(Instrument, playbook.PlaybookId));

        Assert.Multiple(() =>
        {
            Assert.That(dormant.Lifecycle,
                Is.EqualTo(Agent.Models.StructuralSetupLifecycle.Dormant));
            Assert.That(dormant.SetupId, Is.Null);
            Assert.That(store.Get(Instrument, playbook.PlaybookId).TerminalCatalystIdentities,
                Is.Empty);
            Assert.That(candidate.IsReady, Is.True);
        });
    }

    [Test]
    public void InvalidatedCatalyst_CannotResurrect_ButNewRelationshipCanArm()
    {
        IndicatorConfluencePlaybook playbook = Playbook();
        var store = new PlaybookStateStore();
        RsiRelationshipSnapshot first =
            Relationship(RsiRelationshipType.HiddenBullishDivergence);

        PlaybookEvaluation catalyst = playbook.Evaluate(
            Evidence(first, includeAlignedTrigger: false),
            store.Get(Instrument, playbook.PlaybookId));
        store.TryAdvance(Instrument, playbook.PlaybookId, Now, 1, catalyst);

        DateTimeOffset invalidatedAt = Now.AddMinutes(5);
        PlaybookEvaluation invalidated = playbook.Evaluate(
            Evidence(
                first,
                includeAlignedTrigger: false,
                trendStrengthening: false,
                availableAt: invalidatedAt),
            store.Get(Instrument, playbook.PlaybookId));
        store.TryAdvance(Instrument, playbook.PlaybookId, invalidatedAt, 2, invalidated);

        DateTimeOffset retryAt = Now.AddMinutes(10);
        PlaybookEvaluation resurrected = playbook.Evaluate(
            Evidence(first, availableAt: retryAt),
            store.Get(Instrument, playbook.PlaybookId));

        RsiRelationshipSnapshot second = Relationship(
            RsiRelationshipType.HiddenBullishDivergence,
            confirmedAt: Now.AddMinutes(5),
            secondPivotAt: Now);
        PlaybookEvaluation fresh = playbook.Evaluate(
            Evidence(second, availableAt: retryAt),
            store.Get(Instrument, playbook.PlaybookId));

        Assert.Multiple(() =>
        {
            Assert.That(catalyst.Lifecycle,
                Is.EqualTo(Agent.Models.StructuralSetupLifecycle.CatalystObserved));
            Assert.That(invalidated.Lifecycle,
                Is.EqualTo(Agent.Models.StructuralSetupLifecycle.Invalidated));
            Assert.That(resurrected.IsReady, Is.False);
            Assert.That(resurrected.ReasonCode,
                Is.EqualTo("StructuralIndicatorCatalystTerminated"));
            Assert.That(fresh.IsReady, Is.True);
            Assert.That(fresh.CatalystIdentity, Is.Not.EqualTo(invalidated.CatalystIdentity));
        });
    }

    [Test]
    public void ExpiredCatalyst_CannotResurrect()
    {
        IndicatorConfluencePlaybook playbook = Playbook();
        var store = new PlaybookStateStore();
        RsiRelationshipSnapshot relationship =
            Relationship(RsiRelationshipType.HiddenBullishDivergence);

        PlaybookEvaluation catalyst = playbook.Evaluate(
            Evidence(relationship, includeAlignedTrigger: false),
            store.Get(Instrument, playbook.PlaybookId));
        store.TryAdvance(Instrument, playbook.PlaybookId, Now, 1, catalyst);

        PlaybookEvaluation expired = catalyst;
        long version = 1;
        for (int minutes = 5; minutes <= 60; minutes += 5)
        {
            DateTimeOffset evaluationAt = Now.AddMinutes(minutes);
            expired = playbook.Evaluate(
                Evidence(
                    null,
                    includeAlignedTrigger: minutes == 60,
                    availableAt: evaluationAt),
                store.Get(Instrument, playbook.PlaybookId));
            store.TryAdvance(
                Instrument, playbook.PlaybookId, evaluationAt, ++version, expired);
        }

        PlaybookEvaluation resurrected = playbook.Evaluate(
            Evidence(relationship, availableAt: Now.AddMinutes(65)),
            store.Get(Instrument, playbook.PlaybookId));

        Assert.Multiple(() =>
        {
            Assert.That(expired.Lifecycle,
                Is.EqualTo(Agent.Models.StructuralSetupLifecycle.Expired));
            Assert.That(expired.IsReady, Is.False);
            Assert.That(expired.ReasonCode,
                Is.EqualTo("StructuralIndicatorCatalystExpired"));
            Assert.That(resurrected.IsReady, Is.False);
            Assert.That(resurrected.ReasonCode,
                Is.EqualTo("StructuralIndicatorCatalystTerminated"));
        });
    }

    [Test]
    public void SelectedCatalyst_CannotResignalAfterEvaluationGapChangesSetupIdentity()
    {
        IndicatorConfluencePlaybook playbook = Playbook();
        var store = new PlaybookStateStore();
        RsiRelationshipSnapshot relationship =
            Relationship(RsiRelationshipType.HiddenBullishDivergence);

        PlaybookEvaluation armed = playbook.Evaluate(
            Evidence(null, includeAlignedTrigger: false),
            store.Get(Instrument, playbook.PlaybookId));
        store.TryAdvance(Instrument, playbook.PlaybookId, Now, 1, armed);

        DateTimeOffset selectedAt = Now.AddMinutes(5);
        PlaybookEvaluation selected = playbook.Evaluate(
            Evidence(relationship, availableAt: selectedAt),
            store.Get(Instrument, playbook.PlaybookId));
        Assert.That(selected.IsReady, Is.True);
        store.TryAdvance(
            Instrument, playbook.PlaybookId, selectedAt, 2, selected);

        PlaybookEvaluation retry = playbook.Evaluate(
            Evidence(relationship, availableAt: Now.AddMinutes(20)),
            store.Get(Instrument, playbook.PlaybookId));

        Assert.Multiple(() =>
        {
            Assert.That(retry.SetupId, Is.Not.EqualTo(selected.SetupId));
            Assert.That(retry.IsReady, Is.False);
            Assert.That(
                retry.MandatoryGates.Single(gate => gate.Name == "NotAlreadySignaled").Passed,
                Is.False);
        });
    }

    [Test]
    public void SupersededCatalyst_CannotReturnAsANewSetup()
    {
        IndicatorConfluencePlaybook playbook = Playbook();
        var store = new PlaybookStateStore();
        RsiRelationshipSnapshot firstRelationship =
            Relationship(RsiRelationshipType.HiddenBullishDivergence);
        RsiRelationshipSnapshot secondRelationship = Relationship(
            RsiRelationshipType.HiddenBullishDivergence,
            confirmedAt: Now,
            secondPivotAt: Now.AddMinutes(-5));

        PlaybookEvaluation first = playbook.Evaluate(
            Evidence(firstRelationship, includeAlignedTrigger: false),
            store.Get(Instrument, playbook.PlaybookId));
        store.TryAdvance(Instrument, playbook.PlaybookId, Now, 1, first);

        DateTimeOffset secondAt = Now.AddMinutes(5);
        PlaybookEvaluation second = playbook.Evaluate(
            Evidence(
                secondRelationship,
                includeAlignedTrigger: false,
                availableAt: secondAt),
            store.Get(Instrument, playbook.PlaybookId));
        store.TryAdvance(
            Instrument, playbook.PlaybookId, secondAt, 2, second);

        PlaybookEvaluation retry = playbook.Evaluate(
            Evidence(firstRelationship, availableAt: Now.AddMinutes(10)),
            store.Get(Instrument, playbook.PlaybookId));

        Assert.Multiple(() =>
        {
            Assert.That(retry.IsReady, Is.False);
            Assert.That(
                retry.MandatoryGates.Single(gate => gate.Name == "TerminalCatalyst").Passed,
                Is.False);
        });
    }

    [Test]
    public void TrendHypothesis_PreservesIdentityWhenContinuationCatalystArrives()
    {
        IndicatorConfluencePlaybook playbook = Playbook();
        PlaybookEvaluation armed = playbook.Evaluate(
            Evidence(relationship: null),
            new PlaybookRuntimeState());
        var state = new PlaybookRuntimeState
        {
            Lifecycle = armed.Lifecycle,
            SetupId = armed.SetupId,
            LastAvailableAt = Now.AddMinutes(-5),
            LastSnapshotVersion = 1,
            ArmedAt = Now.AddMinutes(-5),
            LastEvaluation = armed
        };

        PlaybookEvaluation candidate = playbook.Evaluate(
            Evidence(Relationship(RsiRelationshipType.HiddenBullishDivergence)),
            state);

        Assert.Multiple(() =>
        {
            Assert.That(armed.Lifecycle, Is.EqualTo(Agent.Models.StructuralSetupLifecycle.Armed));
            Assert.That(candidate.Lifecycle,
                Is.EqualTo(Agent.Models.StructuralSetupLifecycle.CandidateProduced));
            Assert.That(candidate.SetupId, Is.EqualTo(armed.SetupId));
            Assert.That(candidate.CatalystAt, Is.EqualTo(Now.AddMinutes(-5)));
        });
    }

    private static IndicatorConfluencePlaybook Playbook() => new(new StructuralConfluenceStrategyOptions
    {
        IndicatorConfluence = new IndicatorConfluenceOptions
        {
            MinimumBarsBetweenEntries = 0
        }
    });

    private static StructuralEvidencePacket Evidence(
        RsiRelationshipSnapshot? relationship,
        bool squeezeReleased = false,
        bool isExpansion = true,
        bool includeAlignedTrigger = true,
        PriceActionEventType alignedTriggerType = PriceActionEventType.BullishBreakOfStructure,
        PriceActionEvent? opposingTrigger = null,
        bool trendStrengthening = true,
        MarketStructureDirection setupStructureDirection = MarketStructureDirection.Unknown,
        DateTimeOffset? availableAt = null)
    {
        DateTimeOffset at = availableAt ?? Now;
        var rsi = new RsiAnalysisSnapshot
        {
            Zone = RsiZone.Bullish,
            MomentumDirection = MomentumDirection.Rising,
            LatestRelationship = relationship,
            SampleCount = 30
        };
        var bollinger = new BollingerAnalysisSnapshot
        {
            PercentB = 65m,
            WidthDirection = isExpansion
                ? VolatilityDirection.Expanding
                : VolatilityDirection.Stable,
            WidthRegime = isExpansion
                ? BollingerWidthRegime.Expansion
                : BollingerWidthRegime.Normal,
            IsExpansion = isExpansion,
            SqueezeReleased = squeezeReleased,
            SampleCount = 30
        };
        var adx = new AdxAnalysisSnapshot
        {
            Adx = 30m,
            PlusDi = 28m,
            MinusDi = 14m,
            StrengthDirection = trendStrengthening
                ? MomentumDirection.Rising
                : MomentumDirection.Falling,
            DirectionalBias = PriceActionDirection.Bullish,
            IsTrendStrengthening = trendStrengthening
        };
        var indicators = new IndicatorSnapshot
        {
            Atr = 1m,
            Rsi = 58m,
            RsiAnalysis = rsi,
            BollingerAnalysis = bollinger,
            AdxAnalysis = adx
        };
        AnalysisSnapshot context = Analysis(BarInterval.Hours(1), indicators, at);
        AnalysisSnapshot setup = Analysis(BarInterval.Minutes(15), indicators, at) with
        {
            MarketStructure = new MarketStructureSnapshot
            {
                Direction = setupStructureDirection
            }
        };
        AnalysisSnapshot trigger = Analysis(BarInterval.Minutes(5), indicators, at);
        var triggerEvents = new List<PriceActionEvent>();
        if (includeAlignedTrigger)
        {
            triggerEvents.Add(Trigger(
                PriceActionDirection.Bullish,
                alignedTriggerType,
                80m,
                at));
        }
        if (opposingTrigger is not null)
            triggerEvents.Add(opposingTrigger);

        return new StructuralEvidencePacket
        {
            Instrument = Instrument,
            AvailableAt = at,
            ExecutableSpread = 0.02m,
            Context = context,
            Setup = setup,
            Trigger = trigger,
            AdditionalContexts = [],
            ContextEvidence = new StructuralContextEvidence(
                EvidenceAlignment.Aligned, EvidenceAlignment.Conflicting, 80m, 20m),
            SupplyDemand = new SupplyDemandPacket([], []),
            Liquidity = new LiquidityPacket([], [], []),
            TriggerEvidence = new TriggerPacket(triggerEvents.AsReadOnly(), []),
            Indicators = new IndicatorConfirmationPacket(
                1m, null, CciAnalysisSnapshot.Empty, 58m, rsi, StochRsiSnapshot.Empty,
                bollinger, adx, null)
        };
    }

    private static AnalysisSnapshot Analysis(
        BarInterval interval,
        IndicatorSnapshot indicators,
        DateTimeOffset at) => new()
    {
        Instrument = Instrument,
        Interval = interval,
        AvailableAt = at,
        Version = 1,
        LatestCandle = TestCandles.Create(
            Instrument,
            at - TimeSpan.FromSeconds(BarIntervalParser.ApproximateSeconds(interval)),
            interval, 99.5m, 100.5m, 99m, 100m),
        Indicators = indicators,
        Swings = [],
        PriceZones = [],
        Trendlines = [],
        Channels = [],
        Confidence = new ConfidenceScore { Total = 80m, Contributions = [] }
    };

    private static RsiRelationshipSnapshot Relationship(
        RsiRelationshipType type,
        DateTimeOffset? confirmedAt = null,
        DateTimeOffset? secondPivotAt = null) => new()
    {
        Type = type,
        FirstPivotTime = Now.AddMinutes(-20),
        SecondPivotTime = secondPivotAt ?? Now.AddMinutes(-10),
        ConfirmedAt = confirmedAt ?? Now.AddMinutes(-5),
        FirstPrice = 99m,
        SecondPrice = 99.5m,
        FirstRsi = 40m,
        SecondRsi = 45m,
        PriceChange = 0.5m,
        RsiChange = 5m,
        Strength = 80m,
        AgeCandles = 1
    };

    private static PriceActionEvent Trigger(
        PriceActionDirection direction,
        PriceActionEventType type,
        decimal confidence,
        DateTimeOffset at) => new()
    {
        EventId = $"{type}:{at:O}",
        Type = type,
        Direction = direction,
        ConfirmedAt = at,
        ConfirmedSequence = 1,
        Strength = confidence / 100m,
        Confidence = confidence,
        ReasonCode = type.ToString(),
        Explanation = "Test price-action trigger."
    };
}
