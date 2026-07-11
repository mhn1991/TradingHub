using TradingHub.Application.Orders;
using TradingHub.Application.Portfolio;
using TradingHub.Application.Risk;
using TradingHub.Application.Strategies;
using TradingHub.Domain.Markets;
using TradingHub.Domain.Trading;

namespace TradingHub.Application.Trading;

public sealed class TradingSession
{
    private readonly TradingSessionOptions _options;
    private readonly TradingSessionServices _services;
    private readonly Dictionary<string, OrderStatus> _orderStatuses = new(StringComparer.Ordinal);

    public TradingSession(TradingSessionOptions options, TradingSessionServices services)
    {
        _options = options;
        _services = services;
        Portfolio = new PortfolioBook(options.InitialBalance, options.AccountCurrency);
    }

    public PortfolioBook Portfolio { get; }

    public List<TradeIntent> Intents { get; } = [];

    public List<RiskEvaluation> RiskEvaluations { get; } = [];

    public List<OrderSubmissionRecord> Orders { get; } = [];

    public List<ExecutionReport> Executions { get; } = [];

    public List<EquityPoint> EquityCurve { get; } = [];

    public async Task OnBarClosedAsync(PriceBar bar, CancellationToken cancellationToken = default)
    {
        var instrument = GetInstrument(bar.InstrumentId);
        Portfolio.MarkToMarket(bar, instrument);

        foreach (var strategy in _services.Strategies)
        {
            var context = CreateStrategyContext(bar.InstrumentId);
            var intents = await strategy.OnBarAsync(context, bar, cancellationToken);
            foreach (var intent in intents)
            {
                await ProcessIntentAsync(intent, bar, instrument, cancellationToken);
            }
        }

        RecordEquity(bar.CloseTime);
    }

    public void ApplyExecutionReports(IEnumerable<ExecutionReport> reports)
    {
        foreach (var report in reports)
        {
            var instrument = GetInstrument(report.InstrumentId);
            Executions.Add(report);
            _orderStatuses[report.OrderId] = report.Status;
            Portfolio.ApplyExecution(report, instrument);
        }
    }

    private async Task ProcessIntentAsync(
        TradeIntent intent,
        PriceBar bar,
        InstrumentDefinition instrument,
        CancellationToken cancellationToken)
    {
        Intents.Add(intent);
        var context = CreateRiskContext(bar, instrument);
        var decision = _services.RiskEngine.Evaluate(intent, context);
        RiskEvaluations.Add(new RiskEvaluation { Intent = intent, Decision = decision });

        if (!decision.IsApproved)
        {
            return;
        }

        var order = _services.OrderFactory.Create(intent);
        var result = await _services.Broker.SubmitOrderAsync(order, cancellationToken);
        Orders.Add(new OrderSubmissionRecord { Request = order, Result = result });
        _orderStatuses[order.OrderId] = result.Status;

        if (result.ImmediateExecutions.Count > 0)
        {
            ApplyExecutionReports(result.ImmediateExecutions);
        }
    }

    private StrategyContext CreateStrategyContext(string instrumentId)
    {
        return new StrategyContext
        {
            AccountId = _options.AccountId,
            NetPositionQuantity = Portfolio.GetNetQuantity(instrumentId)
        };
    }

    private RiskContext CreateRiskContext(PriceBar bar, InstrumentDefinition instrument)
    {
        var openOrderCount = _orderStatuses.Values.Count(IsOpenStatus);
        return new RiskContext
        {
            Now = _services.Clock.UtcNow,
            Instrument = instrument,
            LatestBar = bar,
            CurrentNetQuantity = Portfolio.GetNetQuantity(bar.InstrumentId),
            OpenOrderCount = openOrderCount
        };
    }

    private InstrumentDefinition GetInstrument(string instrumentId)
    {
        return _options.Instruments.TryGetValue(instrumentId, out var instrument)
            ? instrument
            : throw new KeyNotFoundException($"Instrument '{instrumentId}' is not configured.");
    }

    private void RecordEquity(DateTimeOffset timestamp)
    {
        var unrealized = Portfolio.Positions.Sum(position => position.UnrealizedPnl);
        EquityCurve.Add(new EquityPoint
        {
            Timestamp = timestamp,
            Balance = Portfolio.Balance,
            UnrealizedPnl = unrealized,
            Equity = Portfolio.CalculateEquity()
        });
    }

    private static bool IsOpenStatus(OrderStatus status)
    {
        return status is OrderStatus.Submitted
            or OrderStatus.Acknowledged
            or OrderStatus.PartiallyFilled
            or OrderStatus.CancelPending
            or OrderStatus.Unknown;
    }
}
