namespace QuantResearch.Training.Pipeline;

/// <summary>
/// Computes the leakage-safe training window that sits strictly before a simulation
/// evaluation window: train on past history, leave an embargo/gap, then evaluate.
/// Default product shape: ~2 calendar months of training ending 10 days before <c>From</c>.
/// </summary>
public static class PreRunCalibrationPlanner
{
    public const int DefaultTrainMonths = 2;
    public const int DefaultEmbargoDays = 10;

    public static PreRunCalibrationWindow Resolve(
        DateTimeOffset evaluationFrom,
        int trainMonths = DefaultTrainMonths,
        int embargoDays = DefaultEmbargoDays)
    {
        if (trainMonths < 1)
            throw new ArgumentOutOfRangeException(nameof(trainMonths), "TrainMonths must be >= 1.");
        if (embargoDays is < 0 or > 3650)
            throw new ArgumentOutOfRangeException(nameof(embargoDays), "EmbargoDays must be from 0 to 3650.");
        if (trainMonths > 120)
            throw new ArgumentOutOfRangeException(nameof(trainMonths), "TrainMonths must be <= 120.");

        DateTimeOffset eval = evaluationFrom.ToUniversalTime();
        DateTimeOffset trainTo;
        DateTimeOffset trainFrom;
        try
        {
            trainTo = eval.AddDays(-embargoDays);
            trainFrom = trainTo.AddMonths(-trainMonths);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new ArgumentException(
                "Auto-calibration training window is out of range for the evaluation From date. " +
                "Reduce train months or embargo days.",
                exception);
        }

        if (trainFrom >= trainTo)
        {
            throw new ArgumentException(
                "Auto-calibration training window is empty. Increase train months or reduce embargo days " +
                $"relative to evaluation From={eval:yyyy-MM-dd}.");
        }

        return new PreRunCalibrationWindow(trainFrom, trainTo, embargoDays, trainMonths);
    }
}

public readonly record struct PreRunCalibrationWindow(
    DateTimeOffset TrainFrom,
    DateTimeOffset TrainTo,
    int EmbargoDays,
    int TrainMonths);
