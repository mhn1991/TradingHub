using Brokers.Abstractions;
using Brokers.Models;

namespace Brokers.Reconciliation;

public sealed record ExpectedPosition
{
    public required InstrumentKey Instrument { get; init; }
    public required OrderSide Side { get; init; }
    public required decimal Quantity { get; init; }
    public string? PositionId { get; init; }
}

public enum PositionMismatchType
{
    MissingAtBroker,
    UnexpectedAtBroker,
    SideMismatch,
    QuantityMismatch,
    DuplicateBrokerPosition
}

public sealed record PositionMismatch
{
    public required PositionMismatchType Type { get; init; }
    public required InstrumentKey Instrument { get; init; }
    public required string Message { get; init; }
    public ExpectedPosition? Expected { get; init; }
    public BrokerPosition? Actual { get; init; }
}

public sealed record PositionReconciliationResult
{
    public required bool IsConsistent { get; init; }
    public required IReadOnlyList<PositionMismatch> Mismatches { get; init; }
}

public sealed class PositionReconciler
{
    private readonly decimal _quantityTolerance;

    public PositionReconciler(decimal quantityTolerance = 0.00000001m)
    {
        if (quantityTolerance < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantityTolerance));
        }

        _quantityTolerance = quantityTolerance;
    }

    public async Task<PositionReconciliationResult> ReconcileAsync(
        IReadOnlyList<ExpectedPosition> expected,
        ITradingBrokerClient broker,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(broker);
        IReadOnlyList<BrokerPosition> actual = await broker.Positions
            .GetOpenPositionsAsync(cancellationToken)
            .ConfigureAwait(false);
        return Reconcile(expected, actual);
    }

    public PositionReconciliationResult Reconcile(
        IReadOnlyList<ExpectedPosition> expected,
        IReadOnlyList<BrokerPosition> actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        var mismatches = new List<PositionMismatch>();
        Dictionary<InstrumentKey, ExpectedPosition> expectedByInstrument = expected
            .GroupBy(position => position.Instrument)
            .ToDictionary(
                group => group.Key,
                group => AggregateExpected(group));
        Dictionary<InstrumentKey, BrokerPosition[]> actualByInstrument = actual
            .GroupBy(position => position.Instrument)
            .ToDictionary(group => group.Key, group => group.ToArray());

        foreach ((InstrumentKey instrument, ExpectedPosition expectedPosition) in expectedByInstrument)
        {
            if (!actualByInstrument.TryGetValue(instrument, out BrokerPosition[]? brokerPositions))
            {
                mismatches.Add(new PositionMismatch
                {
                    Type = PositionMismatchType.MissingAtBroker,
                    Instrument = instrument,
                    Expected = expectedPosition,
                    Message = $"Expected {expectedPosition.Side} {expectedPosition.Quantity} for " +
                        $"{instrument}, but the broker reported no position."
                });
                continue;
            }

            if (brokerPositions.Length > 1)
            {
                mismatches.Add(new PositionMismatch
                {
                    Type = PositionMismatchType.DuplicateBrokerPosition,
                    Instrument = instrument,
                    Expected = expectedPosition,
                    Actual = brokerPositions[0],
                    Message = $"The broker reported {brokerPositions.Length} positions for {instrument}."
                });
            }

            BrokerPosition aggregate = AggregateActual(instrument, brokerPositions);
            if (aggregate.Side != expectedPosition.Side)
            {
                mismatches.Add(new PositionMismatch
                {
                    Type = PositionMismatchType.SideMismatch,
                    Instrument = instrument,
                    Expected = expectedPosition,
                    Actual = aggregate,
                    Message = $"Expected {expectedPosition.Side} for {instrument}, but the broker " +
                        $"reported {aggregate.Side}."
                });
            }
            else if (Math.Abs(aggregate.Quantity - expectedPosition.Quantity) > _quantityTolerance)
            {
                mismatches.Add(new PositionMismatch
                {
                    Type = PositionMismatchType.QuantityMismatch,
                    Instrument = instrument,
                    Expected = expectedPosition,
                    Actual = aggregate,
                    Message = $"Expected quantity {expectedPosition.Quantity} for {instrument}, but the " +
                        $"broker reported {aggregate.Quantity}."
                });
            }
        }

        foreach ((InstrumentKey instrument, BrokerPosition[] brokerPositions) in actualByInstrument)
        {
            if (expectedByInstrument.ContainsKey(instrument))
            {
                continue;
            }

            BrokerPosition aggregate = AggregateActual(instrument, brokerPositions);
            mismatches.Add(new PositionMismatch
            {
                Type = PositionMismatchType.UnexpectedAtBroker,
                Instrument = instrument,
                Actual = aggregate,
                Message = $"The broker reported an unexpected {aggregate.Side} position of " +
                    $"{aggregate.Quantity} for {instrument}."
            });
        }

        return new PositionReconciliationResult
        {
            IsConsistent = mismatches.Count == 0,
            Mismatches = mismatches
        };
    }

    private static ExpectedPosition AggregateExpected(IEnumerable<ExpectedPosition> positions)
    {
        ExpectedPosition[] items = positions.ToArray();
        decimal signed = items.Sum(position => position.Side switch
        {
            OrderSide.Buy => position.Quantity,
            OrderSide.Sell => -position.Quantity,
            _ => 0m
        });
        return new ExpectedPosition
        {
            Instrument = items[0].Instrument,
            Side = signed >= 0m ? OrderSide.Buy : OrderSide.Sell,
            Quantity = Math.Abs(signed),
            PositionId = items.Length == 1 ? items[0].PositionId : null
        };
    }

    private static BrokerPosition AggregateActual(
        InstrumentKey instrument,
        IReadOnlyList<BrokerPosition> positions)
    {
        decimal signed = positions.Sum(position => position.Side switch
        {
            OrderSide.Buy => position.Quantity,
            OrderSide.Sell => -position.Quantity,
            _ => 0m
        });
        return new BrokerPosition
        {
            PositionId = positions.Count == 1 ? positions[0].PositionId : $"aggregate:{instrument}",
            Instrument = instrument,
            Side = signed >= 0m ? OrderSide.Buy : OrderSide.Sell,
            Quantity = Math.Abs(signed),
            AveragePrice = WeightedAveragePrice(positions),
            UnrealizedProfitLoss = positions.Sum(position => position.UnrealizedProfitLoss ?? 0m),
            Currency = positions.Select(position => position.Currency).FirstOrDefault(value => value is not null)
        };
    }

    private static decimal? WeightedAveragePrice(IReadOnlyList<BrokerPosition> positions)
    {
        decimal quantity = positions
            .Where(position => position.AveragePrice is not null)
            .Sum(position => position.Quantity);
        if (quantity <= 0m)
        {
            return null;
        }

        return positions
            .Where(position => position.AveragePrice is not null)
            .Sum(position => position.AveragePrice!.Value * position.Quantity) / quantity;
    }
}
