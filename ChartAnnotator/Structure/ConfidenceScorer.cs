using Brokers.Models;
using ChartAnnotator.Models;

namespace ChartAnnotator.Structure;

public sealed class ConfidenceScorer
{
    public ConfidenceScore Calculate(
        Candle candle,
        IndicatorSnapshot indicators,
        IReadOnlyList<PriceZone> zones,
        IReadOnlyList<Trendline> trendlines,
        IReadOnlyList<PriceChannel> channels,
        MarketStructureSnapshot? structure = null)
    {
        ArgumentNullException.ThrowIfNull(candle);
        ArgumentNullException.ThrowIfNull(indicators);
        ArgumentNullException.ThrowIfNull(zones);
        ArgumentNullException.ThrowIfNull(trendlines);
        ArgumentNullException.ThrowIfNull(channels);

        structure ??= MarketStructureSnapshot.Empty;
        var contributions = new List<ConfidenceContribution>();
        decimal close = candle.Prices.Close;
        DateTimeOffset at = candle.CloseTime ?? candle.OpenTime;

        if (indicators.Rsi is decimal rsi)
        {
            if (rsi is >= 45m and <= 65m)
            {
                contributions.Add(new ConfidenceContribution(
                    "RSI balance",
                    8m,
                    $"RSI {rsi:F1} is not at an extreme."));
            }
            else if (rsi is < 20m or > 80m)
            {
                contributions.Add(new ConfidenceContribution(
                    "RSI extreme",
                    -8m,
                    $"RSI {rsi:F1} is at an extreme."));
            }
        }

        RsiRelationshipSnapshot? relationship =
            indicators.RsiAnalysis.LatestRelationship;
        if (relationship is not null && relationship.AgeCandles <= 10)
        {
            decimal score = Math.Min(8m, relationship.Strength / 12.5m);
            contributions.Add(new ConfidenceContribution(
                relationship.IsDivergence
                    ? "RSI divergence"
                    : "RSI convergence",
                score,
                $"{relationship.Type} was confirmed " +
                $"{relationship.AgeCandles} candles ago with strength " +
                $"{relationship.Strength:F1}."));
        }

        BollingerAnalysisSnapshot bollinger = indicators.BollingerAnalysis;
        if (bollinger.SqueezeReleased)
        {
            contributions.Add(new ConfidenceContribution(
                "Bollinger squeeze release",
                6m,
                "Bollinger bandwidth is expanding after a squeeze."));
        }
        else if (bollinger.IsSqueeze)
        {
            contributions.Add(new ConfidenceContribution(
                "Bollinger squeeze",
                4m,
                $"Bandwidth is in the {bollinger.WidthPercentile:F1}th " +
                "historical percentile."));
        }
        else if (bollinger.IsExpansion)
        {
            contributions.Add(new ConfidenceContribution(
                "Bollinger expansion",
                3m,
                "Bollinger bandwidth is wide and still expanding."));
        }

        AtrAnalysisSnapshot atrContext = indicators.AtrAnalysis;
        if (atrContext.Regime == AtrVolatilityRegime.Normal)
        {
            contributions.Add(new ConfidenceContribution(
                "ATR regime",
                2m,
                "Normalized ATR is in its normal recent range."));
        }
        else if (atrContext.Regime == AtrVolatilityRegime.VeryHigh)
        {
            contributions.Add(new ConfidenceContribution(
                "ATR regime",
                -6m,
                "Normalized ATR is extremely high relative to recent history."));
        }
        else if (atrContext.Regime == AtrVolatilityRegime.VeryLow)
        {
            contributions.Add(new ConfidenceContribution(
                "ATR regime",
                -3m,
                "Normalized ATR is extremely low relative to recent history."));
        }

        PriceChannel? bestChannel = channels
            .OrderByDescending(channel => channel.Confidence)
            .ThenByDescending(channel => channel.EndTime)
            .FirstOrDefault();

        if (indicators.Atr is decimal atr && atr > 0m)
        {
            PriceZone? nearestZone = zones
                .OrderBy(zone => DistanceToZone(close, zone))
                .ThenByDescending(zone => zone.Strength)
                .FirstOrDefault();
            if (nearestZone is not null)
            {
                decimal distanceAtr = DistanceToZone(close, nearestZone) / atr;
                if (distanceAtr <= 0.5m)
                {
                    contributions.Add(new ConfidenceContribution(
                        "Nearby price zone",
                        Math.Min(15m, nearestZone.Strength / 5m),
                        $"Price is {distanceAtr:F2} ATR from a " +
                        $"{nearestZone.Type} zone."));
                }
            }

            // A high-fit line is relevant only when its current projection is near
            // price. Selecting the globally best historical line inflated scores for
            // stale structures far away from the latest candle.
            TrendlineContext? nearestRelevantLine = trendlines
                .Select(line => new TrendlineContext(
                    line,
                    Math.Abs(line.PriceAt(at) - close) / atr))
                .Where(item => item.DistanceAtr <= 1m)
                .OrderByDescending(item =>
                    item.Line.FitScore - item.DistanceAtr * 20m)
                .ThenByDescending(item => item.Line.EndTime)
                .FirstOrDefault();
            if (nearestRelevantLine is not null)
            {
                decimal lineScore = bestChannel is null
                    ? nearestRelevantLine.Line.FitScore / 10m
                    : nearestRelevantLine.Line.FitScore / 20m;
                contributions.Add(new ConfidenceContribution(
                    "Relevant trendline",
                    Math.Min(10m, lineScore),
                    $"A {nearestRelevantLine.Line.Type} trendline is " +
                    $"{nearestRelevantLine.DistanceAtr:F2} ATR from price with " +
                    $"fit {nearestRelevantLine.Line.FitScore:F1}."));
            }
        }

        if (bestChannel is not null)
        {
            contributions.Add(new ConfidenceContribution(
                "Price channel",
                bestChannel.Confidence / 10m,
                $"A {bestChannel.Direction} channel has " +
                $"{bestChannel.Confidence:F1} confidence."));

            decimal alignmentScore = ChannelMatchesStructure(
                bestChannel.Direction,
                structure.Direction)
                ? 4m
                : ChannelOpposesStructure(
                    bestChannel.Direction,
                    structure.Direction)
                    ? -6m
                    : 0m;
            if (alignmentScore != 0m)
            {
                contributions.Add(new ConfidenceContribution(
                    "Channel/structure alignment",
                    alignmentScore,
                    alignmentScore > 0m
                        ? "The active channel agrees with market structure."
                        : "The active channel opposes market structure."));
            }
        }

        if (structure.Direction is not MarketStructureDirection.Unknown)
        {
            decimal structureScore =
                structure.Direction == MarketStructureDirection.Sideways
                    ? 2m
                    : Math.Min(12m, structure.Strength / 8m);
            contributions.Add(new ConfidenceContribution(
                "Market structure",
                structureScore,
                $"Market structure is {structure.Direction} with strength " +
                $"{structure.Strength:F1}."));
        }

        if (structure.Break is not MarketStructureBreak.None)
        {
            contributions.Add(new ConfidenceContribution(
                "Structure break",
                -12m,
                $"A {structure.Break} break of structure was detected."));
        }

        decimal total = Math.Clamp(
            50m + contributions.Sum(item => item.Score),
            0m,
            100m);
        return new ConfidenceScore
        {
            Total = total,
            Contributions = contributions
        };
    }

    private static decimal DistanceToZone(decimal price, PriceZone zone)
    {
        if (price < zone.LowerPrice)
        {
            return zone.LowerPrice - price;
        }

        if (price > zone.UpperPrice)
        {
            return price - zone.UpperPrice;
        }

        return 0m;
    }

    private static bool ChannelMatchesStructure(
        ChannelDirection channel,
        MarketStructureDirection structure) =>
        channel switch
        {
            ChannelDirection.Rising =>
                structure == MarketStructureDirection.Rising,
            ChannelDirection.Falling =>
                structure == MarketStructureDirection.Falling,
            ChannelDirection.Sideways =>
                structure == MarketStructureDirection.Sideways,
            _ => false
        };

    private static bool ChannelOpposesStructure(
        ChannelDirection channel,
        MarketStructureDirection structure) =>
        channel == ChannelDirection.Rising &&
        structure == MarketStructureDirection.Falling ||
        channel == ChannelDirection.Falling &&
        structure == MarketStructureDirection.Rising;

    private sealed record TrendlineContext(
        Trendline Line,
        decimal DistanceAtr);
}
