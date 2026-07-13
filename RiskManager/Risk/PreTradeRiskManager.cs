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
        MaximumLossPercentageOfBalance = 0.5m,
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
    public decimal? MaximumLossPercentageOfBalance { get; init; }
}

public sealed record PreTradeRiskContext
{
    public required AgentDecision Decision { get; init; }
    public required decimal Quantity { get; init; }
    public required IReadOnlyList<AccountSnapshot> Accounts { get; init; }
    public required IReadOnlyList<BrokerPosition> Positions { get; init; }
    public TradingSafetySnapshot? Safety { get; init; }
}

public sealed record RiskAssessment
{
    public required bool Approved { get; init; }
    public required IReadOnlyList<string> Reasons { get; init; }
    public decimal? EstimatedLossAtStop { get; init; }
    public decimal? RewardRiskRatio { get; init; }

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

        AgentDecision decision = context.Decision;
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
                 _options.MaximumLossPercentageOfBalance is not null)
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

        decimal? estimatedLoss = riskDistance is null
            ? null
            : EstimateLoss(riskDistance.Value, context.Quantity, decision.QuantityUnit);
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
                decimal percentage = loss / balance.Value * 100m;
                if (percentage > maximumLossPercentage)
                {
                    reasons.Add(
                        $"Estimated risk {percentage:F3}% exceeds the configured maximum of " +
                        $"{maximumLossPercentage:F3}% of balance.");
                }
            }
        }

        return new RiskAssessment
        {
            Approved = reasons.Count == 0,
            Reasons = reasons,
            EstimatedLossAtStop = estimatedLoss,
            RewardRiskRatio = rewardRiskRatio
        };
    }

    private static decimal? ResolveReferencePrice(AgentDecision decision) =>
        decision.ReferencePrice ??
        decision.LimitPrice ??
        decision.StopPrice;

    private static decimal? EstimateLoss(
        decimal riskDistance,
        decimal quantity,
        QuantityUnit quantityUnit) => quantityUnit switch
        {
            QuantityUnit.Units or QuantityUnit.BaseAsset or QuantityUnit.Contracts =>
                riskDistance * quantity,
            _ => null
        };

    private static void Validate(PreTradeRiskOptions options)
    {
        if (options.MinimumConfidence is < 0m or > 100m ||
            options.MinimumRewardRiskRatio is <= 0m ||
            options.MaximumAbsolutePositionQuantity is <= 0m ||
            options.MaximumOpenPositions is <= 0 ||
            options.MaximumLossPerTrade is <= 0m ||
            options.MaximumLossPercentageOfBalance is <= 0m or > 100m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Risk thresholds must be positive and percentages must be from 0 to 100.");
        }
    }
}
