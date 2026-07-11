namespace Brokers;

/// <summary>
/// Provider-neutral candle query. The broker adapter translates it to interval/granularity and provider timestamps.
/// </summary>
public sealed class CandleQuery
{
    public const int MaximumLimit = 1000;

    public CandleQuery(
        string instrumentId,
        CandleTimeframe timeframe,
        DateTimeOffset? startTime = null,
        DateTimeOffset? endTime = null,
        int? limit = null,
        bool includeIncomplete = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instrumentId);

        if (!timeframe.IsValid)
        {
            throw new ArgumentException("The timeframe is not initialized.", nameof(timeframe));
        }

        if (startTime is { } start && endTime is { } end && start >= end)
        {
            throw new ArgumentException("The start time must be earlier than the end time.", nameof(startTime));
        }

        if (limit is <= 0 or > MaximumLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                $"The limit must be between 1 and {MaximumLimit}.");
        }

        InstrumentId = instrumentId;
        Timeframe = timeframe;
        StartTime = startTime?.ToUniversalTime();
        EndTime = endTime?.ToUniversalTime();
        Limit = limit;
        IncludeIncomplete = includeIncomplete;
    }

    public string InstrumentId { get; }

    public CandleTimeframe Timeframe { get; }

    /// <summary>Inclusive UTC lower bound.</summary>
    public DateTimeOffset? StartTime { get; }

    /// <summary>
    /// Exclusive UTC upper bound. Provider adapters translate inclusive API parameters when necessary.
    /// </summary>
    public DateTimeOffset? EndTime { get; }

    public int? Limit { get; }

    public bool IncludeIncomplete { get; }
}
