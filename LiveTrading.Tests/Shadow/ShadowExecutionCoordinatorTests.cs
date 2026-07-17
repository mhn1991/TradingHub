using Agent.Models;
using Brokers.Abstractions;
using Brokers.Models;
using LiveTrading.Shadow;
using NUnit.Framework;

namespace LiveTrading.Tests.Shadow;

[TestFixture]
public sealed class ShadowExecutionCoordinatorTests
{
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");

    private static AgentDecision Decision(AgentAction action) => new()
    {
        Action = action,
        Instrument = Instrument,
        Confidence = 0.8m,
        CreatedAt = DateTimeOffset.UtcNow,
        Reason = "test"
    };

    /// <summary>Every member throws on access - proves <see cref="ShadowExecutionCoordinator"/>
    /// behaviorally, not just by reading its source, never touches the broker it is handed.</summary>
    private sealed class ThrowingBrokerClient : ITradingBrokerClient
    {
        public BrokerDescriptor Descriptor => throw new InvalidOperationException("Should not be accessed.");
        public IMarketDataClient MarketData => throw new InvalidOperationException("Should not be accessed.");
        public IAccountClient Accounts => throw new InvalidOperationException("Should not be accessed.");
        public IPositionClient Positions => throw new InvalidOperationException("Should not be accessed.");
        public ICostClient Costs => throw new InvalidOperationException("Should not be accessed.");
        public ITradingOrderClient Orders => throw new InvalidOperationException("Should not be accessed.");
        IOrderClient IBrokerClient.Orders => throw new InvalidOperationException("Should not be accessed.");
        public ValueTask DisposeAsync() => throw new InvalidOperationException("Should not be accessed.");
    }

    [Test]
    public async Task ProcessAsync_NeverTouchesTheBrokerItIsHanded()
    {
        var coordinator = new ShadowExecutionCoordinator();
        var throwingBroker = new ThrowingBrokerClient();

        OrderSubmission? result = await coordinator.ProcessAsync(Decision(AgentAction.Buy), throwingBroker, CancellationToken.None);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task ProcessAsync_ObserveDecision_AlsoReturnsNull()
    {
        var coordinator = new ShadowExecutionCoordinator();
        var broker = new ThrowingBrokerClient();

        OrderSubmission? result = await coordinator.ProcessAsync(Decision(AgentAction.Observe), broker, CancellationToken.None);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void AmendProtectiveStopAsync_AlwaysThrows()
    {
        var coordinator = new ShadowExecutionCoordinator();
        var broker = new ThrowingBrokerClient();
        var command = new ExecutionManager.ProtectiveStopAmendmentCommand
        {
            Instrument = Instrument,
            StrategyId = "strategy",
            SetupId = "setup",
            PositionId = "position",
            PositionSide = OrderSide.Buy,
            PositionQuantity = 1_000m,
            EntryPrice = 1.1m,
            InitialStopPrice = 1.095m,
            CurrentStopPrice = 1.095m,
            ProposedStopPrice = 1.098m,
            CurrentExecutablePrice = 1.101m,
            MinimumPriceIncrement = 0.0001m,
            OpenProfitR = 0.5m,
            AmendmentReason = StopAmendmentReason.BreakEven,
            Reason = "test",
            CreatedAt = DateTimeOffset.UtcNow,
            RequestedSequence = 1,
            EffectiveFromExecutionSequence = 2
        };

        Assert.ThrowsAsync<InvalidOperationException>(
            async () => await coordinator.AmendProtectiveStopAsync(command, broker, CancellationToken.None));
    }
}
