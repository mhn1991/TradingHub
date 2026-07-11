using TradingHub.Domain.Markets;
using TradingHub.Domain.Trading;

namespace TradingHub.Simulation.Broker;

internal sealed class SimulatedAccount
{
    private readonly string _accountId;
    private readonly string _currency;
    private readonly Dictionary<string, SimulatedPosition> _positions = new(StringComparer.Ordinal);

    public SimulatedAccount(SimulatedBrokerOptions options)
    {
        _accountId = options.AccountId;
        _currency = options.AccountCurrency;
        Balance = options.InitialBalance;
    }

    public decimal Balance { get; private set; }

    public void ApplyFill(ExecutionReport report, InstrumentDefinition instrument)
    {
        if (report.LastFillQuantity <= 0m || report.LastFillPrice is null)
        {
            return;
        }

        var position = GetOrCreate(report.InstrumentId);
        var signedFill = report.Side == OrderSide.Buy
            ? report.LastFillQuantity
            : -report.LastFillQuantity;
        var previousQuantity = position.Quantity;
        var resultingQuantity = previousQuantity + signedFill;

        if (previousQuantity == 0m || Math.Sign(previousQuantity) == Math.Sign(signedFill))
        {
            var totalCost = Math.Abs(previousQuantity) * position.AveragePrice
                + Math.Abs(signedFill) * report.LastFillPrice.Value;
            position.AveragePrice = totalCost / Math.Abs(resultingQuantity);
        }
        else
        {
            RealizeClosingPnl(position, signedFill, report.LastFillPrice.Value, instrument.ContractSize);
            if (resultingQuantity == 0m)
            {
                position.AveragePrice = 0m;
            }
            else if (Math.Sign(resultingQuantity) != Math.Sign(previousQuantity))
            {
                position.AveragePrice = report.LastFillPrice.Value;
            }
        }

        position.Quantity = resultingQuantity;
        Balance -= report.Fee;
    }

    public BrokerAccountSnapshot CreateSnapshot(DateTimeOffset observedAt)
    {
        return new BrokerAccountSnapshot
        {
            AccountId = _accountId,
            ObservedAt = observedAt,
            Balance = Balance,
            Equity = Balance,
            CashBalances =
            [
                new CashBalance { Asset = _currency, Available = Balance, Reserved = 0m }
            ],
            Positions = _positions.Values.Select(position => new BrokerPositionSnapshot
            {
                InstrumentId = position.InstrumentId,
                NetQuantity = position.Quantity,
                AveragePrice = position.AveragePrice
            }).ToArray()
        };
    }

    private SimulatedPosition GetOrCreate(string instrumentId)
    {
        if (!_positions.TryGetValue(instrumentId, out var position))
        {
            position = new SimulatedPosition(instrumentId);
            _positions.Add(instrumentId, position);
        }

        return position;
    }

    private void RealizeClosingPnl(
        SimulatedPosition position,
        decimal signedFill,
        decimal fillPrice,
        decimal contractSize)
    {
        var closedQuantity = Math.Min(Math.Abs(position.Quantity), Math.Abs(signedFill));
        var pricePnl = position.Quantity > 0m
            ? fillPrice - position.AveragePrice
            : position.AveragePrice - fillPrice;
        Balance += pricePnl * closedQuantity * contractSize;
    }

    private sealed class SimulatedPosition(string instrumentId)
    {
        public string InstrumentId { get; } = instrumentId;

        public decimal Quantity { get; set; }

        public decimal AveragePrice { get; set; }
    }
}
