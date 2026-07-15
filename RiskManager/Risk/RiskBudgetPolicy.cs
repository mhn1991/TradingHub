namespace RiskManager;

public sealed record RiskMultiplierBand
{
    public required decimal FromInclusive { get; init; }
    public required decimal? ToExclusive { get; init; }
    public required decimal Multiplier { get; init; }
}

public sealed record AdaptiveRiskOptions
{
    public bool Enabled { get; init; }
    public IReadOnlyList<RiskMultiplierBand> DrawdownSchedule { get; init; } =
    [
        new() { FromInclusive = 0m, ToExclusive = 2m, Multiplier = 1m },
        new() { FromInclusive = 2m, ToExclusive = 4m, Multiplier = 0.75m },
        new() { FromInclusive = 4m, ToExclusive = 6m, Multiplier = 0.50m },
        new() { FromInclusive = 6m, ToExclusive = null, Multiplier = 0m }
    ];
    public IReadOnlyList<RiskMultiplierBand> VolatilityPercentileSchedule { get; init; } =
    [
        new() { FromInclusive = 0m, ToExclusive = 80m, Multiplier = 1m },
        new() { FromInclusive = 80m, ToExclusive = 95m, Multiplier = 0.70m },
        new() { FromInclusive = 95m, ToExclusive = null, Multiplier = 0.30m }
    ];
    public decimal MissingVolatilityMultiplier { get; init; } = 0.70m;
    public decimal MinimumCombinedRiskMultiplier { get; init; }
    public decimal MaximumCombinedRiskMultiplier { get; init; } = 1m;

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(DrawdownSchedule);
        ArgumentNullException.ThrowIfNull(VolatilityPercentileSchedule);
        ValidateSchedule(DrawdownSchedule, nameof(DrawdownSchedule));
        ValidateSchedule(VolatilityPercentileSchedule, nameof(VolatilityPercentileSchedule));
        if (MissingVolatilityMultiplier is < 0m or > 1m ||
            MinimumCombinedRiskMultiplier is < 0m or > 1m ||
            MaximumCombinedRiskMultiplier is < 0m or > 1m ||
            MinimumCombinedRiskMultiplier > MaximumCombinedRiskMultiplier)
            throw new ArgumentOutOfRangeException(nameof(AdaptiveRiskOptions));
    }

    private static void ValidateSchedule(IReadOnlyList<RiskMultiplierBand> schedule, string name)
    {
        if (schedule.Count == 0) throw new ArgumentException("A risk schedule cannot be empty.", name);
        decimal expectedFrom = schedule[0].FromInclusive;
        foreach (RiskMultiplierBand band in schedule)
        {
            if (band.FromInclusive != expectedFrom || band.Multiplier is < 0m or > 1m ||
                band.ToExclusive is decimal to && to <= band.FromInclusive)
                throw new ArgumentException("Risk bands must be ordered, contiguous, and capped at one.", name);
            expectedFrom = band.ToExclusive ?? expectedFrom;
        }
    }
}

public sealed record RiskBudgetContext
{
    public required decimal AccountEquity { get; init; }
    public decimal DrawdownPercent { get; init; }
    public decimal? VolatilityPercentile { get; init; }
    public decimal RegimeMultiplier { get; init; } = 1m;
    public decimal LiquidityMultiplier { get; init; } = 1m;
    public decimal CorrelationMultiplier { get; init; } = 1m;
    public decimal StrategyAllocationMultiplier { get; init; } = 1m;
    public decimal EquityProtectionMultiplier { get; init; } = 1m;
    public decimal CalibrationMultiplier { get; init; } = 1m;
    public decimal MetaLabelMultiplier { get; init; } = 1m;
}

public sealed record RiskBudgetDecision
{
    public required decimal DrawdownMultiplier { get; init; }
    public required decimal VolatilityMultiplier { get; init; }
    public required decimal RegimeMultiplier { get; init; }
    public required decimal LiquidityMultiplier { get; init; }
    public required decimal CorrelationMultiplier { get; init; }
    public required decimal StrategyAllocationMultiplier { get; init; }
    public required decimal EquityProtectionMultiplier { get; init; }
    public required decimal CalibrationMultiplier { get; init; }
    public required decimal MetaLabelMultiplier { get; init; }
    public required decimal CombinedMultiplier { get; init; }
    public required string ReasonCode { get; init; }
    public required string Explanation { get; init; }
}

public interface IRiskBudgetPolicy
{
    RiskBudgetDecision Evaluate(RiskBudgetContext context);
}

public sealed class RiskBudgetPolicy : IRiskBudgetPolicy
{
    private readonly AdaptiveRiskOptions _options;

    public RiskBudgetPolicy(AdaptiveRiskOptions? options = null)
    {
        _options = options ?? new AdaptiveRiskOptions();
        _options.Validate();
    }

    public RiskBudgetDecision Evaluate(RiskBudgetContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        decimal drawdown = _options.Enabled ? Resolve(_options.DrawdownSchedule, Math.Max(0m, context.DrawdownPercent)) : 1m;
        decimal volatility = _options.Enabled
            ? context.VolatilityPercentile is decimal percentile
                ? Resolve(_options.VolatilityPercentileSchedule, Math.Clamp(percentile, 0m, 100m))
                : _options.MissingVolatilityMultiplier
            : 1m;
        decimal regime = Cap(context.RegimeMultiplier);
        decimal liquidity = Cap(context.LiquidityMultiplier);
        decimal correlation = Cap(context.CorrelationMultiplier);
        decimal allocation = Cap(context.StrategyAllocationMultiplier);
        decimal equity = Cap(context.EquityProtectionMultiplier);
        decimal calibration = Cap(context.CalibrationMultiplier);
        decimal metaLabel = Cap(context.MetaLabelMultiplier);
        decimal raw = drawdown * volatility * regime * liquidity * correlation * allocation * equity * calibration * metaLabel;
        decimal combined = Math.Clamp(raw, _options.MinimumCombinedRiskMultiplier, _options.MaximumCombinedRiskMultiplier);
        return new RiskBudgetDecision
        {
            DrawdownMultiplier = drawdown,
            VolatilityMultiplier = volatility,
            RegimeMultiplier = regime,
            LiquidityMultiplier = liquidity,
            CorrelationMultiplier = correlation,
            StrategyAllocationMultiplier = allocation,
            EquityProtectionMultiplier = equity,
            CalibrationMultiplier = calibration,
            MetaLabelMultiplier = metaLabel,
            CombinedMultiplier = combined,
            ReasonCode = combined <= 0m ? "RiskBudgetRejected" : combined < 1m ? "RiskBudgetAdjusted" : "BaseRiskBudget",
            Explanation = $"risk multipliers: drawdown={drawdown:F3}, volatility={volatility:F3}, " +
                $"regime={regime:F3}, liquidity={liquidity:F3}, correlation={correlation:F3}, " +
                $"allocation={allocation:F3}, equity={equity:F3}, calibration={calibration:F3}, " +
                $"metaLabel={metaLabel:F3}; combined={combined:F3}."
        };
    }

    private static decimal Resolve(IReadOnlyList<RiskMultiplierBand> schedule, decimal value) =>
        schedule.FirstOrDefault(band => value >= band.FromInclusive &&
            (band.ToExclusive is null || value < band.ToExclusive.Value))?.Multiplier ?? schedule[^1].Multiplier;

    private static decimal Cap(decimal value) => Math.Clamp(value, 0m, 1m);
}
