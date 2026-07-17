namespace LiveTrading.Oanda;

public sealed record LiveBrokerCapabilityMatrix
{
    public bool MultiInstrumentPricingStream { get; init; }
    public bool TransactionStream { get; init; }
    public bool ClientOrderIds { get; init; }
    public bool AtomicStopOnFill { get; init; }
    public bool AtomicTakeProfitOnFill { get; init; }
    public bool SafeProtectiveStopReplacement { get; init; }
    public bool PartialClose { get; init; }
    public bool ReduceOnlyClose { get; init; }
    public bool QueryByClientOrderId { get; init; }
    public bool ProtectiveStopReplacementPracticeCertified { get; init; }
    public bool PartialClosePracticeCertified { get; init; }

    public static LiveBrokerCapabilityMatrix OandaCurrent { get; } = new()
    {
        MultiInstrumentPricingStream = true,
        TransactionStream = true,
        ClientOrderIds = true,
        AtomicStopOnFill = true,
        AtomicTakeProfitOnFill = true,
        SafeProtectiveStopReplacement = true,
        PartialClose = true,
        ReduceOnlyClose = true,
        QueryByClientOrderId = false,
        ProtectiveStopReplacementPracticeCertified = false,
        PartialClosePracticeCertified = false
    };
}
