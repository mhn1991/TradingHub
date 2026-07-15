using Brokers.Models;
using PortfolioManager.Risk;

namespace Simulator.Financing;

public sealed record FinancingRate
{
    /// <summary>Annual percentage points charged/credited for long positions.</summary>
    public decimal LongAnnualPercent { get; init; }
    /// <summary>Annual percentage points charged/credited for short positions.</summary>
    public decimal ShortAnnualPercent { get; init; }
}

public sealed record FinancingOptions
{
    public bool Enabled { get; init; }
    public IReadOnlyDictionary<string, FinancingRate> InstrumentRates { get; init; } =
        new Dictionary<string, FinancingRate>(StringComparer.OrdinalIgnoreCase);
    public string BrokerTimeZoneId { get; init; } = "America/New_York";
    public TimeOnly RolloverLocalTime { get; init; } = new(17, 0);
    public DayOfWeek TripleFinancingDay { get; init; } = DayOfWeek.Wednesday;
    // Keep the persisted configuration on a System.Text.Json constructible
    // collection contract.  IReadOnlySet<T> serializes, but cannot be
    // deserialized by the file-backed job repository.
    public IReadOnlyList<DateOnly> Holidays { get; init; } = [];
    public string ModelVersion { get; init; } = "configured-synthetic-v1";

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(InstrumentRates);
        ArgumentNullException.ThrowIfNull(Holidays);
        if (string.IsNullOrWhiteSpace(BrokerTimeZoneId) || string.IsNullOrWhiteSpace(ModelVersion) ||
            !Enum.IsDefined(TripleFinancingDay) ||
            InstrumentRates.Any(item => string.IsNullOrWhiteSpace(item.Key)))
            throw new ArgumentException("Financing configuration is invalid.");
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(BrokerTimeZoneId); }
        catch (TimeZoneNotFoundException exception)
        { throw new ArgumentException("The broker financing timezone is unknown.", exception); }
    }
}

public sealed record FinancingContext
{
    public required decimal QuoteToAccountCurrencyRate { get; init; }
    public required DateOnly RolloverDate { get; init; }
    public required int RolloverDayCount { get; init; }
    public required string AccountCurrency { get; init; }
}

public sealed record FinancingCharge
{
    public required decimal AmountAccountCurrency { get; init; }
    public required int DayCount { get; init; }
    public required string ModelVersion { get; init; }
    public required bool IsSynthetic { get; init; }
    public required string Explanation { get; init; }
}

public interface IFinancingModel
{
    FinancingCharge Calculate(
        PortfolioPositionLot position,
        DateTimeOffset from,
        DateTimeOffset to,
        FinancingContext context);
}

public sealed class ConfiguredFinancingModel : IFinancingModel
{
    private readonly FinancingOptions _options;

    public ConfiguredFinancingModel(FinancingOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public FinancingCharge Calculate(
        PortfolioPositionLot position,
        DateTimeOffset from,
        DateTimeOffset to,
        FinancingContext context)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(context);
        if (to <= from || context.RolloverDayCount < 1 || context.QuoteToAccountCurrencyRate <= 0m)
            throw new ArgumentOutOfRangeException(nameof(context));
        if (!_options.InstrumentRates.TryGetValue(position.Instrument.Value, out FinancingRate? rate))
            throw new InvalidOperationException($"Financing rate unavailable for {position.Instrument}.");

        decimal annualPercent = position.Side == OrderSide.Buy
            ? rate.LongAnnualPercent
            : rate.ShortAnnualPercent;
        decimal notional = position.Quantity * position.CurrentPrice * position.ContractMultiplier *
            context.QuoteToAccountCurrencyRate;
        decimal amount = notional * annualPercent / 100m / 365m * context.RolloverDayCount;
        return new FinancingCharge
        {
            AmountAccountCurrency = amount,
            DayCount = context.RolloverDayCount,
            ModelVersion = _options.ModelVersion,
            IsSynthetic = true,
            Explanation = $"Synthetic configured {context.RolloverDayCount}-day financing at {annualPercent:F4}% annual."
        };
    }
}
