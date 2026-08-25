using Agent.Models;
using RiskManager.Safety;
using Brokers.Models;

namespace RiskManager;

public sealed record PreTradeRiskOptions
{
    public static PreTradeRiskOptions PhaseOneSafeDefaults { get; } = new()
    {
        RequireStopLoss = true,
        MinimumRewardRiskRatio = 1.5m,
        MaximumOpenPositions = 3,
        // Percentage points: 0.5 means half of one percent of balance, not 50%.
        MaximumLossPercentageOfBalance = 0.5m,
        // Three 0.5% trades → 1.5% portfolio heat budget.
        MaximumOpenRiskPercentOfEquity = 1.5m,
        AllowPyramiding = false
    };

    public bool AllowPyramiding { get; init; }
    public bool RequireStopLoss { get; init; }
    public bool RequireTakeProfit { get; init; }
    public decimal MinimumConfidence { get; init; }
    public decimal? MinimumRewardRiskRatio { get; init; }
    public decimal? MaximumAbsolutePositionQuantity { get; init; }
    public int? MaximumOpenPositions { get; init; }
    public decimal? MaximumLossPerTrade { get; init; }

    /// <summary>
    /// Maximum loss for the new trade as percentage points of balance.
    /// Example: 0.5 means half of one percent (loss/balance×100 ≤ 0.5), not 50%.
    /// </summary>
    public decimal? MaximumLossPercentageOfBalance { get; init; }

    /// <summary>
    /// Maximum combined residual open risk (existing positions + this trade) as percentage
    /// points of equity. Example: 1.5 means 1.5% of equity. Null disables the heat check.
    /// </summary>
    public decimal? MaximumOpenRiskPercentOfEquity { get; init; }

    /// <summary>
    /// When open positions lack an explicit residual-risk total, assume each position's stop
    /// distance is this many percentage points of its average price (0.5 = half a percent).
    /// Null disables auto-estimation of open-book risk.
    /// </summary>
    public decimal? AssumedOpenPositionRiskDistancePercentOfPrice { get; init; }
}

public sealed record PreTradeRiskContext
{
    public required AgentDecision Decision { get; init; }
    public required decimal Quantity { get; init; }
    public required IReadOnlyList<AccountSnapshot> Accounts { get; init; }
    public required IReadOnlyList<BrokerPosition> Positions { get; init; }
    /// <summary>Value of one quote-currency unit in the account currency.</summary>
    public decimal QuoteToAccountCurrencyRate { get; init; } = 1m;
    /// <summary>Contract / pip model for the decision instrument. Defaults to unit notional.</summary>
    public InstrumentRiskSpec InstrumentSpec { get; init; } = InstrumentRiskSpec.UnitNotional;
    /// <summary>
    /// Explicit residual open-book risk in account currency (excluding the new trade).
    /// When null, may be auto-estimated from open positions.
    /// </summary>
    public decimal? KnownOpenRiskAccountCurrency { get; init; }
    public IReadOnlyDictionary<InstrumentKey, InstrumentRiskSpec>? InstrumentSpecs { get; init; }
    public IReadOnlyDictionary<InstrumentKey, decimal>? QuoteToAccountRatesByInstrument { get; init; }
    public TradingSafetySnapshot? Safety { get; init; }
}

public sealed record RiskAssessment
{
    public required bool Approved { get; init; }
    public required IReadOnlyList<string> Reasons { get; init; }
    public decimal? EstimatedLossAtStop { get; init; }
    public decimal? RewardRiskRatio { get; init; }
    public decimal? OpenRiskAccountCurrency { get; init; }
    public decimal? ProjectedOpenRiskAccountCurrency { get; init; }

    public string Summary => Approved
        ? "Approved"
        : string.Join(" ", Reasons);
}

public interface IPreTradeRiskManager
{
    RiskAssessment Evaluate(PreTradeRiskContext context);
}

public sealed class PreTradeRiskManager : IPreTradeRiskManager
{
    private readonly PreTradeRiskOptions _options;

    public PreTradeRiskManager(PreTradeRiskOptions? options = null)
    {
        _options = options ?? new PreTradeRiskOptions();
        Validate(_options);
    }

    public RiskAssessment Evaluate(PreTradeRiskContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.Decision);
        ArgumentNullException.ThrowIfNull(context.Accounts);
        ArgumentNullException.ThrowIfNull(context.Positions);
        InstrumentRiskSpec spec = context.InstrumentSpec ?? InstrumentRiskSpec.UnitNotional;
        spec.Validate();

        AgentDecision decision = context.Decision;
        if (decision.Action is not (AgentAction.Buy or AgentAction.Sell))
        {
            return new RiskAssessment
            {
                Approved = true,
                Reasons = [],
                EstimatedLossAtStop = null,
                RewardRiskRatio = null
            };
        }

        var reasons = new List<string>();
        if (context.Safety is { CanOpenNewTrades: false } safety)
        {
            reasons.Add($"Trading safety is {safety.State}: {safety.Message ?? safety.Reason.ToString()}.");
        }

        if (decision.Confidence < _options.MinimumConfidence)
        {
            reasons.Add(
                $"Decision confidence {decision.Confidence:F2} is below the configured minimum " +
                $"of {_options.MinimumConfidence:F2}.");
        }

        BrokerPosition[] openPositions = context.Positions
            .Where(position => position.Quantity > 0m)
            .ToArray();
        if (_options.MaximumOpenPositions is int maximumOpenPositions)
        {
            int distinctOpenPositions = openPositions
                .Select(position => position.Instrument)
                .Distinct()
                .Count();
            bool opensNewInstrument = openPositions.All(position =>
                position.Instrument != decision.Instrument);
            if (opensNewInstrument && distinctOpenPositions >= maximumOpenPositions)
            {
                reasons.Add(
                    $"The maximum of {maximumOpenPositions} open positions has been reached.");
            }
        }

        decimal signedPosition = openPositions
            .Where(position => position.Instrument == decision.Instrument)
            .Sum(position => position.Side switch
            {
                OrderSide.Buy => position.Quantity,
                OrderSide.Sell => -position.Quantity,
                _ => 0m
            });
        decimal signedOrder = decision.Action == AgentAction.Buy
            ? context.Quantity
            : -context.Quantity;

        if (!_options.AllowPyramiding &&
            signedPosition != 0m &&
            Math.Sign(signedPosition) == Math.Sign(signedOrder))
        {
            reasons.Add(
                $"Pyramiding is disabled and a same-direction position already exists for " +
                $"{decision.Instrument}.");
        }

        if (_options.MaximumAbsolutePositionQuantity is decimal maximumPosition)
        {
            if (decision.QuantityUnit is not (QuantityUnit.Units or
                QuantityUnit.BaseAsset or QuantityUnit.Contracts))
            {
                reasons.Add("The configured position cap cannot evaluate this quantity unit.");
            }
            else
            {
                decimal projected = Math.Abs(signedPosition + signedOrder);
                if (projected > maximumPosition)
                {
                    reasons.Add(
                        $"Projected position quantity {projected:F8} exceeds the configured maximum " +
                        $"of {maximumPosition:F8}.");
                }
            }
        }

        if (_options.RequireStopLoss && decision.StopLossPrice is null)
        {
            reasons.Add("A protective stop-loss is required before an order can be sent.");
        }

        if (_options.RequireTakeProfit && decision.TakeProfitPrice is null)
        {
            reasons.Add("A take-profit price is required before an order can be sent.");
        }

        decimal? referencePrice = ResolveReferencePrice(decision);
        decimal? riskDistance = null;
        decimal? rewardDistance = null;
        if (referencePrice is decimal reference)
        {
            if (decision.StopLossPrice is decimal stopLoss)
            {
                bool validStopSide = decision.Action switch
                {
                    AgentAction.Buy => stopLoss < reference,
                    AgentAction.Sell => stopLoss > reference,
                    _ => true
                };
                if (!validStopSide)
                {
                    reasons.Add("The stop-loss is on the wrong side of the reference price.");
                }

                riskDistance = Math.Abs(reference - stopLoss);
                if (riskDistance <= 0m)
                {
                    reasons.Add("The stop-loss distance must be positive.");
                }
            }

            if (decision.TakeProfitPrice is decimal takeProfit)
            {
                bool validTargetSide = decision.Action switch
                {
                    AgentAction.Buy => takeProfit > reference,
                    AgentAction.Sell => takeProfit < reference,
                    _ => true
                };
                if (!validTargetSide)
                {
                    reasons.Add("The take-profit is on the wrong side of the reference price.");
                }

                rewardDistance = Math.Abs(takeProfit - reference);
            }
        }
        else if (_options.MinimumRewardRiskRatio is not null ||
                 _options.MaximumLossPerTrade is not null ||
                 _options.MaximumLossPercentageOfBalance is not null ||
                 _options.MaximumOpenRiskPercentOfEquity is not null)
        {
            reasons.Add(
                "A reference, limit, or stop price is required to evaluate monetary trade risk.");
        }

        decimal? rewardRiskRatio = riskDistance is > 0m && rewardDistance is not null
            ? rewardDistance.Value / riskDistance.Value
            : null;
        if (_options.MinimumRewardRiskRatio is decimal minimumRewardRiskRatio)
        {
            if (rewardRiskRatio is null)
            {
                reasons.Add("Stop-loss and take-profit prices are required to evaluate reward/risk.");
            }
            else if (rewardRiskRatio < minimumRewardRiskRatio)
            {
                reasons.Add(
                    $"Reward/risk {rewardRiskRatio:F2} is below the configured minimum of " +
                    $"{minimumRewardRiskRatio:F2}.");
            }
        }

        bool requiresCurrencyConversion =
            _options.MaximumLossPerTrade is not null ||
            _options.MaximumLossPercentageOfBalance is not null ||
            _options.MaximumOpenRiskPercentOfEquity is not null;

        if (requiresCurrencyConversion && context.QuoteToAccountCurrencyRate <= 0m)
        {
            reasons.Add("A positive quote-to-account-currency conversion rate is required.");
        }

        decimal? estimatedLoss = riskDistance is null || context.QuoteToAccountCurrencyRate <= 0m
            ? null
            : EstimateLoss(
                riskDistance.Value,
                context.Quantity,
                decision.QuantityUnit,
                context.QuoteToAccountCurrencyRate,
                spec);

        if (_options.MaximumLossPerTrade is decimal maximumLossPerTrade &&
            estimatedLoss is decimal estimated &&
            estimated > maximumLossPerTrade)
        {
            reasons.Add(
                $"Estimated stop loss {estimated:F2} exceeds the configured per-trade limit of " +
                $"{maximumLossPerTrade:F2}.");
        }

        if (_options.MaximumLossPercentageOfBalance is decimal maximumLossPercentage &&
            estimatedLoss is decimal loss)
        {
            decimal? balance = context.Accounts
                .Select(account => account.Balance)
                .FirstOrDefault(value => value is > 0m);
            if (balance is null)
            {
                reasons.Add("An account balance is required to enforce percentage risk.");
            }
            else
            {
                // percentage points: (loss/balance)*100 compared to configured points (e.g. 0.5).
                decimal percentage = loss / balance.Value * 100m;
                if (percentage > maximumLossPercentage)
                {
                    reasons.Add(
                        $"Estimated risk {percentage:F3}% exceeds the configured maximum of " +
                        $"{maximumLossPercentage:F3}% of balance.");
                }
            }
        }

        decimal? openRisk = null;
        decimal? projectedOpenRisk = null;
        // A missing conversion rate is already rejected above whenever this cap is configured.
        // Substituting a 1.0 rate here would still report a fabricated OpenRiskAccountCurrency on
        // the assessment (the RSK-01 failure mode), so leave it null instead.
        if (_options.MaximumOpenRiskPercentOfEquity is decimal maximumOpenRiskPercent &&
            context.QuoteToAccountCurrencyRate > 0m)
        {
            openRisk = PortfolioOpenRisk.EstimateAccountCurrency(
                context.Positions,
                spec,
                context.QuoteToAccountCurrencyRate,
                context.KnownOpenRiskAccountCurrency,
                _options.AssumedOpenPositionRiskDistancePercentOfPrice,
                context.InstrumentSpecs,
                context.QuoteToAccountRatesByInstrument);

            if (estimatedLoss is decimal newTradeLoss)
            {
                projectedOpenRisk = openRisk.Value + newTradeLoss;
                AccountSnapshot? account = context.Accounts.FirstOrDefault(item => item.Balance is > 0m);
                if (account?.Balance is not decimal balanceForHeat)
                {
                    reasons.Add("An account balance is required to enforce portfolio open-risk heat.");
                }
                else
                {
                    decimal equity = balanceForHeat + (account.UnrealizedProfitLoss ?? 0m);
                    if (equity <= 0m)
                    {
                        reasons.Add("Account equity must be positive to enforce portfolio open-risk heat.");
                    }
                    else
                    {
                        decimal heatPercent = projectedOpenRisk.Value / equity * 100m;
                        if (heatPercent > maximumOpenRiskPercent)
                        {
                            reasons.Add(
                                $"Projected open risk {projectedOpenRisk.Value:F2} ({heatPercent:F3}% of equity) " +
                                $"exceeds the configured maximum of {maximumOpenRiskPercent:F3}% of equity.");
                        }
                    }
                }
            }
            else if (requiresCurrencyConversion)
            {
                reasons.Add("Unable to evaluate portfolio open-risk heat without a monetary stop-loss estimate.");
            }
        }

        return new RiskAssessment
        {
            Approved = reasons.Count == 0,
            Reasons = reasons,
            EstimatedLossAtStop = estimatedLoss,
            RewardRiskRatio = rewardRiskRatio,
            OpenRiskAccountCurrency = openRisk,
            ProjectedOpenRiskAccountCurrency = projectedOpenRisk
        };
    }

    private static decimal? ResolveReferencePrice(AgentDecision decision) =>
        decision.ReferencePrice ??
        decision.LimitPrice ??
        decision.StopPrice;

    private static decimal? EstimateLoss(
        decimal riskDistance,
        decimal quantity,
        QuantityUnit quantityUnit,
        decimal quoteToAccountCurrencyRate,
        InstrumentRiskSpec spec) => quantityUnit switch
        {
            QuantityUnit.Units or QuantityUnit.BaseAsset or QuantityUnit.Contracts =>
                spec.EstimateStopLossAccountCurrency(riskDistance, quantity, quoteToAccountCurrencyRate),
            _ => null
        };

    private static void Validate(PreTradeRiskOptions options)
    {
        if (options.MinimumConfidence is < 0m or > 100m ||
            options.MinimumRewardRiskRatio is <= 0m ||
            options.MaximumAbsolutePositionQuantity is <= 0m ||
            options.MaximumOpenPositions is <= 0 ||
            options.MaximumLossPerTrade is <= 0m ||
            options.MaximumLossPercentageOfBalance is <= 0m or > 100m ||
            options.MaximumOpenRiskPercentOfEquity is <= 0m or > 100m ||
            options.AssumedOpenPositionRiskDistancePercentOfPrice is <= 0m or > 100m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Risk thresholds must be positive and percentages must be from 0 to 100 " +
                "(percentage-point fields such as MaximumLossPercentageOfBalance use 0.5 for half a percent).");
        }
    }
}
