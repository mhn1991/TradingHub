using Brokers;
using Brokers.Models;
using Brokers.Oanda;
using LiveTrading.MarketData;
using LiveTrading.Oanda;
using NUnit.Framework;

namespace LiveTrading.Tests;

/// <summary>
/// Real-network tests only - mirrors <c>Brokers.IntegrationTests/OandaIntegrationTests.cs</c>'s
/// [Category("Integration")]/[Explicit]/[NonParallelizable] convention exactly, so these never
/// run automatically (no OANDA credentials or network egress exist in this sandboxed
/// environment - confirmed empty via <c>env | grep -i oanda</c> during Phase 1 planning). Run
/// manually with real OANDA practice credentials via OANDA_TOKEN / OANDA_ACCOUNT_ID env vars.
/// </summary>
[TestFixture]
[Category("Integration")]
[NonParallelizable]
public sealed class OandaAdapterIntegrationTests
{
    private OandaBrokerClient _broker = null!;
    private OandaInstrumentMap _map = null!;
    private InstrumentKey _instrument;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        string? token = Environment.GetEnvironmentVariable("OANDA_TOKEN");
        _instrument = new InstrumentKey(Environment.GetEnvironmentVariable("OANDA_TEST_INSTRUMENT") ?? "FX:EUR/USD");
        if (string.IsNullOrWhiteSpace(token))
        {
            return; // Explicit tests below will fail with a clear message when actually run without credentials.
        }

        _broker = BrokerClientFactory.CreateOanda(new OandaOptions
        {
            Environment = Brokers.Abstractions.BrokerEnvironment.Demo,
            AccessToken = token,
            AccountId = Environment.GetEnvironmentVariable("OANDA_ACCOUNT_ID") ?? "not-used-by-market-data-tests"
        });
        _map = new OandaInstrumentMap(new Dictionary<string, string>());
    }

    [Test]
    [Explicit("Calls the real OANDA pricing stream using environment-variable credentials.")]
    public async Task OandaLiveQuoteStream_ReceivesAtLeastOneTradeableTick()
    {
        RequireBroker();
        var stream = new OandaLiveQuoteStream(_broker, _map, TimeProvider.System);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await foreach (LiveQuoteEvent tick in stream.ReadAsync([_instrument], timeout.Token))
        {
            Assert.That(tick.Instrument, Is.EqualTo(_instrument));
            Assert.That(tick.Quote.Bid, Is.GreaterThan(0m));
            return;
        }

        Assert.Fail("No ticks were received within the timeout.");
    }

    [Test]
    [Explicit("Calls the real OANDA REST candle endpoint using environment-variable credentials.")]
    public async Task OandaCompletedCandleProvider_FiltersOutIncompleteTrailingCandles()
    {
        RequireBroker();
        var provider = new OandaCompletedCandleProvider(_broker);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IReadOnlyList<Candle> candles = await provider.GetCompletedCandlesAsync(
            _instrument, BarInterval.Minutes(1), now.AddHours(-2), now, CancellationToken.None);

        Assert.That(candles, Is.Not.Null);
        Assert.That(candles, Has.All.Matches<Candle>(c => c.IsComplete));
    }
    [Test]
    [Explicit("Places and cleans up a real tiny OANDA Practice trade. Set OANDA_ENABLE_WRITE_TESTS=true explicitly.")]
    public async Task ProtectedEntry_PartialClose_StopReplacement_AndFinalClose()
    {
        RequireBroker();
        if (!string.Equals(
                Environment.GetEnvironmentVariable("OANDA_ENABLE_WRITE_TESTS"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            Assert.Fail("Set OANDA_ENABLE_WRITE_TESTS=true to acknowledge that this test mutates the OANDA Practice account.");
        }
        Assert.That(_broker.Descriptor.Environment, Is.EqualTo(Brokers.Abstractions.BrokerEnvironment.Demo));

        InstrumentTradingMetadata metadata = (await _broker.InstrumentMetadata
            .GetInstrumentMetadataAsync(CancellationToken.None))
            .Single(item => item.Instrument == _instrument);
        LiveQuoteSnapshot quote = await ReadQuoteAsync();
        decimal quantity = Math.Max(metadata.MinimumQuantity * 2m, metadata.QuantityStep * 2m);
        quantity = Math.Ceiling(quantity / metadata.QuantityStep) * metadata.QuantityStep;
        if (metadata.MaximumOrderQuantity is decimal maximum)
            quantity = Math.Min(quantity, maximum);
        Assert.That(quantity, Is.GreaterThanOrEqualTo(metadata.MinimumQuantity * 2m),
            "The configured instrument cannot support a two-stage partial-close certification quantity.");

        decimal stopDistance = Math.Max(metadata.PriceIncrement * 1_000m, quote.Ask * 0.002m);
        decimal initialStop = Math.Floor((quote.Bid - stopDistance) / metadata.PriceIncrement) * metadata.PriceIncrement;
        string clientId = $"th-cert-{Guid.NewGuid():N}";
        BrokerPosition? trade = null;
        try
        {
            OrderSubmission submission = await _broker.Orders.PlaceOrderAsync(new PlaceOrderRequest
            {
                Instrument = _instrument,
                Side = OrderSide.Buy,
                Type = StandardOrderType.Market,
                Quantity = new OrderQuantity(quantity, QuantityUnit.Units),
                TimeInForce = StandardTimeInForce.FillOrKill,
                ClientOrderId = clientId,
                StrategyId = "practice-certification",
                DecisionId = clientId,
                SetupId = clientId,
                StopLoss = new StopLossInstruction(initialStop)
            }, CancellationToken.None);
            Assert.That(submission.Certainty, Is.EqualTo(ExecutionCertainty.Accepted), submission.RejectionReason);

            trade = await WaitForOwnedTradeAsync(clientId, TimeSpan.FromSeconds(15));
            Assert.Multiple(() =>
            {
                Assert.That(trade.ProtectiveStopOrderId, Is.Not.Null.And.Not.Empty);
                Assert.That(trade.ProtectiveStopPrice, Is.EqualTo(initialStop));
                Assert.That(trade.Quantity, Is.EqualTo(quantity));
            });

            decimal reductionQuantity = Math.Max(metadata.MinimumQuantity, metadata.QuantityStep);
            PositionReductionResult reduction = await _broker.PositionReductions.ReducePositionAsync(
                new ReduceBrokerPositionRequest
                {
                    Instrument = _instrument,
                    PositionId = trade.PositionId,
                    PositionSide = trade.Side,
                    Quantity = reductionQuantity,
                    OwnedQuantity = trade.Quantity,
                    ClientRequestId = $"{clientId}-reduce",
                    RequestedAt = DateTimeOffset.UtcNow,
                    Reason = "OANDA Practice partial-close certification"
                }, CancellationToken.None);
            Assert.That(reduction.Certainty, Is.EqualTo(ExecutionCertainty.Accepted), reduction.Reason);

            trade = await WaitForOwnedTradeAsync(clientId, TimeSpan.FromSeconds(15));
            decimal improvedStop = Math.Floor(
                (initialStop + stopDistance / 4m) / metadata.PriceIncrement) * metadata.PriceIncrement;
            LiveQuoteSnapshot amendQuote = await ReadQuoteAsync();
            ProtectiveStopAmendmentResult amendment = await _broker.ProtectiveOrders.AmendProtectiveStopAsync(
                new AmendProtectiveStopRequest
                {
                    Instrument = _instrument,
                    ClientAmendmentId = $"{clientId}-stop",
                    PositionId = trade.PositionId,
                    ExistingStopOrderId = trade.ProtectiveStopOrderId,
                    CurrentStopPrice = trade.ProtectiveStopPrice ?? initialStop,
                    NewStopPrice = improvedStop,
                    CurrentExecutablePrice = trade.Side == OrderSide.Buy ? amendQuote.Bid : amendQuote.Ask,
                    PositionQuantity = trade.Quantity,
                    PositionSide = trade.Side,
                    MinimumPriceIncrement = metadata.PriceIncrement,
                    Reason = "OANDA Practice stop-amendment certification",
                    RequestedAt = DateTimeOffset.UtcNow,
                    EffectiveFromExecutionSequence = 1
                }, CancellationToken.None);
            Assert.That(amendment.Certainty, Is.EqualTo(ExecutionCertainty.Accepted), amendment.RejectionReason);

            trade = await WaitForOwnedTradeAsync(clientId, TimeSpan.FromSeconds(15));
            Assert.That(trade.ProtectiveStopPrice, Is.EqualTo(improvedStop));
        }
        finally
        {
            BrokerPosition? remaining = (await _broker.Positions.GetOpenPositionsAsync(CancellationToken.None))
                .SingleOrDefault(item => item.ClientTradeId == clientId);
            if (remaining is not null)
            {
                await _broker.PositionReductions.ReducePositionAsync(new ReduceBrokerPositionRequest
                {
                    Instrument = remaining.Instrument,
                    PositionId = remaining.PositionId,
                    PositionSide = remaining.Side,
                    Quantity = remaining.Quantity,
                    OwnedQuantity = remaining.Quantity,
                    ClientRequestId = $"{clientId}-cleanup",
                    RequestedAt = DateTimeOffset.UtcNow,
                    Reason = "OANDA Practice certification cleanup"
                }, CancellationToken.None);
            }
        }
    }

    private void RequireBroker()
    {
        if (_broker is null)
            Assert.Fail("Set OANDA_TOKEN and OANDA_ACCOUNT_ID before running explicit OANDA integration tests.");
    }

    private async Task<LiveQuoteSnapshot> ReadQuoteAsync()
    {
        var stream = new OandaLiveQuoteStream(_broker, _map, TimeProvider.System);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await foreach (LiveQuoteEvent item in stream.ReadAsync([_instrument], timeout.Token))
            return item.Quote;
        throw new TimeoutException("No executable OANDA quote was received.");
    }

    private async Task<BrokerPosition> WaitForOwnedTradeAsync(string clientId, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            BrokerPosition? trade = (await _broker.Positions.GetOpenPositionsAsync(CancellationToken.None))
                .SingleOrDefault(item => item.ClientTradeId == clientId);
            if (trade is not null)
                return trade;
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }
        throw new TimeoutException($"OANDA did not expose trade ownership for client id '{clientId}'.");
    }

}
