namespace TrendStatistics.Detection;

/// <summary>
/// Configuration for the causal V0 detector. Defaults target completed four-hour candles.
/// </summary>
public sealed record TrendDetectorConfig
{
    public TimeSpan Timeframe { get; init; } = TimeSpan.FromHours(4);

    public int EmaPeriod { get; init; } = 20;

    public int EmaSlopeLookbackBars { get; init; } = 3;

    public int AtrPeriod { get; init; } = 14;

    /// <summary>
    /// Use the ATR known before the signal candle when normalizing displacement and retracement.
    /// This prevents a large impulse bar from raising its own denominator and suppressing the
    /// breakout it is meant to confirm.
    /// </summary>
    public bool UseLaggedAtrForDecisions { get; init; } = true;

    public int ExtremeLookbackBars { get; init; } = 12;

    public int StructureLookbackBars { get; init; } = 3;

    public int CandidateTimeoutBars { get; init; } = 8;

    public int MatureAfterBars { get; init; } = 12;

    public decimal CandidateMinDisplacementAtr { get; init; } = 0.75m;

    public decimal ConfirmationMinDisplacementAtr { get; init; } = 1.25m;

    public decimal ExhaustionRetracementAtr { get; init; } = 0.75m;

    public decimal EndRetracementAtr { get; init; } = 1.25m;

    internal int WarmupBars => Math.Max(
        Math.Max(EmaPeriod + EmaSlopeLookbackBars, AtrPeriod),
        Math.Max(ExtremeLookbackBars + 1, StructureLookbackBars + 1));

    public void Validate()
    {
        if (Timeframe <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(Timeframe));
        }

        ValidatePositive(EmaPeriod, nameof(EmaPeriod));
        ValidatePositive(EmaSlopeLookbackBars, nameof(EmaSlopeLookbackBars));
        ValidatePositive(AtrPeriod, nameof(AtrPeriod));
        ValidatePositive(ExtremeLookbackBars, nameof(ExtremeLookbackBars));
        ValidatePositive(StructureLookbackBars, nameof(StructureLookbackBars));
        ValidatePositive(CandidateTimeoutBars, nameof(CandidateTimeoutBars));
        ValidatePositive(MatureAfterBars, nameof(MatureAfterBars));
        ValidatePositive(CandidateMinDisplacementAtr, nameof(CandidateMinDisplacementAtr));
        ValidatePositive(ConfirmationMinDisplacementAtr, nameof(ConfirmationMinDisplacementAtr));
        ValidatePositive(ExhaustionRetracementAtr, nameof(ExhaustionRetracementAtr));
        ValidatePositive(EndRetracementAtr, nameof(EndRetracementAtr));

        if (ConfirmationMinDisplacementAtr < CandidateMinDisplacementAtr)
        {
            throw new ArgumentException(
                "Confirmation displacement must be at least the candidate displacement.",
                nameof(ConfirmationMinDisplacementAtr));
        }

        if (EndRetracementAtr < ExhaustionRetracementAtr)
        {
            throw new ArgumentException(
                "End retracement must be at least the exhaustion retracement.",
                nameof(EndRetracementAtr));
        }
    }

    private static void ValidatePositive(int value, string name)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }

    private static void ValidatePositive(decimal value, string name)
    {
        if (value <= 0m)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }
}
