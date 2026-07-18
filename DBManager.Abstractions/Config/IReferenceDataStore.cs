namespace DBManager.Abstractions.Config;

public sealed record RegisterBroker
{
    public required string Code { get; init; }
    public required string DisplayName { get; init; }
}

public sealed record RegisterBrokerAccount
{
    public required Guid BrokerAccountId { get; init; }
    public required string BrokerCode { get; init; }
    public required string ExternalAccountKeyHash { get; init; }
    public required string MaskedAccountName { get; init; }
    public required short Environment { get; init; }
    public required string AccountCurrency { get; init; }
}

public sealed record RegisterInstrument
{
    public required string CanonicalKey { get; init; }
    public required short AssetClass { get; init; }
    public string? BaseCurrency { get; init; }
    public string? QuoteCurrency { get; init; }
    public required string DisplayName { get; init; }
}

/// <summary>
/// Minimal reference-data persistence (section 7.1) needed as FK targets for the config schema.
/// Broker metadata management (versioned <c>broker_instruments</c>, tradeability) is out of Phase 1
/// scope and is added when execution-schema work needs it.
/// </summary>
public interface IReferenceDataStore
{
    Task<DurableResult> RegisterBrokerAsync(RegisterBroker command, CancellationToken cancellationToken);

    Task<DurableResult> RegisterBrokerAccountAsync(
        RegisterBrokerAccount command, CancellationToken cancellationToken);

    Task<long?> RegisterInstrumentAsync(RegisterInstrument command, CancellationToken cancellationToken);
}
