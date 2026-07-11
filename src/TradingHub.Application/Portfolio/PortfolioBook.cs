using TradingHub.Domain.Markets;
using TradingHub.Domain.Trading;

namespace TradingHub.Application.Portfolio;

public sealed class PortfolioBook
{
    private readonly Dictionary<string, PositionState> _positions = new(StringComparer.Ordinal);

    public PortfolioBook(decimal initialBalance, string accountCurrency)
    {
        if (initialBalance <= 0m || string.IsNullOrWhiteSpace(accountCurrency))
        {
            throw new ArgumentException("A positive balance and account currency are required.");
        }

        Balance = initialBalance;
        AccountCurrency = accountCurrency;
    }

    public decimal Balance { get; private set; }

    public decimal RealizedPnl { get; private set; }

    public decimal FeesPaid { get; private set; }

    public string AccountCurrency { get; }

    public IReadOnlyCollection<PositionState> Positions => _positions.Values;

    public decimal GetNetQuantity(string instrumentId)
    {
        return _positions.TryGetValue(instrumentId, out var position) ? position.NetQuantity : 0m;
    }

    public void ApplyExecution(ExecutionReport report, InstrumentDefinition instrument)
    {
        if (report.LastFillQuantity <= 0m || report.LastFillPrice is null)
        {
            return;
        }

        var position = GetOrCreate(report.InstrumentId);
        var signedFill = report.Side == OrderSide.Buy
            ? report.LastFillQuantity
            : -report.LastFillQuantity;

        ApplyFill(position, signedFill, report.LastFillPrice.Value, instrument.ContractSize);
        Balance -= report.Fee;
        FeesPaid += report.Fee;
    }

    public void MarkToMarket(PriceBar bar, InstrumentDefinition instrument)
    {
        var position = GetOrCreate(bar.InstrumentId);
        position.UnrealizedPnl = position.NetQuantity switch
        {
            > 0m => position.NetQuantity * (bar.Bid.Close - position.AveragePrice) * instrument.ContractSize,
            < 0m => Math.Abs(position.NetQuantity) * (position.AveragePrice - bar.Ask.Close) * instrument.ContractSize,
            _ => 0m
        };
    }

    public decimal CalculateEquity()
    {
        return Balance + _positions.Values.Sum(position => position.UnrealizedPnl);
    }

    private PositionState GetOrCreate(string instrumentId)
    {
        if (!_positions.TryGetValue(instrumentId, out var position))
        {
            position = new PositionState(instrumentId);
            _positions.Add(instrumentId, position);
        }

        return position;
    }

    private void ApplyFill(PositionState position, decimal signedFill, decimal fillPrice, decimal contractSize)
    {
        var previousQuantity = position.NetQuantity;
        var resultingQuantity = previousQuantity + signedFill;

        if (previousQuantity == 0m || Math.Sign(previousQuantity) == Math.Sign(signedFill))
        {
            var previousCost = Math.Abs(previousQuantity) * position.AveragePrice;
            var fillCost = Math.Abs(signedFill) * fillPrice;
            position.AveragePrice = (previousCost + fillCost) / Math.Abs(resultingQuantity);
        }
        else
        {
            ApplyClosingFill(position, signedFill, fillPrice, contractSize);
            if (resultingQuantity == 0m)
            {
                position.AveragePrice = 0m;
            }
            else if (Math.Sign(resultingQuantity) != Math.Sign(previousQuantity))
            {
                position.AveragePrice = fillPrice;
            }
        }

        position.NetQuantity = resultingQuantity;
    }

    private void ApplyClosingFill(PositionState position, decimal signedFill, decimal fillPrice, decimal contractSize)
    {
        var closedQuantity = Math.Min(Math.Abs(position.NetQuantity), Math.Abs(signedFill));
        var pricePnl = position.NetQuantity > 0m
            ? fillPrice - position.AveragePrice
            : position.AveragePrice - fillPrice;
        var realized = pricePnl * closedQuantity * contractSize;

        RealizedPnl += realized;
        Balance += realized;
    }
}
