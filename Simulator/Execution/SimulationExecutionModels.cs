using Brokers.Models;
using Simulator.Broker;

namespace Simulator.Execution;

public enum SimulationFillModel
{
    MidpointPlusConfiguredSpread,
    HistoricalBidAsk,
    VariableSyntheticSpread,
    StressExecution
}

public enum StressExecutionScenario
{
    Base,
    SpreadDouble,
    SlippageTriple,
    GapStress,
    StopAmendmentFailure,
    ConnectionLoss,
    CorrelationShock,
    CombinedStress
}

public sealed record FillCapacityModel
{
    public decimal? MaximumQuantityPerExecutionFrame { get; init; }
    public decimal MaximumParticipationFraction { get; init; } = 1m;

    public void Validate()
    {
        if (MaximumQuantityPerExecutionFrame is <= 0m || MaximumParticipationFraction is <= 0m or > 1m)
            throw new ArgumentOutOfRangeException(nameof(FillCapacityModel));
    }
}

public sealed record ExecutionModelOptions
{
    public SimulationFillModel FillModel { get; init; } = SimulationFillModel.MidpointPlusConfiguredSpread;
    public decimal AsianSessionSpreadMultiplier { get; init; } = 1.20m;
    public decimal LondonSessionSpreadMultiplier { get; init; } = 1m;
    public decimal NewYorkSessionSpreadMultiplier { get; init; } = 1m;
    public decimal RolloverSpreadMultiplier { get; init; } = 3m;
    public decimal HighVolatilitySpreadMultiplier { get; init; } = 1.50m;
    public decimal EventSpreadMultiplier { get; init; } = 1m;
    public decimal BaseSlippageBasisPoints { get; init; }
    public decimal VolatilitySlippageFraction { get; init; }
    public decimal GapSlippageFraction { get; init; }
    public decimal OrderSizeSlippageFraction { get; init; }
    public FillCapacityModel FillCapacity { get; init; } = new();
    public StressExecutionScenario StressScenario { get; init; } = StressExecutionScenario.Base;
    public int DeterministicSeed { get; init; } = 17;

    public void Validate()
    {
        if (!Enum.IsDefined(FillModel) || !Enum.IsDefined(StressScenario) ||
            FillModel == SimulationFillModel.HistoricalBidAsk ||
            AsianSessionSpreadMultiplier <= 0m || LondonSessionSpreadMultiplier <= 0m ||
            NewYorkSessionSpreadMultiplier <= 0m || RolloverSpreadMultiplier <= 0m ||
            HighVolatilitySpreadMultiplier <= 0m || BaseSlippageBasisPoints < 0m ||
            EventSpreadMultiplier <= 0m || VolatilitySlippageFraction < 0m ||
            GapSlippageFraction < 0m || OrderSizeSlippageFraction < 0m)
            throw new ArgumentOutOfRangeException(nameof(ExecutionModelOptions),
                "HistoricalBidAsk requires a real bid/ask candle source and cannot be selected for midpoint candles.");
        FillCapacity.Validate();
    }
}

public sealed record ExecutionModelContext
{
    public required long Sequence { get; init; }
    public required decimal ConfiguredSpreadBasisPoints { get; init; }
    public required decimal ConfiguredSlippageBasisPoints { get; init; }
    public required decimal OrderQuantity { get; init; }
    public required decimal RemainingQuantity { get; init; }
}

public sealed record FillEvaluation
{
    public required bool ShouldFill { get; init; }
    public bool TriggeredOnly { get; init; }
    public decimal? ExecutablePrice { get; init; }
    public decimal FillQuantity { get; init; }
    public decimal AppliedSpreadPrice { get; init; }
    public decimal AppliedSlippagePrice { get; init; }
    public bool GapThroughStop { get; init; }
    public bool IsPartialFill { get; init; }
    public string? RejectionReason { get; init; }
    public IReadOnlyDictionary<string, decimal> Multipliers { get; init; } =
        new Dictionary<string, decimal>(StringComparer.Ordinal);
}

public interface ISimulationExecutionModel
{
    FillEvaluation Evaluate(SimulatedOrder order, Candle candle, ExecutionModelContext context);
}

public sealed class SimulationExecutionModel : ISimulationExecutionModel
{
    private readonly ExecutionModelOptions _options;

    public SimulationExecutionModel(ExecutionModelOptions? options = null)
    {
        _options = options ?? new ExecutionModelOptions();
        _options.Validate();
    }

    public FillEvaluation Evaluate(SimulatedOrder order, Candle candle, ExecutionModelContext context)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(candle);
        (bool fill, bool trigger, decimal raw, bool gap) = Trigger(order, candle);
        if (!fill)
            return new FillEvaluation { ShouldFill = false, TriggeredOnly = trigger, FillQuantity = 0m };

        bool variableSpread = _options.FillModel is
            SimulationFillModel.VariableSyntheticSpread or SimulationFillModel.StressExecution;
        (decimal session, decimal rollover) = variableSpread
            ? SpreadTimeMultipliers(candle.CloseTime ?? candle.OpenTime)
            : (1m, 1m);
        decimal volatility = variableSpread && candle.Prices.Open > 0m &&
            (candle.Prices.High - candle.Prices.Low) / candle.Prices.Open >= 0.01m
            ? _options.HighVolatilitySpreadMultiplier : 1m;
        decimal stressSpread = _options.StressScenario is StressExecutionScenario.SpreadDouble or StressExecutionScenario.CombinedStress ? 2m : 1m;
        decimal stressSlippage = _options.StressScenario is StressExecutionScenario.SlippageTriple or StressExecutionScenario.CombinedStress ? 3m : 1m;
        if (gap && _options.StressScenario == StressExecutionScenario.GapStress)
            stressSlippage = 3m;
        decimal eventMultiplier = variableSpread ? _options.EventSpreadMultiplier : 1m;
        decimal spreadBps = context.ConfiguredSpreadBasisPoints * session * rollover * volatility *
            eventMultiplier * stressSpread;
        decimal halfSpread = raw * spreadBps / 20_000m;
        decimal range = candle.Prices.High - candle.Prices.Low;
        decimal baseSlippageBps = context.ConfiguredSlippageBasisPoints + _options.BaseSlippageBasisPoints;
        decimal slippage = raw * baseSlippageBps / 10_000m +
            range * _options.VolatilitySlippageFraction +
            (gap ? Math.Abs(candle.Prices.Open - (order.Request.StopPrice ?? candle.Prices.Open)) * _options.GapSlippageFraction : 0m) +
            range * _options.OrderSizeSlippageFraction *
                (context.OrderQuantity <= 0m ? 0m : context.RemainingQuantity / context.OrderQuantity);
        slippage *= stressSlippage;
        decimal adverse = halfSpread + slippage;
        decimal executable = order.Request.Side == OrderSide.Buy ? raw + adverse : raw - adverse;
        if (order.Request.Type is StandardOrderType.Limit or StandardOrderType.StopLimit &&
            order.Request.LimitPrice is decimal limit)
            executable = order.Request.Side == OrderSide.Buy ? Math.Min(executable, limit) : Math.Max(executable, limit);

        decimal capacity = context.OrderQuantity * _options.FillCapacity.MaximumParticipationFraction;
        if (_options.FillCapacity.MaximumQuantityPerExecutionFrame is decimal maximum)
            capacity = Math.Min(capacity, maximum);
        decimal quantity = Math.Min(context.RemainingQuantity, capacity);
        bool partial = quantity < context.RemainingQuantity;
        if (partial && order.Request.TimeInForce == StandardTimeInForce.FillOrKill)
            return new FillEvaluation { ShouldFill = false, FillQuantity = 0m, RejectionReason = "FillOrKill capacity was insufficient." };

        return new FillEvaluation
        {
            ShouldFill = quantity > 0m,
            ExecutablePrice = executable,
            FillQuantity = quantity,
            AppliedSpreadPrice = halfSpread,
            AppliedSlippagePrice = slippage,
            GapThroughStop = gap,
            IsPartialFill = partial,
            Multipliers = new Dictionary<string, decimal>(StringComparer.Ordinal)
            {
                ["session"] = session,
                ["rollover"] = rollover,
                ["volatility"] = volatility,
                ["event"] = eventMultiplier,
                ["stressSpread"] = stressSpread,
                ["stressSlippage"] = stressSlippage
            }
        };
    }

    private (bool Fill, bool Trigger, decimal Raw, bool Gap) Trigger(SimulatedOrder order, Candle candle)
    {
        decimal open = candle.Prices.Open, high = candle.Prices.High, low = candle.Prices.Low;
        bool buy = order.Request.Side == OrderSide.Buy;
        return order.Request.Type switch
        {
            StandardOrderType.Market => (true, false, open, false),
            StandardOrderType.Limit when buy && low <= order.Request.LimitPrice!.Value =>
                (true, false, open <= order.Request.LimitPrice.Value ? open : order.Request.LimitPrice.Value, false),
            StandardOrderType.Limit when !buy && high >= order.Request.LimitPrice!.Value =>
                (true, false, open >= order.Request.LimitPrice.Value ? open : order.Request.LimitPrice.Value, false),
            StandardOrderType.Stop when buy && high >= order.Request.StopPrice!.Value =>
                (true, false, open >= order.Request.StopPrice.Value ? open : order.Request.StopPrice.Value,
                    open > order.Request.StopPrice.Value),
            StandardOrderType.Stop when !buy && low <= order.Request.StopPrice!.Value =>
                (true, false, open <= order.Request.StopPrice.Value ? open : order.Request.StopPrice.Value,
                    open < order.Request.StopPrice.Value),
            StandardOrderType.StopLimit when !order.IsTriggered &&
                (buy ? high >= order.Request.StopPrice!.Value : low <= order.Request.StopPrice!.Value) =>
                (false, true, 0m, false),
            StandardOrderType.StopLimit when order.IsTriggered && buy && low <= order.Request.LimitPrice!.Value =>
                (true, false, open <= order.Request.LimitPrice.Value ? open : order.Request.LimitPrice.Value, false),
            StandardOrderType.StopLimit when order.IsTriggered && !buy && high >= order.Request.LimitPrice!.Value =>
                (true, false, open >= order.Request.LimitPrice.Value ? open : order.Request.LimitPrice.Value, false),
            _ => (false, false, 0m, false)
        };
    }

    private (decimal Session, decimal Rollover) SpreadTimeMultipliers(DateTimeOffset timestamp)
    {
        int hour = timestamp.UtcDateTime.Hour;
        if (hour is >= 21 and < 23) return (1m, _options.RolloverSpreadMultiplier);
        if (hour is >= 7 and < 16) return (_options.LondonSessionSpreadMultiplier, 1m);
        if (hour is >= 12 and < 21) return (_options.NewYorkSessionSpreadMultiplier, 1m);
        return (_options.AsianSessionSpreadMultiplier, 1m);
    }
}
