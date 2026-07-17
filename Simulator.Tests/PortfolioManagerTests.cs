using Agent.Models;
using Brokers.Models;
using PortfolioManager.Allocation;
using PortfolioManager.Correlation;
using PortfolioManager.Risk;
using RiskManager;

namespace Simulator.Tests;

[TestFixture]
public sealed class PortfolioManagerTests
{
    [Test]
    public void ReservationBook_IsAtomic_AndDoubleReleaseIsIdempotent()
    {
        var book = new PortfolioReservationBook(new PortfolioRiskOptions
        {
            MaximumTotalOpenRiskPercent = 1.5m,
            MaximumPendingRiskPercent = 0.75m,
            MaximumStrategyRiskPercent = 0.75m,
            MaximumInstrumentRiskPercent = 0.75m,
            MaximumCurrencyStopRiskPercent = 0.75m
        });
        PortfolioReservationRequest first = Request("r1", "a", 500m);
        PortfolioReservationRequest second = Request("r2", "b", 500m);

        PortfolioReservationResult[] results = Task.WhenAll(
                Task.Run(() => book.TryReserve(first)),
                Task.Run(() => book.TryReserve(second)))
            .GetAwaiter().GetResult();

        Assert.That(results.Count(item => item.Approved), Is.EqualTo(1));
        string approvedId = results.Single(item => item.Approved).Reservation!.ReservationId;
        book.Release(approvedId, PortfolioReleaseReason.Cancelled);
        long version = book.Snapshot.Version;
        book.Release(approvedId, PortfolioReleaseReason.Cancelled);
        Assert.Multiple(() =>
        {
            Assert.That(book.Snapshot.PendingRisk, Is.Zero);
            Assert.That(book.Snapshot.Version, Is.EqualTo(version));
        });
    }

    [Test]
    public void PortfolioHeat_RecalculatesAfterStopMoves()
    {
        PortfolioPositionLot lot = Lot(stop: 0.99m);
        PortfolioHeatSnapshot before = PortfolioHeatCalculator.Calculate(10_000m, [lot], []);
        PortfolioHeatSnapshot after = PortfolioHeatCalculator.Calculate(10_000m, [lot with { ProtectiveStopPrice = 0.995m }], []);

        Assert.Multiple(() =>
        {
            Assert.That(before.OpenHeat, Is.EqualTo(100m));
            Assert.That(after.OpenHeat, Is.EqualTo(50m));
            Assert.That(after.TotalHeat, Is.LessThan(before.TotalHeat));
        });
    }

    [Test]
    public void ReservationFillCommit_ReleasesOnlyTheFilledFractionExactlyOnce()
    {
        var book = new PortfolioReservationBook(new PortfolioRiskOptions());
        PortfolioReservationResult reserved = book.TryReserve(Request("fill-r1", "strategy", 500m) with
        {
            Reservation = Request("fill-r1", "strategy", 500m).Reservation with
            {
                EstimatedMargin = 100m
            }
        });
        Assert.That(reserved.Approved, Is.True);

        book.CommitFill("fill-r1", new PortfolioFillAllocation
        {
            FilledQuantity = 25m,
            FillRiskAccountCurrency = 125m,
            FillMarginAccountCurrency = 25m
        });
        PortfolioReservationSnapshot partial = book.Snapshot;
        book.CommitFill("fill-r1", new PortfolioFillAllocation
        {
            FilledQuantity = 75m,
            FillRiskAccountCurrency = 375m,
            FillMarginAccountCurrency = 75m
        });
        long completedVersion = book.Snapshot.Version;
        book.Release("fill-r1", PortfolioReleaseReason.FillReconciled);

        Assert.Multiple(() =>
        {
            Assert.That(partial.PendingRisk, Is.EqualTo(375m));
            Assert.That(partial.ReservedMargin, Is.EqualTo(75m));
            Assert.That(book.Snapshot.PendingRisk, Is.Zero);
            Assert.That(book.Snapshot.Version, Is.EqualTo(completedVersion));
        });
    }

    [Test]
    public void CurrencyExposure_DecomposesLongPair_AndRejectsMissingConversion()
    {
        var calculator = new CurrencyExposureCalculator();
        CurrencyExposureResult complete = calculator.Calculate([Lot(stop: 0.99m)], [], new FxConversionSnapshot
        {
            AvailableAt = DateTimeOffset.UtcNow,
            AccountCurrency = "USD",
            CurrencyToAccountRates = new Dictionary<string, decimal> { ["GBP"] = 1.25m }
        });
        CurrencyExposureResult missing = calculator.Calculate([Lot(stop: 0.99m)], [], new FxConversionSnapshot
        {
            AvailableAt = DateTimeOffset.UtcNow,
            AccountCurrency = "EUR",
            CurrencyToAccountRates = new Dictionary<string, decimal>()
        });

        Assert.Multiple(() =>
        {
            Assert.That(complete.Complete, Is.True);
            Assert.That(complete.NetExposureAccountCurrency["GBP"], Is.EqualTo(12_500m));
            Assert.That(complete.NetExposureAccountCurrency["USD"], Is.EqualTo(-10_000m));
            Assert.That(missing.Complete, Is.False);
            Assert.That(missing.MissingCurrencies, Does.Contain("GBP"));
            Assert.That(missing.MissingCurrencies, Does.Contain("USD"));
        });
    }

    [Test]
    public void RollingCorrelation_AppliesHardPenaltyToSameDirection()
    {
        var options = new CorrelationRiskOptions
        {
            LookbackBars = 10,
            MinimumSamples = 5,
            SoftCorrelationThreshold = 0.5m,
            HardCorrelationThreshold = 0.75m
        };
        var state = new RollingCorrelationClusters(options);
        InstrumentKey left = new("FX:GBP/USD");
        InstrumentKey right = new("FX:EUR/USD");
        DateTimeOffset start = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        for (int i = 1; i <= 6; i++)
        {
            state.Update(start.AddHours(i), new Dictionary<InstrumentKey, decimal>
            {
                [left] = i,
                [right] = i * 2m
            });
        }

        CorrelationPenaltyDecision penalty = state.Evaluate(left, 1, new Dictionary<InstrumentKey, int> { [right] = 1 });
        CorrelationPenaltyDecision hedge = state.Evaluate(left, 1, new Dictionary<InstrumentKey, int> { [right] = -1 });
        Assert.Multiple(() =>
        {
            Assert.That(penalty.RiskMultiplier, Is.EqualTo(options.HardRiskMultiplier));
            Assert.That(penalty.MaximumRelevantCorrelation, Is.EqualTo(1m));
            Assert.That(hedge.RiskMultiplier, Is.EqualTo(1m));
        });
    }

    [Test]
    public void CapitalAllocator_UsesDeterministicTieBreakers()
    {
        var book = new PortfolioReservationBook(new PortfolioRiskOptions());
        var allocator = new CapitalAllocator(book);
        DateTimeOffset now = new(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);
        PortfolioOpportunity larger = Opportunity("b", "d2", 200m, 20m, now);
        PortfolioOpportunity smaller = Opportunity("a", "d1", 100m, 10m, now);
        PortfolioAllocationContext context = new()
        {
            AccountEquity = 100_000m,
            CurrencyExposureFactory = (_, _) => new Dictionary<string, decimal>(),
            CorrelationClusterFactory = _ => "cluster:test"
        };

        IReadOnlyList<PortfolioAllocationDecision> decisions = allocator.Allocate([larger, smaller], context);
        Assert.That(decisions[0].Opportunity.StrategyId, Is.EqualTo("a"));
    }

    [Test]
    public void CapitalAllocator_ResizesSimultaneousStrategiesWithoutExceedingSharedHeat()
    {
        var book = new PortfolioReservationBook(new PortfolioRiskOptions
        {
            MaximumTotalOpenRiskPercent = 0.75m,
            MaximumPendingRiskPercent = 0.75m,
            MaximumStrategyRiskPercent = 0.75m,
            MaximumInstrumentRiskPercent = 0.75m,
            MaximumCurrencyStopRiskPercent = 0.75m
        });
        var allocator = new CapitalAllocator(book);
        DateTimeOffset now = new(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);
        PortfolioOpportunity first = Opportunity("a", "d1", 100m, 500m, now);
        PortfolioOpportunity second = Opportunity("b", "d2", 100m, 500m, now);
        IReadOnlyList<PortfolioAllocationDecision> decisions = allocator.Allocate(
            [first, second],
            new PortfolioAllocationContext
            {
                AccountEquity = 100_000m,
                CurrencyExposureFactory = (_, _) => new Dictionary<string, decimal>(),
                CorrelationClusterFactory = _ => "cluster:test"
            });

        Assert.Multiple(() =>
        {
            Assert.That(decisions, Has.Count.EqualTo(2));
            Assert.That(decisions.All(item => item.Approved), Is.True);
            Assert.That(decisions.Sum(item => item.AllocatedQuantity), Is.EqualTo(150m));
            Assert.That(book.Snapshot.PendingRisk, Is.EqualTo(750m));
            Assert.That(book.Snapshot.PendingRisk / 100_000m * 100m, Is.LessThanOrEqualTo(0.75m));
        });
    }

    private static PortfolioReservationRequest Request(string id, string strategy, decimal risk) => new()
    {
        Reservation = new PortfolioReservation
        {
            ReservationId = id,
            StrategyId = strategy,
            DecisionId = id,
            Instrument = new InstrumentKey("FX:GBP/USD"),
            Quantity = 100m,
            PlannedStopRiskAccountCurrency = risk,
            EstimatedMargin = 100m,
            CurrencyExposureDelta = new Dictionary<string, decimal>(),
            CorrelationClusterId = "cluster:test",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedSequence = id == "r1" ? 1 : 2
        },
        AccountEquity = 100_000m
    };

    private static PortfolioPositionLot Lot(decimal stop) => new()
    {
        LotId = "lot",
        StrategyId = "strategy",
        DecisionId = "decision",
        Instrument = new InstrumentKey("FX:GBP/USD"),
        Side = OrderSide.Buy,
        Quantity = 10_000m,
        EntryPrice = 1m,
        CurrentPrice = 1m,
        ProtectiveStopPrice = stop,
        QuoteToAccountCurrencyRate = 1m,
        ContractMultiplier = 1m
    };

    private static PortfolioOpportunity Opportunity(
        string strategy,
        string decisionId,
        decimal quantity,
        decimal risk,
        DateTimeOffset time) => new()
    {
        StrategyId = strategy,
        Decision = new AgentDecision
        {
            DecisionId = decisionId,
            Action = AgentAction.Buy,
            Instrument = new InstrumentKey("FX:GBP/USD"),
            Confidence = 60m,
            CreatedAt = time,
            Reason = "test"
        },
        Sizing = new PositionSizingResult
        {
            Approved = true,
            Quantity = quantity,
            EstimatedLossAtStop = risk,
            EstimatedMargin = 100m,
            ReasonCode = "test",
            Reason = "test"
        },
        SetupQuality = 1m,
        ExpectedRewardRisk = 2m,
        RegimeSuitability = 1m,
        TransactionCostPenalty = 0m,
        CorrelationPenalty = 0m,
        CurrencyConcentrationPenalty = 0m,
        MarginConsumptionPenalty = 0m,
        CreatedAt = time,
        Sequence = strategy == "a" ? 1 : 2
    };
}
