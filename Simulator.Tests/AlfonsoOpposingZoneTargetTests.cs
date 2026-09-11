using Agent.Strategies.Alfonso;
using Agent.Strategies.Alfonso.Zones;
using Agent.Models;
using Brokers.Models;
using NUnit.Framework;
using Simulator.Models;
using RiskManager;

namespace Simulator.Tests;

[TestFixture]
public sealed class AlfonsoOpposingZoneTargetTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 17, 3, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private static Imbalance Zone(bool buy, decimal proximal) => new()
    {
        Interval = Interval, Kind = buy ? ImbalanceKind.Supply : ImbalanceKind.Demand,
        Proximal = proximal, Distal = buy ? proximal + 1m : proximal - 1m,
        BaseStart = Now.AddHours(-2), BaseEnd = Now.AddHours(-2), DistalAt = Now.AddHours(-2),
        ConfirmedAt = Now.AddMinutes(-15), BaseCandleCount = 1,
        Strength = ImpulseStrength.Strong, Accomplished = Accomplishment.SwingBroken,
        ImpulseToBaseRatio = 3m, ImpulseDisplacement = 3m, ImpulseBarsTracked = 2,
        IsContinuationPattern = false, MeetsTradeabilityCriteria = true
    };

    [TestCase(true)]
    [TestCase(false)]
    public void NearestOpposingProximalWinsNotFartherOrWrongSide(bool buy)
    {
        var nearest = Zone(buy, buy ? 101m : 99m);
        Imbalance[] zones = [Zone(buy, buy ? 105m : 95m),
            Zone(buy, buy ? 99m : 101m), Zone(!buy, buy ? 100.5m : 99.5m),
            Zone(buy, 100m), nearest];
        Assert.That(AlfonsoOpposingZoneTarget.Find(buy, 100m, Interval, Now, zones), Is.SameAs(nearest));
    }

    [Test]
    public void ConfirmationMustBeClosedAndOnEntryTimeframe()
    {
        var zone = Zone(true, 101m);
        Assert.That(AlfonsoOpposingZoneTarget.Find(true, 100m, Interval, Now.AddTicks(-1), [zone]), Is.Null);
        Assert.That(AlfonsoOpposingZoneTarget.Find(true, 100m, Interval, Now, [zone]), Is.SameAs(zone));
        Assert.That(AlfonsoOpposingZoneTarget.Find(true, 100m, Interval, Now,
            [zone with { ConfirmedAt = Now }, zone with { Interval = TimeSpan.FromHours(1) }]), Is.Null);
    }

    [TestCase(ImbalanceState.Fresh)]
    [TestCase(ImbalanceState.Tested)]
    [TestCase(ImbalanceState.UsedUp)]
    public void SurvivingReactionLevelsNeedNotQualifyForFreshEntry(ImbalanceState state)
    {
        var zone = Zone(true, 101m) with { State = state, Strength = ImpulseStrength.Weak,
            MeetsTradeabilityCriteria = false };
        Assert.That(AlfonsoOpposingZoneTarget.Find(true, 100m, Interval, Now, [zone]), Is.SameAs(zone));
    }

    [Test]
    public void EliminatedAndMissingZonesNeverFallBackToR()
    {
        var zone = Zone(false, 99m);
        Assert.That(AlfonsoOpposingZoneTarget.Find(false, 100m, Interval, Now, []), Is.Null);
        Assert.That(AlfonsoOpposingZoneTarget.Find(false, 100m, Interval, Now,
            [zone with { State = ImbalanceState.Eliminated }, zone with { EliminatedAt = Now }]), Is.Null);
    }

    [Test]
    public void DescriptionRecordsExactLevelAndCausalProvenance()
    {
        var description = AlfonsoOpposingZoneTarget.Describe(Zone(false, 54.95m));
        Assert.That(description, Does.Contain("opposing Demand 15m: proximal 54.95"));
        Assert.That(description, Does.Contain($"confirmed close {Now:O}"));
        Assert.That(description, Does.Contain("base "));
    }

    [Test]
    public void RequestWiresOptInAndAcceptsExplicitZeroFloorWithoutChangingDefaults()
    {
        var request = new BacktestRequest
        {
            Instrument = new InstrumentKey("METAL:XAG/USD"), From = Now, To = Now.AddDays(1),
            Strategies = ["alfonso"], AlfonsoUseOpposingZoneTarget = true, MinimumRewardRisk = 0m
        };
        request.Validate();
        var options = request.ResolveAgentDefinition("alfonso").Alfonso!;
        Assert.That(options.UseOpposingZoneTarget, Is.True);
        Assert.That(new AlfonsoStrategyOptions().UseOpposingZoneTarget, Is.False);
        Assert.That(new BacktestRequest { Instrument = request.Instrument, From = Now, To = Now.AddDays(1) }
            .MinimumRewardRisk, Is.EqualTo(1.5m));
        Assert.Throws<ArgumentException>(() => (request with { MinimumRewardRisk = -1m }).Validate());
    }

    [Test]
    public void NoRewardFloorStillRequiresValidBracketAndHonorsCashRisk()
    {
        var risk = new PreTradeRiskManager(PreTradeRiskOptions.PhaseOneSafeDefaults with
        {
            RequireTakeProfit = true, MinimumRewardRiskRatio = null
        });
        var context = new PreTradeRiskContext
        {
            Decision = new AgentDecision
            {
                Action = AgentAction.Buy, Instrument = new InstrumentKey("METAL:XAG/USD"),
                ReferencePrice = 100m, StopLossPrice = 90m, TakeProfitPrice = 100.1m,
                SuggestedQuantity = 10m, QuantityUnit = QuantityUnit.Units,
                Confidence = 80m, CreatedAt = Now, Reason = "nearby opposing zone"
            },
            Quantity = 10m,
            Accounts = [new AccountSnapshot { AccountId = "test", Currency = "USD", Balance = 100000m, CanTrade = true }],
            Positions = []
        };
        Assert.That(risk.Evaluate(context).Approved, Is.True, "A positive 0.01R target is allowed.");
        Assert.That(risk.Evaluate(context with { Quantity = 100m }).Approved, Is.False);
        Assert.That(risk.Evaluate(context with { Decision = context.Decision with { TakeProfitPrice = null } }).Approved, Is.False);
        Assert.That(risk.Evaluate(context with { Decision = context.Decision with { TakeProfitPrice = 99m } }).Approved, Is.False);
        Assert.That(risk.Evaluate(context with { Decision = context.Decision with { StopLossPrice = null } }).Approved, Is.False);
    }
}
