namespace ChartAnnotator.Regime;

/// <summary>
/// Shared research defaults for spread-to-ATR safety gates. Keeping regime classification and
/// trading-condition admission on the same constants prevents one subsystem from silently using
/// the previous 0.15/0.30 limits while another uses the relaxed 0.25/0.50 configuration.
/// </summary>
public static class SpreadAtrSafetyDefaults
{
    public const decimal SoftMaximum = 0.25m;
    public const decimal HardMaximum = 0.50m;
}
