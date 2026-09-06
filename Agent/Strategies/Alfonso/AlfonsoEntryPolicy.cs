namespace Agent.Strategies.Alfonso;

/// <summary>Separate experiment arms; reversal entries are never silently added to the core matrix.</summary>
public enum AlfonsoEntryPolicy
{
    Core,
    LowerTimeframeReversal
}
