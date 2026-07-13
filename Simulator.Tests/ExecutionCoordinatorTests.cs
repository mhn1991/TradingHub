using Agent.Execution;
using Agent.Models;
using Brokers.Abstractions;
using Brokers.Models;
using Simulator.Broker;
using Simulator.Models;
using Simulator.Time;

namespace Simulator.Tests;

[TestFixture]
public sealed class ExecutionCoordinatorTests
{
    private static readonly InstrumentKey Instrument = new("FX:GBP/USD");
    private static readonly DateTimeOffset Now =
        new(2026, 1, 1, 0, 5, 0, TimeSpan.Zero);

    [Test]
    public async Task RetriedDecision_UsesStableIdAndIsBlockedByOpenOrder()
    {
        var clock = new HistoricalSimulationClock();
        clock.AdvanceTo(Now);
        await using var broker = new SimulatedBrokerClient(
            new SimulationOptions(),
            clock);
        var coordinator = new ExecutionCoordinator();
        AgentDecision decision = BuyDecision();

        OrderSubmission first = (await coordinator.ProcessAsync(decision, broker))!;
        OrderSubmission retry = (await coordinator.ProcessAsync(decision, broker))!;

        Assert.Multiple(() =>
        {
            Assert.That(first.Status, Is.EqualTo(SubmissionStatus.Accepted));
            Assert.That(retry.Status, Is.EqualTo(SubmissionStatus.Rejected));
            Assert.That(retry.Certainty, Is.EqualTo(ExecutionCertainty.NotSent));
            Assert.That(retry.ClientOrderId, Is.EqualTo(first.ClientOrderId));
            Assert.That(retry.RejectionReason, Does.Contain("open order"));
        });
    }

    [Test]
    public async Task SameDirectionPosition_IsBlockedWhenPyramidingIsDisabled()
    {
        var clock = new HistoricalSimulationClock();
        clock.AdvanceTo(Now.AddMinutes(-5));
        await using var broker = new SimulatedBrokerClient(
            new SimulationOptions
            {
                CommissionRate = 0m,
                SpreadBasisPoints = 0m,
                SlippageBasisPoints = 0m
            },
            clock);
        await broker.Orders.PlaceOrderAsync(new PlaceOrderRequest
        {
            Instrument = Instrument,
            Side = OrderSide.Buy,
            Type = StandardOrderType.Market,
            Quantity = new OrderQuantity(1m, QuantityUnit.Units)
        });
        clock.AdvanceTo(Now);
        await broker.Runtime.ProcessExecutionCandleAsync(new Candle
        {
            Instrument = Instrument,
            Interval = BarInterval.Minutes(5),
            OpenTime = Now.AddMinutes(-5),
            CloseTime = Now,
            Prices = new Ohlc(100m, 101m, 99m, 100m),
            IsComplete = true
        });

        OrderSubmission result = (await new ExecutionCoordinator()
            .ProcessAsync(BuyDecision(), broker))!;

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(SubmissionStatus.Rejected));
            Assert.That(result.Certainty, Is.EqualTo(ExecutionCertainty.NotSent));
            Assert.That(result.RejectionReason, Does.Contain("Pyramiding"));
        });
    }

    [Test]
    public void InvalidExecutionOptions_AreRejected()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                () => new ExecutionCoordinator(new ExecutionOptions { MinimumConfidence = -1m }),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => new ExecutionCoordinator(new ExecutionOptions { MinimumConfidence = 101m }),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => new ExecutionCoordinator(new ExecutionOptions
                {
                    MaximumAbsolutePositionQuantity = 0m
                }),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }

    [TestCase(AgentAction.Close)]
    [TestCase(AgentAction.Cancel)]
    public void UnsupportedDecisionAction_IsRejected(AgentAction action)
    {
        var broker = new FakeTradingBroker();
        AgentDecision decision = BuyDecision() with { Action = action };

        Assert.That(
            async () => await new ExecutionCoordinator().ProcessAsync(decision, broker),
            Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    public async Task ObserveDecision_DoesNotContactBroker()
    {
        var broker = new FakeTradingBroker { ThrowOnRead = true };
        AgentDecision decision = BuyDecision() with { Action = AgentAction.Observe };

        OrderSubmission? result = await new ExecutionCoordinator().ProcessAsync(decision, broker);

        Assert.That(result, Is.Null);
    }

    [TestCase(-1)]
    [TestCase(101)]
    public void InvalidDecisionConfidence_IsRejected(decimal confidence)
    {
        var broker = new FakeTradingBroker();

        Assert.That(
            async () => await new ExecutionCoordinator().ProcessAsync(
                BuyDecision() with { Confidence = confidence },
                broker),
            Throws.TypeOf<InvalidOperationException>());
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void NonPositiveDecisionQuantity_IsRejected(decimal quantity)
    {
        var broker = new FakeTradingBroker();

        Assert.That(
            async () => await new ExecutionCoordinator().ProcessAsync(
                BuyDecision() with { SuggestedQuantity = quantity },
                broker),
            Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void MissingDecisionQuantityAndInstrument_AreRejected()
    {
        var broker = new FakeTradingBroker();
        Assert.Multiple(() =>
        {
            Assert.That(
                async () => await new ExecutionCoordinator().ProcessAsync(
                    BuyDecision() with { SuggestedQuantity = null },
                    broker),
                Throws.TypeOf<InvalidOperationException>());
            Assert.That(
                async () => await new ExecutionCoordinator().ProcessAsync(
                    BuyDecision() with { Instrument = default },
                    broker),
                Throws.TypeOf<InvalidOperationException>());
        });
    }

    [TestCase(null)]
    [TestCase(false)]
    public async Task AccountWithoutExplicitTradingPermission_BlocksSubmission(bool? canTrade)
    {
        var broker = new FakeTradingBroker
        {
            Accounts = [Account(canTrade)]
        };

        OrderSubmission result = (await new ExecutionCoordinator()
            .ProcessAsync(BuyDecision(), broker))!;

        Assert.Multiple(() =>
        {
            Assert.That(result.Certainty, Is.EqualTo(ExecutionCertainty.NotSent));
            Assert.That(broker.PlacedRequests, Is.Empty);
        });
    }

    [Test]
    public async Task MissingAccount_BlocksSubmissionByDefault()
    {
        var broker = new FakeTradingBroker { Accounts = [] };

        OrderSubmission result = (await new ExecutionCoordinator()
            .ProcessAsync(BuyDecision(), broker))!;

        Assert.That(result.RejectionReason, Does.Contain("did not return an account"));
    }

    [Test]
    public async Task AccountGate_CanBeExplicitlyDisabled()
    {
        var broker = new FakeTradingBroker { Accounts = [] };
        var coordinator = new ExecutionCoordinator(new ExecutionOptions
        {
            BlockWhenAccountCannotTrade = false
        });

        OrderSubmission result = (await coordinator.ProcessAsync(BuyDecision(), broker))!;

        Assert.That(result.Status, Is.EqualTo(SubmissionStatus.Accepted));
    }

    [TestCase(OrderStatus.Pending, true)]
    [TestCase(OrderStatus.Open, true)]
    [TestCase(OrderStatus.PartiallyFilled, true)]
    [TestCase(OrderStatus.Unknown, false)]
    [TestCase(OrderStatus.Cancelled, false)]
    [TestCase(OrderStatus.Filled, false)]
    public async Task DuplicateOrderGate_UsesNormalizedOpenStatuses(
        OrderStatus status,
        bool expectedBlocked)
    {
        var broker = new FakeTradingBroker
        {
            Orders = [Order(status)]
        };

        OrderSubmission result = (await new ExecutionCoordinator()
            .ProcessAsync(BuyDecision(), broker))!;

        Assert.That(result.Certainty == ExecutionCertainty.NotSent, Is.EqualTo(expectedBlocked));
    }

    [TestCase(2, 2, true)]
    [TestCase(2, 2.0001, false)]
    public async Task MaximumPositionBoundary_IsEnforced(
        decimal maximum,
        decimal quantity,
        bool expectedAccepted)
    {
        var broker = new FakeTradingBroker();
        var coordinator = new ExecutionCoordinator(new ExecutionOptions
        {
            MaximumAbsolutePositionQuantity = maximum
        });

        OrderSubmission result = (await coordinator.ProcessAsync(
            BuyDecision() with { SuggestedQuantity = quantity },
            broker))!;

        Assert.That(result.Status == SubmissionStatus.Accepted, Is.EqualTo(expectedAccepted));
    }

    [Test]
    public async Task MinimumConfidence_RejectsWithoutReadingBroker()
    {
        var broker = new FakeTradingBroker { ThrowOnRead = true };
        var coordinator = new ExecutionCoordinator(new ExecutionOptions
        {
            MinimumConfidence = 95m
        });

        OrderSubmission result = (await coordinator.ProcessAsync(BuyDecision(), broker))!;

        Assert.Multiple(() =>
        {
            Assert.That(result.Certainty, Is.EqualTo(ExecutionCertainty.NotSent));
            Assert.That(result.RejectionReason, Does.Contain("below"));
        });
    }

    [Test]
    public async Task PyramidingCanBeExplicitlyEnabled()
    {
        var broker = new FakeTradingBroker
        {
            Positions = [Position(OrderSide.Buy, 1m)]
        };
        var coordinator = new ExecutionCoordinator(new ExecutionOptions
        {
            AllowPyramiding = true
        });

        OrderSubmission result = (await coordinator.ProcessAsync(BuyDecision(), broker))!;

        Assert.That(result.Status, Is.EqualTo(SubmissionStatus.Accepted));
    }

    [Test]
    public async Task OppositeDirectionOrder_IsAllowedToReducePosition()
    {
        var broker = new FakeTradingBroker
        {
            Positions = [Position(OrderSide.Buy, 1m)]
        };

        OrderSubmission result = (await new ExecutionCoordinator().ProcessAsync(
            BuyDecision() with { Action = AgentAction.Sell },
            broker))!;

        Assert.That(result.Status, Is.EqualTo(SubmissionStatus.Accepted));
    }

    [Test]
    public async Task DerivedId_IsStableAndChangesWhenDecisionChanges()
    {
        var broker = new FakeTradingBroker();
        var coordinator = new ExecutionCoordinator();
        AgentDecision original = BuyDecision() with { DecisionId = null };

        OrderSubmission first = (await coordinator.ProcessAsync(original, broker))!;
        OrderSubmission retry = (await coordinator.ProcessAsync(original, broker))!;
        OrderSubmission changed = (await coordinator.ProcessAsync(
            original with { SuggestedQuantity = 2m },
            broker))!;

        Assert.Multiple(() =>
        {
            Assert.That(retry.ClientOrderId, Is.EqualTo(first.ClientOrderId));
            Assert.That(changed.ClientOrderId, Is.Not.EqualTo(first.ClientOrderId));
        });
    }

    private static AgentDecision BuyDecision() => new()
    {
        DecisionId = "decision-123",
        Action = AgentAction.Buy,
        Instrument = Instrument,
        SuggestedQuantity = 1m,
        QuantityUnit = QuantityUnit.Units,
        OrderType = StandardOrderType.Market,
        Confidence = 90m,
        CreatedAt = Now,
        Reason = "Test decision"
    };

    private static AccountSnapshot Account(bool? canTrade = true) => new()
    {
        AccountId = "account",
        CanTrade = canTrade
    };

    private static BrokerOrder Order(OrderStatus status) => new()
    {
        BrokerOrderId = "order",
        Instrument = Instrument,
        Side = OrderSide.Buy,
        Type = "LIMIT",
        Status = status.ToString(),
        NormalizedStatus = status
    };

    private static BrokerPosition Position(OrderSide side, decimal quantity) => new()
    {
        PositionId = "position",
        Instrument = Instrument,
        Side = side,
        Quantity = quantity
    };

    private sealed class FakeTradingBroker : ITradingBrokerClient
    {
        private readonly FakeOrderClient _orders;

        public FakeTradingBroker()
        {
            _orders = new FakeOrderClient(this);
        }

        public BrokerDescriptor Descriptor { get; } = new(BrokerKind.Oanda, BrokerEnvironment.Demo, "account");
        public IReadOnlyList<AccountSnapshot> Accounts { get; init; } = [Account()];
        public IReadOnlyList<BrokerPosition> Positions { get; init; } = [];
        public IReadOnlyList<BrokerOrder> Orders { get; init; } = [];
        public List<PlaceOrderRequest> PlacedRequests { get; } = [];
        public bool ThrowOnRead { get; init; }
        public IMarketDataClient MarketData => new FakeMarketDataClient();
        IAccountClient IBrokerClient.Accounts => new FakeAccountClient(this);
        ITradingOrderClient ITradingBrokerClient.Orders => _orders;
        IOrderClient IBrokerClient.Orders => _orders;
        IPositionClient IBrokerClient.Positions => new FakePositionClient(this);
        ICostClient IBrokerClient.Costs => new FakeCostClient();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class FakeAccountClient(FakeTradingBroker owner) : IAccountClient
        {
            public Task<IReadOnlyList<AccountSnapshot>> GetAccountsAsync(
                CancellationToken cancellationToken = default) =>
                owner.ThrowOnRead
                    ? throw new InvalidOperationException("Unexpected broker read.")
                    : Task.FromResult(owner.Accounts);
        }

        private sealed class FakePositionClient(FakeTradingBroker owner) : IPositionClient
        {
            public Task<IReadOnlyList<BrokerPosition>> GetOpenPositionsAsync(
                CancellationToken cancellationToken = default) =>
                owner.ThrowOnRead
                    ? throw new InvalidOperationException("Unexpected broker read.")
                    : Task.FromResult(owner.Positions);
        }

        private sealed class FakeOrderClient(FakeTradingBroker owner) : ITradingOrderClient
        {
            public Task<IReadOnlyList<BrokerOrder>> GetOpenOrdersAsync(
                InstrumentKey? instrument = null,
                CancellationToken cancellationToken = default) =>
                owner.ThrowOnRead
                    ? throw new InvalidOperationException("Unexpected broker read.")
                    : Task.FromResult(owner.Orders);

            public Task<OrderSubmission> PlaceOrderAsync(
                PlaceOrderRequest request,
                CancellationToken cancellationToken = default)
            {
                owner.PlacedRequests.Add(request);
                return Task.FromResult(new OrderSubmission
                {
                    ClientOrderId = request.ClientOrderId!,
                    BrokerOrderId = "broker-order",
                    Status = SubmissionStatus.Accepted,
                    Certainty = ExecutionCertainty.Accepted
                });
            }

            public Task CancelOrderAsync(
                string brokerOrderId,
                CancellationToken cancellationToken = default) => Task.CompletedTask;

            public async IAsyncEnumerable<OrderEvent> StreamOrderEventsAsync(
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default)
            {
                await Task.CompletedTask;
                yield break;
            }
        }

        private sealed class FakeMarketDataClient : IMarketDataClient
        {
            public Task<IReadOnlyList<Candle>> GetCandlesAsync(
                CandleQuery query,
                CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<Candle>>([]);
        }

        private sealed class FakeCostClient : ICostClient
        {
            public Task<CommissionSchedule> GetCommissionAsync(
                InstrumentKey instrument,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new CommissionSchedule { Instrument = instrument });
        }
    }
}
