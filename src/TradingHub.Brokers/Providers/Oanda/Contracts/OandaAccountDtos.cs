namespace TradingHub.Brokers.Oanda.Contracts;

internal sealed record OandaAccountEnvelopeDto
{
    public OandaAccountDto? Account { get; init; }
}

internal sealed record OandaAccountDto
{
    public string? Id { get; init; }

    public string? Currency { get; init; }

    public string? Balance { get; init; }

    public string? Nav { get; init; }

    public string? MarginAvailable { get; init; }
}

internal sealed record OandaErrorDto
{
    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }
}
