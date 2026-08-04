namespace TradeManager;

/// <summary>
/// Shared entry-time volatility-bucket classification, used by both the simulator (which already
/// computed this correctly per trade) and live trading (which previously hardcoded a literal
/// "Live" string instead of computing a real bucket - see CAL-01: that string can never match a
/// calibration cohort key like "Low"/"Normal"/"High", so live calibrated management silently and
/// permanently fell back to the static default whenever calibration was enabled). Extracted here
/// so the two paths can never independently drift on the bucket boundaries again.
/// </summary>
public static class VolatilityBucketClassifier
{
    public static string Classify(decimal? atrPercentile) => atrPercentile switch
    {
        null => "Unknown",
        < 20m => "VeryLow",
        < 40m => "Low",
        < 70m => "Normal",
        < 90m => "High",
        _ => "VeryHigh"
    };
}
