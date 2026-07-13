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
        IReadOnlyList<PriceChannel> channels)
    {
        var contributions = new List<ConfidenceContribution>();

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

        if (indicators.Atr is decimal atr && atr > 0m)
        {
            PriceZone? nearest = zones
                .OrderBy(zone => Math.Abs(zone.CentrePrice - candle.Prices.Close))
                .FirstOrDefault();
            if (nearest is not null)
            {
                decimal distanceAtr = Math.Abs(nearest.CentrePrice - candle.Prices.Close) / atr;
                if (distanceAtr <= 0.5m)
                {
                    contributions.Add(new ConfidenceContribution(
                        "Nearby price zone",
                        Math.Min(15m, nearest.Strength / 5m),
                        $"Price is {distanceAtr:F2} ATR from a {nearest.Type} zone."));
                }
            }
        }

        Trendline? bestLine = trendlines.OrderByDescending(line => line.FitScore).FirstOrDefault();
        if (bestLine is not null)
        {
            contributions.Add(new ConfidenceContribution(
                "Trendline fit",
                bestLine.FitScore / 10m,
                $"Best {bestLine.Type} trendline score is {bestLine.FitScore:F1}."));
        }

        PriceChannel? bestChannel = channels.OrderByDescending(channel => channel.Confidence).FirstOrDefault();
        if (bestChannel is not null)
        {
            contributions.Add(new ConfidenceContribution(
                "Price channel",
                bestChannel.Confidence / 10m,
                $"A {bestChannel.Direction} channel has {bestChannel.Confidence:F1} confidence."));
        }

        decimal total = Math.Clamp(50m + contributions.Sum(item => item.Score), 0m, 100m);
        return new ConfidenceScore
        {
            Total = total,
            Contributions = contributions
        };
    }
}
