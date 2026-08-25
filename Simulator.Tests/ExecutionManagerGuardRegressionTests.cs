using Agent.Models;
using Brokers.Abstractions;
using Brokers.Models;
using ExecutionManager;
using RiskManager;

namespace Simulator.Tests;

/// <summary>
/// Regression cover for the ExecutionManager audit (2026-08-19): the pre-trade risk caps must never
/// be evaluated against a fabricated FX rate, the amendment cache must not grow without bound, and a
/// Close on a hedged book must not silently guess which side to close.
/// </summary>
[TestFixture]
public sealed class ExecutionManagerGuardRegressionTests
{
    // Cross pair: quote (JPY) and base (EUR) both differ from the USD account currency, so
    // ResolveQuoteToAccountRate cannot derive a conversion and returns 0m.
    private static readonly InstrumentKey Cross = new("FX:EUR/JPY");
    private static readonly InstrumentKey Instrument = new("FX:GBP/USD");
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task UnresolvableConversion_IsNotFabricatedAsOneForPreTradeRisk()
    {
        var spy = new SpyRiskManager();
        var coordinator = new ExecutionCoordinator(riskManager: spy);

        await coordinator.ProcessAsync(
            PortfolioApprovedDecision(Cross),
            new ProbeBroker { Accounts = [UsdAccount()] });

        Assert.That(spy.SeenRate, Is.Zero,
            "A missing conversion must reach the risk manager as zero so its own guard engages, " +
            "not as a fabricated 1.0 that makes the monetary caps meaningless.");
    }

    [Test]
    public async Task ResolvableConversion_IsStillPassedThrough()
    {
        var spy = new SpyRiskManager();
        var coordinator = new ExecutionCoordinator(riskManager: spy);

        // Quote currency (USD) matches the account currency, so the rate resolves to 1.0 legitimately.
        await coordinator.ProcessAsync(
            PortfolioApprovedDecision(Instrument),
            new ProbeBroker { Accounts = [UsdAccount()] });

        Assert.That(spy.SeenRate, Is.EqualTo(1m));
    }

    [Test]
    public async Task MonetaryCapWithNoConversion_IsRejectedRatherThanCheckedAgainstAFakeRate()
    {
        var coordinator = new ExecutionCoordinator(
            riskManager: new PreTradeRiskManager(new PreTradeRiskOptions
            {
                MaximumLossPercentageOfBalance = 0.5m
            }));

        OrderSubmission? result = await coordinator.ProcessAsync(
            PortfolioApprovedDecision(Cross),
            new ProbeBroker { Accounts = [UsdAccount()] });

        Assert.Multiple(() =>
        {
            Assert.That(result!.Status, Is.EqualTo(SubmissionStatus.Rejected));
            Assert.That(result.RejectionReason, Does.Contain("conversion rate"));
        });
    }

    [Test]
    public async Task CloseOnAHedgedBook_IsRejectedRatherThanGuessingASide()
    {
        var coordinator = new ExecutionCoordinator();
        var broker = new ProbeBroker
        {
            Accounts = [UsdAccount()],
            Positions =
            [
                new BrokerPosition
                {
                    PositionId = "long", Instrument = Instrument,
                    Side = OrderSide.Buy, Quantity = 1_000m
                },
                new BrokerPosition
                {
                    PositionId = "short", Instrument = Instrument,
                    Side = OrderSide.Sell, Quantity = 400m
                }
            ]
        };

        InvalidOperationException? error = Assert.ThrowsAsync<InvalidOperationException>(
            async () => await coordinator.ProcessAsync(CloseDecision(), broker));

        Assert.That(error!.Message, Does.Contain("ambiguous"));
    }

    [Test]
    public async Task CloseWithSameSidePositions_AggregatesTheClosableQuantity()
    {
        var coordinator = new ExecutionCoordinator();
        var broker = new ProbeBroker
        {
            Accounts = [UsdAccount()],
            Positions =
            [
                new BrokerPosition
                {
                    PositionId = "a", Instrument = Instrument,
                    Side = OrderSide.Buy, Quantity = 600m
                },
                new BrokerPosition
                {
                    PositionId = "b", Instrument = Instrument,
                    Side = OrderSide.Buy, Quantity = 400m
                }
            ]
        };

        await coordinator.ProcessAsync(CloseDecision() with { SuggestedQuantity = 1_000m }, broker);

        PlaceOrderRequest request = broker.PlacedRequests.Single();
        Assert.Multiple(() =>
        {
            // Both legs are long, so the close is a sell for the aggregate, not just the first leg.
            Assert.That(request.Side, Is.EqualTo(OrderSide.Sell));
            Assert.That(request.Quantity.Value, Is.EqualTo(1_000m));
            Assert.That(request.ReduceOnly, Is.True);
        });
    }

    [Test]
    public async Task CompletedAmendment_ReleasesItsCacheEntry()
    {
        var coordinator = new ExecutionCoordinator();
        var broker = new ProtectiveProbeBroker
        {
            Accounts = [UsdAccount()],
            Positions =
            [
                new BrokerPosition
                {
                    PositionId = "position", Instrument = Instrument,
                    Side = OrderSide.Buy, Quantity = 1_000m
                }
            ],
            OpenOrders =
            [
                new BrokerOrder
                {
                    BrokerOrderId = "stop-1", Instrument = Instrument,
                    Side = OrderSide.Sell, Type = nameof(StandardOrderType.Stop),
                    Status = nameof(OrderStatus.Open), NormalizedStatus = OrderStatus.Open,
                    Quantity = 1_000m, Price = 1.295m
                }
            ]
        };

        ProtectiveStopAmendmentResult first = await coordinator.AmendProtectiveStopAsync(Amendment(), broker);
        ProtectiveStopAmendmentResult second = await coordinator.AmendProtectiveStopAsync(Amendment(), broker);

        Assert.Multiple(() =>
        {
            Assert.That(first.Status, Is.EqualTo(ProtectiveStopAmendmentStatus.Accepted));
            Assert.That(second.Status, Is.EqualTo(ProtectiveStopAmendmentStatus.Accepted));
            // A retained entry would replay the first completed task and never reach the broker
            // again. Two calls proves the slot was released rather than held forever.
            Assert.That(broker.AmendCallCount, Is.EqualTo(2),
                "A completed amendment must release its cache slot, or the map grows without bound.");
        });
    }

    [Test]
    public void AmendmentWithAnAbsurdPriceIncrement_IsRejectedRatherThanThrowing()
    {
        var coordinator = new ExecutionCoordinator();
        var broker = new ProbeBroker { Accounts = [UsdAccount()] };

        // decimal's smallest positive value is 1e-28, so the ratio only leaves range at a price
        // well above 7.92: 99.8 / 1e-28 is ~9.98e29, past decimal.MaxValue.
        ProtectiveStopAmendmentResult result = coordinator.AmendProtectiveStopAsync(
            Amendment() with
            {
                EntryPrice = 100m,
                InitialStopPrice = 99m,
                CurrentStopPrice = 99.5m,
                ProposedStopPrice = 99.8m,
                CurrentExecutablePrice = 101m,
                MinimumPriceIncrement = 0.0000000000000000000000000001m
            },
            broker).GetAwaiter().GetResult();

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(ProtectiveStopAmendmentStatus.Rejected));
            Assert.That(result.RejectionReason, Does.Contain("not positive"));
        });
    }

    private static ProtectiveStopAmendmentCommand Amendment() => new()
    {
        Instrument = Instrument,
        StrategyId = "strategy",
        SetupId = "setup",
        PositionId = "position",
        ExistingStopOrderId = "stop-1",
        PositionSide = OrderSide.Buy,
        PositionQuantity = 1_000m,
        EntryPrice = 1.30m,
        InitialStopPrice = 1.29m,
        CurrentStopPrice = 1.295m,
        ProposedStopPrice = 1.298m,
        CurrentExecutablePrice = 1.31m,
        MinimumPriceIncrement = 0.0001m,
        OpenProfitR = 1.5m,
        AmendmentReason = StopAmendmentReason.ProfitFloor,
        Reason = "trail",
        CreatedAt = Now,
        RequestedSequence = 1,
        EffectiveFromExecutionSequence = 2
    };

    private static AccountSnapshot UsdAccount() => new()
    {
        AccountId = "account",
        Currency = "USD",
        Balance = 100_000m,
        MarginUsed = 0m,
        CanTrade = true
    };

    private static AgentDecision PortfolioApprovedDecision(InstrumentKey instrument) => new()
    {
        DecisionId = $"decision-{instrument.Value}",
        Action = AgentAction.Buy,
        Instrument = instrument,
        SuggestedQuantity = 1_000m,
        QuantityUnit = QuantityUnit.Units,
        OrderType = StandardOrderType.Market,
        Confidence = 90m,
        CreatedAt = Now,
        Reason = "test",
        ReferencePrice = 165m,
        StopLossPrice = 164m,
        QuantityIsPortfolioApproved = true,
        PortfolioReservationId = "res-1",
        PortfolioAllocatedQuantity = 1_000m
    };

    private static AgentDecision CloseDecision() => new()
    {
        DecisionId = "decision-close",
        Action = AgentAction.Close,
        Instrument = Instrument,
        SuggestedQuantity = 500m,
        QuantityUnit = QuantityUnit.Units,
        OrderType = StandardOrderType.Market,
        Confidence = 90m,
        CreatedAt = Now,
        Reason = "close"
    };

    private sealed class SpyRiskManager : IPreTradeRiskManager
    {
        public decimal SeenRate { get; private set; } = -1m;

        public RiskAssessment Evaluate(PreTradeRiskContext context)
        {
            SeenRate = context.QuoteToAccountCurrencyRate;
            return new RiskAssessment { Approved = true, Reasons = [] };
        }
    }

    private sealed class ProtectiveProbeBroker : IProtectiveOrderBrokerClient
    {
        private readonly Orders_ _orders;

        public ProtectiveProbeBroker() => _orders = new Orders_(this);

        public int AmendCallCount { get; private set; }
        public BrokerDescriptor Descriptor { get; } = new(BrokerKind.Oanda, BrokerEnvironment.Demo, "account");
        public IReadOnlyList<AccountSnapshot> Accounts { get; init; } = [];
        public IReadOnlyList<BrokerPosition> Positions { get; init; } = [];
        public IReadOnlyList<BrokerOrder> OpenOrders { get; init; } = [];
        public TradingBrokerCapabilities TradingCapabilities { get; } = new()
        {
            SupportsNativeStopAmendment = true,
            SupportsAtomicOrderReplacement = true,
            SupportsDependentOcoAmendment = true
        };
        public IProtectiveOrderClient ProtectiveOrders => new Protective(this);
        public IMarketDataClient MarketData => new Md();
        IAccountClient IBrokerClient.Accounts => new Acc(this);
        ITradingOrderClient ITradingBrokerClient.Orders => _orders;
        IOrderClient IBrokerClient.Orders => _orders;
        IPositionClient IBrokerClient.Positions => new Pos(this);
        ICostClient IBrokerClient.Costs => new Cost();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Protective(ProtectiveProbeBroker owner) : IProtectiveOrderClient
        {
            public Task<ProtectiveStopAmendmentResult> AmendProtectiveStopAsync(
                AmendProtectiveStopRequest request, CancellationToken c = default)
            {
                owner.AmendCallCount++;
                return Task.FromResult(new ProtectiveStopAmendmentResult
                {
                    Status = ProtectiveStopAmendmentStatus.Accepted,
                    ClientAmendmentId = request.ClientAmendmentId!,
                    PreviousStopOrderId = request.ExistingStopOrderId,
                    CurrentStopOrderId = request.ExistingStopOrderId,
                    RequestedStopPrice = request.NewStopPrice,
                    AcceptedStopPrice = request.NewStopPrice,
                    Certainty = ExecutionCertainty.Accepted
                });
            }
        }

        private sealed class Acc(ProtectiveProbeBroker owner) : IAccountClient
        {
            public Task<IReadOnlyList<AccountSnapshot>> GetAccountsAsync(CancellationToken c = default) =>
                Task.FromResult(owner.Accounts);
        }

        private sealed class Pos(ProtectiveProbeBroker owner) : IPositionClient
        {
            public Task<IReadOnlyList<BrokerPosition>> GetOpenPositionsAsync(CancellationToken c = default) =>
                Task.FromResult(owner.Positions);
        }

        private sealed class Orders_(ProtectiveProbeBroker owner) : ITradingOrderClient
        {
            public Task<IReadOnlyList<BrokerOrder>> GetOpenOrdersAsync(
                InstrumentKey? instrument = null, CancellationToken c = default) =>
                Task.FromResult(owner.OpenOrders);
            public Task<OrderSubmission> PlaceOrderAsync(PlaceOrderRequest r, CancellationToken c = default) =>
                Task.FromResult(new OrderSubmission
                {
                    ClientOrderId = r.ClientOrderId!,
                    Status = SubmissionStatus.Accepted,
                    Certainty = ExecutionCertainty.Accepted
                });
            public Task CancelOrderAsync(string id, CancellationToken c = default) => Task.CompletedTask;
            public async IAsyncEnumerable<OrderEvent> StreamOrderEventsAsync(
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken c = default)
            { await Task.CompletedTask; yield break; }
        }

        private sealed class Md : IMarketDataClient
        {
            public Task<IReadOnlyList<Candle>> GetCandlesAsync(CandleQuery q, CancellationToken c = default) =>
                Task.FromResult<IReadOnlyList<Candle>>([]);
        }

        private sealed class Cost : ICostClient
        {
            public Task<CommissionSchedule> GetCommissionAsync(InstrumentKey i, CancellationToken c = default) =>
                Task.FromResult(new CommissionSchedule { Instrument = i });
        }
    }

    private sealed class ProbeBroker : ITradingBrokerClient
    {
        private readonly Orders_ _orders;

        public ProbeBroker() => _orders = new Orders_(this);

        public BrokerDescriptor Descriptor { get; } = new(BrokerKind.Oanda, BrokerEnvironment.Demo, "account");
        public IReadOnlyList<AccountSnapshot> Accounts { get; init; } = [];
        public IReadOnlyList<BrokerPosition> Positions { get; init; } = [];
        public List<PlaceOrderRequest> PlacedRequests { get; } = [];
        public IMarketDataClient MarketData => new Md();
        IAccountClient IBrokerClient.Accounts => new Acc(this);
        ITradingOrderClient ITradingBrokerClient.Orders => _orders;
        IOrderClient IBrokerClient.Orders => _orders;
        IPositionClient IBrokerClient.Positions => new Pos(this);
        ICostClient IBrokerClient.Costs => new Cost();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Acc(ProbeBroker owner) : IAccountClient
        {
            public Task<IReadOnlyList<AccountSnapshot>> GetAccountsAsync(CancellationToken c = default) =>
                Task.FromResult(owner.Accounts);
        }

        private sealed class Pos(ProbeBroker owner) : IPositionClient
        {
            public Task<IReadOnlyList<BrokerPosition>> GetOpenPositionsAsync(CancellationToken c = default) =>
                Task.FromResult(owner.Positions);
        }

        private sealed class Orders_(ProbeBroker owner) : ITradingOrderClient
        {
            public Task<IReadOnlyList<BrokerOrder>> GetOpenOrdersAsync(
                InstrumentKey? instrument = null, CancellationToken c = default) =>
                Task.FromResult<IReadOnlyList<BrokerOrder>>([]);

            public Task<OrderSubmission> PlaceOrderAsync(PlaceOrderRequest request, CancellationToken c = default)
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

            public Task CancelOrderAsync(string brokerOrderId, CancellationToken c = default) =>
                Task.CompletedTask;

            public async IAsyncEnumerable<OrderEvent> StreamOrderEventsAsync(
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken c = default)
            { await Task.CompletedTask; yield break; }
        }

        private sealed class Md : IMarketDataClient
        {
            public Task<IReadOnlyList<Candle>> GetCandlesAsync(CandleQuery q, CancellationToken c = default) =>
                Task.FromResult<IReadOnlyList<Candle>>([]);
        }

        private sealed class Cost : ICostClient
        {
            public Task<CommissionSchedule> GetCommissionAsync(InstrumentKey i, CancellationToken c = default) =>
                Task.FromResult(new CommissionSchedule { Instrument = i });
        }
    }
}
