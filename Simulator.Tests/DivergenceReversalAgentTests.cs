using Agent.Models;
using Agent.Strategies.DivergenceReversal;
using Brokers.Models;
using ChartAnnotator.Indicators;
using ChartAnnotator.Models;

namespace Simulator.Tests;

[TestFixture]
public sealed class StochRsiAnalysisStateTests
{
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");
    private static readonly BarInterval Interval = BarInterval.Minutes(15);
    private static readonly DateTimeOffset Start = SimulationTestHarness.DefaultStart;

    [Test]
    public void Update_TwoLowPivots_LowerLowWithHigherFast_DetectsRegularBullishDivergence()
    {
        var state = new StochRsiAnalysisState();
        DateTimeOffset t1 = Start;
        DateTimeOffset t2 = Start.AddHours(2);

        StochRsiAnalysisSnapshot first = state.Update(
            Candle(t1, 1.1000m), stochRsiFast: 2m,
            confirmedSwings: [Swing(t1, 1.1000m, SwingType.Low)], atr: 0.0010m);
        Assert.That(first.LatestRelationship, Is.Null, "A single pivot has nothing to compare against yet.");

        StochRsiAnalysisSnapshot second = state.Update(
            Candle(t2, 1.0950m), stochRsiFast: 10m,
            confirmedSwings: [Swing(t2, 1.0950m, SwingType.Low)], atr: 0.0010m);

        Assert.That(second.IsNewRelationship, Is.True);
        Assert.That(second.LatestRelationship!.Type, Is.EqualTo(StochRsiRelationshipType.RegularBullishDivergence));
    }

    [Test]
    public void Update_TwoLowPivots_HigherLowWithLowerFast_DetectsHiddenBullishDivergence()
    {
        var state = new StochRsiAnalysisState();
        DateTimeOffset t1 = Start;
        DateTimeOffset t2 = Start.AddHours(2);

        state.Update(Candle(t1, 1.0950m), stochRsiFast: 30m,
            confirmedSwings: [Swing(t1, 1.0950m, SwingType.Low)], atr: 0.0010m);
        StochRsiAnalysisSnapshot second = state.Update(Candle(t2, 1.1000m), stochRsiFast: 15m,
            confirmedSwings: [Swing(t2, 1.1000m, SwingType.Low)], atr: 0.0010m);

        Assert.That(second.LatestRelationship!.Type, Is.EqualTo(StochRsiRelationshipType.HiddenBullishDivergence));
    }

    [Test]
    public void Update_TwoLowPivots_LowerLowWithLowerFast_DetectsConvergence()
    {
        var state = new StochRsiAnalysisState();
        DateTimeOffset t1 = Start;
        DateTimeOffset t2 = Start.AddHours(2);

        state.Update(Candle(t1, 1.1000m), stochRsiFast: 20m,
            confirmedSwings: [Swing(t1, 1.1000m, SwingType.Low)], atr: 0.0010m);
        StochRsiAnalysisSnapshot second = state.Update(Candle(t2, 1.0950m), stochRsiFast: 5m,
            confirmedSwings: [Swing(t2, 1.0950m, SwingType.Low)], atr: 0.0010m);

        Assert.That(second.LatestRelationship!.Type, Is.EqualTo(StochRsiRelationshipType.BearishConvergence));
    }

    [Test]
    public void Update_PriceChangeBelowTolerance_ProducesNoRelationship()
    {
        var state = new StochRsiAnalysisState();
        DateTimeOffset t1 = Start;
        DateTimeOffset t2 = Start.AddHours(2);

        state.Update(Candle(t1, 1.1000m), stochRsiFast: 2m,
            confirmedSwings: [Swing(t1, 1.1000m, SwingType.Low)], atr: 0.0010m);
        // Price barely moves (well under 0.05 * ATR) - not a meaningful second pivot.
        StochRsiAnalysisSnapshot second = state.Update(Candle(t2, 1.10001m), stochRsiFast: 10m,
            confirmedSwings: [Swing(t2, 1.10001m, SwingType.Low)], atr: 0.0010m);

        Assert.That(second.LatestRelationship, Is.Null);
    }

    private static Candle Candle(DateTimeOffset openTime, decimal price) =>
        TestCandles.Create(Instrument, openTime, Interval, price, price, price, price);

    private static SwingPoint Swing(DateTimeOffset pivotTime, decimal price, SwingType type) => new()
    {
        PivotTime = pivotTime,
        ConfirmedAt = pivotTime,
        Price = price,
        Type = type,
        Strength = 2
    };
}

[TestFixture]
public sealed class DivergenceReversalAgentTests
{
    private static readonly InstrumentKey Instrument = new("FX:EUR/USD");
    private static readonly BarInterval Interval = BarInterval.Minutes(15);
    private static readonly DateTimeOffset T1 = SimulationTestHarness.DefaultStart;
    private static readonly DateTimeOffset T2 = SimulationTestHarness.DefaultStart.AddHours(2);

    private static DivergenceReversalAgent CreateAgent() => new(new DivergenceReversalStrategyOptions
    {
        MonitoredIntervals = [Interval],
        Quantity = 1_000m,
        // Relaxed from the 0/100 production default so a divergence gap (which itself needs
        // >= MinimumStochRsiFastDifference between pivots) can still land within "extreme".
        StochRsiFastOversold = 12m,
        StochRsiFastOverbought = 88m
    });

    [Test]
    public async Task EvaluateAsync_RegularBullishDivergenceAtExtreme_WhileFlat_ReturnsBuyWithProtectiveStop()
    {
        DivergenceReversalAgent agent = CreateAgent();

        // First pivot: seeds the StochRSI-fast/price history, nothing to compare against yet.
        await agent.EvaluateAsync(Context(agent, [], Snapshot(T1, close: 1.1000m, low: 1.1000m,
            bollingerLower: 1.0995m, rsi: 20m, stochRsiFast: 2m,
            swings: [Swing(T1, 1.1000m, SwingType.Low)])));

        // Second pivot: lower low, but StochRSI-fast makes a higher low (weakening downside
        // momentum) - regular bullish divergence at a bullish extreme.
        AgentDecision decision = await agent.EvaluateAsync(Context(agent, [], Snapshot(T2, close: 1.0950m, low: 1.0950m,
            bollingerLower: 1.0995m, rsi: 20m, stochRsiFast: 10m,
            swings: [Swing(T2, 1.0950m, SwingType.Low)])));

        Assert.That(decision.Action, Is.EqualTo(AgentAction.Buy));
        Assert.That(decision.ReferencePrice, Is.Not.Null);
        Assert.That(decision.StopLossPrice, Is.Not.Null.And.LessThan(decision.ReferencePrice!.Value));
    }

    [Test]
    public async Task EvaluateAsync_RegularDivergenceOpposingExistingPosition_ClosesThenFlipsOnNextEvaluation()
    {
        DivergenceReversalAgent agent = CreateAgent();
        var openLong = new BrokerPosition
        {
            PositionId = "p1",
            Instrument = Instrument,
            Side = OrderSide.Buy,
            Quantity = 1_000m
        };

        await agent.EvaluateAsync(Context(agent, [openLong], Snapshot(T1, close: 1.1050m, high: 1.1050m,
            bollingerUpper: 1.1045m, rsi: 80m, stochRsiFast: 98m,
            swings: [Swing(T1, 1.1050m, SwingType.High)])));

        AgentDecision closeDecision = await agent.EvaluateAsync(Context(agent, [openLong], Snapshot(T2, close: 1.1100m, high: 1.1100m,
            bollingerUpper: 1.1045m, rsi: 80m, stochRsiFast: 90m,
            swings: [Swing(T2, 1.1100m, SwingType.High)])));

        Assert.That(closeDecision.Action, Is.EqualTo(AgentAction.Close));

        // Now flat (the position closed) - the agent should open the opposite side without
        // needing to re-detect the signal.
        AgentDecision flipDecision = await agent.EvaluateAsync(Context(agent, [], Snapshot(T2.AddMinutes(15), close: 1.1090m,
            high: 1.1090m, bollingerUpper: 1.1045m, rsi: 80m, stochRsiFast: 90m, swings: [])));

        Assert.That(flipDecision.Action, Is.EqualTo(AgentAction.Sell));
    }

    [Test]
    public async Task EvaluateAsync_ConvergenceAtExtremeOpposingExistingPosition_NeverCloses()
    {
        DivergenceReversalAgent agent = CreateAgent();
        var openLong = new BrokerPosition
        {
            PositionId = "p1",
            Instrument = Instrument,
            Side = OrderSide.Buy,
            Quantity = 1_000m
        };

        await agent.EvaluateAsync(Context(agent, [openLong], Snapshot(T1, close: 1.1050m, high: 1.1050m,
            bollingerUpper: 1.1045m, rsi: 80m, stochRsiFast: 40m,
            swings: [Swing(T1, 1.1050m, SwingType.High)])));

        // Higher high AND a higher StochRSI-fast reading at the pivot: momentum confirms the
        // extreme (convergence), not divergence - a breakout continuation, which must never close
        // an existing position on its own.
        AgentDecision decision = await agent.EvaluateAsync(Context(agent, [openLong], Snapshot(T2, close: 1.1100m, high: 1.1100m,
            bollingerUpper: 1.1045m, rsi: 80m, stochRsiFast: 90m,
            swings: [Swing(T2, 1.1100m, SwingType.High)])));

        Assert.That(decision.Action, Is.Not.EqualTo(AgentAction.Close));
    }

    [Test]
    public async Task EvaluateAsync_PartialReadingOnTrigger_ConfirmedByLowerTimeframe_ReturnsBuy()
    {
        BarInterval trigger = BarInterval.Minutes(15);
        BarInterval confirmation = BarInterval.Minutes(5);
        var agent = new DivergenceReversalAgent(new DivergenceReversalStrategyOptions
        {
            MonitoredIntervals = [trigger],
            ConfirmationIntervals = [confirmation],
            Quantity = 1_000m,
            StochRsiFastOversold = 12m,
            StochRsiFastOverbought = 88m
        });

        // Seed the confirmation timeframe's first Low pivot. Confirmation intervals are only ever
        // touched when the trigger itself reads as at least Partial, so the trigger must also show
        // a (here, irrelevant-to-the-outcome) partial reading on this seeding call.
        await agent.EvaluateAsync(Context(agent, [],
            new Dictionary<BarInterval, AnalysisSnapshot>
            {
                [trigger] = Snapshot(T1, close: 1.1000m, low: 1.0995m, rsi: 50m, stochRsiFast: 50m,
                    bollingerLower: 1.0995m, swings: []),
                [confirmation] = Snapshot(T1, close: 1.1000m, low: 1.1000m, rsi: 20m, stochRsiFast: 2m,
                    bollingerLower: 1.0995m, swings: [Swing(T1, 1.1000m, SwingType.Low)])
            },
            T1));

        // Trigger timeframe only touches the lower band (partial - close stays above it) while the
        // confirmation timeframe makes a lower low with a higher StochRSI-fast reading (regular
        // bullish divergence) and *fully* qualifies (close at/below the band, RSI/StochRSI extreme).
        AgentDecision decision = await agent.EvaluateAsync(Context(agent, [],
            new Dictionary<BarInterval, AnalysisSnapshot>
            {
                [trigger] = Snapshot(T2, close: 1.1000m, low: 1.0995m, rsi: 50m, stochRsiFast: 50m,
                    bollingerLower: 1.0995m, swings: []),
                [confirmation] = Snapshot(T2, close: 1.0950m, low: 1.0950m, rsi: 20m, stochRsiFast: 10m,
                    bollingerLower: 1.0995m, swings: [Swing(T2, 1.0950m, SwingType.Low)])
            },
            T2));

        Assert.That(decision.Action, Is.EqualTo(AgentAction.Buy));
        Assert.That(decision.SignalInterval, Is.EqualTo(confirmation));
        Assert.That(decision.Reason, Does.Contain("partial reading confirmed"));
    }

    private static AgentMarketContext Context(
        DivergenceReversalAgent agent,
        IReadOnlyList<BrokerPosition> positions,
        AnalysisSnapshot snapshot) => Context(
            agent,
            positions,
            agent.RequiredIntervals.ToDictionary(interval => interval, _ => snapshot),
            snapshot.LatestCandle.OpenTime);

    private static AgentMarketContext Context(
        DivergenceReversalAgent agent,
        IReadOnlyList<BrokerPosition> positions,
        IReadOnlyDictionary<BarInterval, AnalysisSnapshot> snapshots,
        DateTimeOffset timestamp) => new()
    {
        Instrument = Instrument,
        Timestamp = timestamp,
        Analysis = new MultiTimeframeAnalysis(Instrument, timestamp, snapshots),
        Account = new AccountSnapshot { AccountId = "account", CanTrade = true },
        Positions = positions,
        OpenOrders = []
    };

    private static AnalysisSnapshot Snapshot(
        DateTimeOffset openTime,
        decimal close,
        decimal rsi,
        decimal stochRsiFast,
        IReadOnlyList<SwingPoint> swings,
        decimal? high = null,
        decimal? low = null,
        decimal? bollingerUpper = null,
        decimal? bollingerLower = null) => new()
    {
        Instrument = Instrument,
        Interval = Interval,
        AvailableAt = openTime,
        Version = 1,
        LatestCandle = TestCandles.Create(
            Instrument, openTime, Interval,
            open: close, high: high ?? close, low: low ?? close, close: close),
        Indicators = new IndicatorSnapshot
        {
            Atr = 0.0010m,
            Rsi = rsi,
            StochRsi = new StochRsiSnapshot { Fast = stochRsiFast, Slow = stochRsiFast },
            BollingerMiddle = 1.1000m,
            BollingerUpper = bollingerUpper ?? 1.1100m,
            BollingerLower = bollingerLower ?? 1.0900m
        },
        Swings = swings,
        PriceZones = [],
        Trendlines = [],
        Channels = [],
        Confidence = new ConfidenceScore { Total = 50m, Contributions = [] }
    };

    private static SwingPoint Swing(DateTimeOffset pivotTime, decimal price, SwingType type) => new()
    {
        PivotTime = pivotTime,
        ConfirmedAt = pivotTime,
        Price = price,
        Type = type,
        Strength = 2
    };
}
