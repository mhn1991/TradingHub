using RiskManager.Safety;

namespace Simulator.Tests;

[TestFixture]
public sealed class EquityProtectionTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public void Tier_ActivatesOnlyAfterBothActivationProfitAndGivebackAreMet()
    {
        var safety = new TradingSafetyController(new TradingSafetyOptions
        {
            EquityProtection = new EquityProtectionOptions
            {
                Enabled = true,
                Tiers =
                [
                    new EquityProtectionTier
                    {
                        TierId = "pause-1",
                        ActivationProfitAmount = 500m,
                        MaximumGivebackAmount = 150m,
                        Action = EquityProtectionAction.PauseNewEntries
                    }
                ]
            }
        });

        var start = safety.ObserveEquity(100_000m, Start);
        var peak = safety.ObserveEquity(100_600m, Start.AddHours(1));
        var breached = safety.ObserveEquity(100_440m, Start.AddHours(2));

        Assert.Multiple(() =>
        {
            Assert.That(start.CanOpenNewTrades, Is.True);
            Assert.That(peak.CanOpenNewTrades, Is.True, "Activation profit met but no giveback yet.");
            Assert.That(peak.EquityProtection.PeakEquity, Is.EqualTo(100_600m));
            Assert.That(breached.CanOpenNewTrades, Is.False);
            Assert.That(breached.State, Is.EqualTo(TradingSafetyState.Paused));
            Assert.That(breached.Reason, Is.EqualTo(SafetyTripReason.EquityProtectionTier));
            Assert.That(breached.EquityProtection.ActivatedTierIds, Does.Contain("pause-1"));
            Assert.That(breached.EquityProtection.DrawdownFromPeak, Is.EqualTo(160m));
        });
    }

    [Test]
    public void MultipleReduceFutureRiskTiers_MultiplyTogetherAndClampToOne()
    {
        var safety = new TradingSafetyController(new TradingSafetyOptions
        {
            EquityProtection = new EquityProtectionOptions
            {
                Enabled = true,
                Tiers =
                [
                    new EquityProtectionTier
                    {
                        TierId = "risk-1",
                        MaximumGivebackAmount = 50m,
                        Action = EquityProtectionAction.ReduceFutureRisk,
                        FutureRiskMultiplier = 0.5m
                    },
                    new EquityProtectionTier
                    {
                        TierId = "risk-2",
                        MaximumGivebackAmount = 100m,
                        Action = EquityProtectionAction.ReduceFutureRisk,
                        FutureRiskMultiplier = 0.6m
                    }
                ]
            }
        });

        safety.ObserveEquity(100_000m, Start);
        safety.ObserveEquity(100_500m, Start.AddHours(1));
        var bothActive = safety.ObserveEquity(100_350m, Start.AddHours(2)); // giveback 150 >= both thresholds

        Assert.Multiple(() =>
        {
            Assert.That(bothActive.EquityProtection.ActivatedTierIds, Is.EquivalentTo(new[] { "risk-1", "risk-2" }));
            Assert.That(bothActive.EquityProtection.CurrentRiskMultiplier, Is.EqualTo(0.30m));
            Assert.That(bothActive.CanOpenNewTrades, Is.True, "ReduceFutureRisk tiers must not block new entries.");
        });
    }

    [Test]
    public void FlattenAllPositions_TakesPriorityOverReduceOpenPositionsWhenBothActive()
    {
        var safety = new TradingSafetyController(new TradingSafetyOptions
        {
            EquityProtection = new EquityProtectionOptions
            {
                Enabled = true,
                Tiers =
                [
                    new EquityProtectionTier
                    {
                        TierId = "reduce-tier",
                        MaximumGivebackAmount = 100m,
                        Action = EquityProtectionAction.ReduceOpenPositions,
                        ReductionFraction = 0.3m
                    },
                    new EquityProtectionTier
                    {
                        TierId = "flatten-tier",
                        MaximumGivebackAmount = 100m,
                        Action = EquityProtectionAction.FlattenAllPositions
                    }
                ]
            }
        });

        safety.ObserveEquity(100_000m, Start);
        safety.ObserveEquity(100_500m, Start.AddHours(1));
        var snapshot = safety.ObserveEquity(100_350m, Start.AddHours(2));

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.EquityProtection.PendingPositionAction, Is.EqualTo(EquityProtectionAction.FlattenAllPositions));
            Assert.That(snapshot.EquityProtection.PendingPositionTierId, Is.EqualTo("flatten-tier"));
        });
    }

    [Test]
    public void Recovery_RequiresConsecutiveConfirmationBarsBeforeResuming()
    {
        var safety = new TradingSafetyController(new TradingSafetyOptions
        {
            EquityProtection = new EquityProtectionOptions
            {
                Enabled = true,
                RecoveryConfirmationBars = 2,
                RecoveryDrawdownPercentThreshold = 5m,
                Tiers =
                [
                    new EquityProtectionTier
                    {
                        TierId = "pause-1",
                        MaximumGivebackAmount = 100m,
                        Action = EquityProtectionAction.PauseNewEntries
                    }
                ]
            }
        });

        safety.ObserveEquity(1000m, Start);
        safety.ObserveEquity(1200m, Start.AddHours(1));
        var breached = safety.ObserveEquity(1050m, Start.AddHours(2)); // giveback 150 >= 100
        var recoveringBarOne = safety.ObserveEquity(1180m, Start.AddHours(3)); // giveback 20, drawdown 1.67% <= 5%
        var recoveredBarTwo = safety.ObserveEquity(1190m, Start.AddHours(4)); // second consecutive recovery bar

        Assert.Multiple(() =>
        {
            Assert.That(breached.CanOpenNewTrades, Is.False);
            Assert.That(recoveringBarOne.CanOpenNewTrades, Is.False, "A single recovering bar must not restore trading yet.");
            Assert.That(recoveringBarOne.EquityProtection.ActivatedTierIds, Does.Contain("pause-1"));
            Assert.That(recoveredBarTwo.CanOpenNewTrades, Is.True);
            Assert.That(recoveredBarTwo.EquityProtection.ActivatedTierIds, Is.Empty);
            Assert.That(recoveredBarTwo.EquityProtection.CurrentRiskMultiplier, Is.EqualTo(1m));
        });
    }

    [Test]
    public void Recovery_RelapseResetsTheConfirmationCounter()
    {
        var safety = new TradingSafetyController(new TradingSafetyOptions
        {
            EquityProtection = new EquityProtectionOptions
            {
                Enabled = true,
                RecoveryConfirmationBars = 2,
                RecoveryDrawdownPercentThreshold = 5m,
                Tiers =
                [
                    new EquityProtectionTier
                    {
                        TierId = "pause-1",
                        MaximumGivebackAmount = 100m,
                        Action = EquityProtectionAction.PauseNewEntries
                    }
                ]
            }
        });

        safety.ObserveEquity(1000m, Start);
        safety.ObserveEquity(1200m, Start.AddHours(1));
        safety.ObserveEquity(1050m, Start.AddHours(2)); // breach, giveback 150
        safety.ObserveEquity(1180m, Start.AddHours(3)); // recovery bar 1 (counter = 1)
        var relapse = safety.ObserveEquity(1050m, Start.AddHours(4)); // relapse: giveback 150 again
        var recoveringAgain = safety.ObserveEquity(1180m, Start.AddHours(5)); // recovery bar 1 again (counter should be 1, not 2)

        Assert.Multiple(() =>
        {
            Assert.That(relapse.CanOpenNewTrades, Is.False);
            Assert.That(recoveringAgain.CanOpenNewTrades, Is.False, "The relapse must have reset the confirmation counter.");
        });
    }

    [Test]
    public void Recovery_WithRequireNewEquityHigh_NeedsEquityAtOrAbovePeak()
    {
        var safety = new TradingSafetyController(new TradingSafetyOptions
        {
            EquityProtection = new EquityProtectionOptions
            {
                Enabled = true,
                RecoveryConfirmationBars = 1,
                RecoveryDrawdownPercentThreshold = 50m,
                RequireNewEquityHighForFullRecovery = true,
                Tiers =
                [
                    new EquityProtectionTier
                    {
                        TierId = "pause-1",
                        MaximumGivebackAmount = 100m,
                        Action = EquityProtectionAction.PauseNewEntries
                    }
                ]
            }
        });

        safety.ObserveEquity(1000m, Start);
        safety.ObserveEquity(1200m, Start.AddHours(1));
        safety.ObserveEquity(1050m, Start.AddHours(2)); // breach
        var belowPeak = safety.ObserveEquity(1180m, Start.AddHours(3)); // drawdown recovered but still below peak
        var atNewHigh = safety.ObserveEquity(1200m, Start.AddHours(4)); // back at peak

        Assert.Multiple(() =>
        {
            Assert.That(belowPeak.CanOpenNewTrades, Is.False,
                "RequireNewEquityHighForFullRecovery must block recovery until equity matches the peak.");
            Assert.That(atNewHigh.CanOpenNewTrades, Is.True);
        });
    }

    [Test]
    public void Disabled_LeavesEquityProtectionEmptyAcrossManyObservations()
    {
        var safety = new TradingSafetyController(new TradingSafetyOptions());

        safety.ObserveEquity(100_000m, Start);
        safety.ObserveEquity(100_600m, Start.AddHours(1));
        var snapshot = safety.ObserveEquity(90_000m, Start.AddHours(2));

        Assert.That(snapshot.EquityProtection, Is.EqualTo(EquityHighWatermarkSnapshot.Empty));
    }

    [Test]
    public void DailyAndPersistentProtection_CoexistWithoutInterference()
    {
        var safety = new TradingSafetyController(new TradingSafetyOptions
        {
            DailyEquityGivebackActivation = 400m,
            MaximumDailyEquityGiveback = 150m,
            EquityProtection = new EquityProtectionOptions
            {
                Enabled = true,
                Tiers =
                [
                    new EquityProtectionTier
                    {
                        TierId = "risk-1",
                        MaximumGivebackAmount = 5_000m,
                        Action = EquityProtectionAction.ReduceFutureRisk,
                        FutureRiskMultiplier = 0.5m
                    }
                ]
            }
        });

        safety.ObserveEquity(100_000m, Start);
        safety.ObserveEquity(106_000m, Start.AddHours(1)); // persistent peak 106,000; giveback 6,000 >= 5,000 tier
        var beforeRollover = safety.ObserveEquity(100_500m, Start.AddHours(2));
        var afterRollover = safety.ObserveEquity(100_500m, Start.AddDays(1));

        Assert.Multiple(() =>
        {
            Assert.That(beforeRollover.EquityProtection.CurrentRiskMultiplier, Is.EqualTo(0.5m));
            Assert.That(afterRollover.DailyEquityProfitLoss, Is.Zero, "Daily figures must still reset on UTC day rollover.");
            Assert.That(afterRollover.EquityProtection.PeakEquity, Is.EqualTo(106_000m),
                "The persistent peak must not reset on a day boundary.");
            Assert.That(afterRollover.EquityProtection.CurrentRiskMultiplier, Is.EqualTo(0.5m),
                "The persistent risk multiplier must survive the daily rollover.");
        });
    }

    [Test]
    public void TierValidate_RequiresAtLeastOneGivebackThreshold()
    {
        Assert.That(
            () => new EquityProtectionTier
            {
                TierId = "bad",
                Action = EquityProtectionAction.PauseNewEntries
            }.Validate(),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void TierValidate_RejectsFutureRiskMultiplierAboveOne()
    {
        Assert.That(
            () => new EquityProtectionTier
            {
                TierId = "bad",
                MaximumGivebackAmount = 100m,
                Action = EquityProtectionAction.ReduceFutureRisk,
                FutureRiskMultiplier = 1.5m
            }.Validate(),
            Throws.TypeOf<ArgumentOutOfRangeException>(),
            "Phase 1 must never allow adaptive risk above base risk.");
    }

    [Test]
    public void OptionsValidate_RejectsDuplicateTierIds()
    {
        Assert.That(
            () => new EquityProtectionOptions
            {
                Enabled = true,
                Tiers =
                [
                    new EquityProtectionTier { TierId = "dup", MaximumGivebackAmount = 100m, Action = EquityProtectionAction.PauseNewEntries },
                    new EquityProtectionTier { TierId = "dup", MaximumGivebackAmount = 200m, Action = EquityProtectionAction.FlattenAllPositions }
                ]
            }.Validate(),
            Throws.TypeOf<ArgumentException>());
    }
}
