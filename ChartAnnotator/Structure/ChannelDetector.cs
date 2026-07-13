using ChartAnnotator.Models;

namespace ChartAnnotator.Structure;

public sealed record ChannelOptions(
    decimal MaximumSlopeDifferenceRatio = 0.25m,
    decimal MinimumWidthAtr = 0.5m,
    decimal MaximumWidthAtr = 20m);

public sealed class ChannelDetector
{
    private readonly ChannelOptions _options;

    public ChannelDetector(ChannelOptions? options = null)
    {
        _options = options ?? new ChannelOptions();
    }

    public IReadOnlyList<PriceChannel> Detect(
        IReadOnlyList<Trendline> trendlines,
        DateTimeOffset at,
        decimal atr)
    {
        if (atr <= 0m)
        {
            return [];
        }

        Trendline[] supports = trendlines.Where(line => line.Type == TrendlineType.Support).ToArray();
        Trendline[] resistances = trendlines.Where(line => line.Type == TrendlineType.Resistance).ToArray();
        var channels = new List<PriceChannel>();

        foreach (Trendline lower in supports)
        {
            foreach (Trendline upper in resistances)
            {
                decimal slopeScale = Math.Max(
                    Math.Max(Math.Abs(lower.SlopePerSecond), Math.Abs(upper.SlopePerSecond)),
                    0.0000000001m);
                decimal slopeDifference = Math.Abs(lower.SlopePerSecond - upper.SlopePerSecond) / slopeScale;
                if (slopeDifference > _options.MaximumSlopeDifferenceRatio)
                {
                    continue;
                }

                decimal width = upper.PriceAt(at) - lower.PriceAt(at);
                decimal widthAtr = width / atr;
                if (width <= 0m || widthAtr < _options.MinimumWidthAtr || widthAtr > _options.MaximumWidthAtr)
                {
                    continue;
                }

                decimal averageSlope = (lower.SlopePerSecond + upper.SlopePerSecond) / 2m;
                decimal confidence = Math.Clamp(
                    ((lower.FitScore + upper.FitScore) / 2m) - slopeDifference * 30m,
                    0m,
                    100m);

                channels.Add(new PriceChannel
                {
                    LowerLine = lower,
                    UpperLine = upper,
                    Direction = averageSlope > 0m
                        ? ChannelDirection.Rising
                        : averageSlope < 0m
                            ? ChannelDirection.Falling
                            : ChannelDirection.Sideways,
                    Width = width,
                    WidthAtr = widthAtr,
                    Confidence = confidence
                });
            }
        }

        return channels.OrderByDescending(channel => channel.Confidence).ToArray();
    }
}
